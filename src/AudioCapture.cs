using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiteRec
{
    /// <summary>
    /// 系统声音采集（WASAPI 环路捕获）—— 纯裸 vtable 版。
    ///
    /// 【最重要的坑，实测定位】
    /// IAudioCaptureClient 的 vtable 顺序按 audioclient.h 的**声明顺序**：
    ///     slot3 = GetBuffer        (5 个参数)
    ///     slot4 = ReleaseBuffer    (1 个参数)
    ///     slot5 = GetNextPacketSize(1 个参数)
    /// 不是"调用顺序"（GNPS→GB→RB）。按调用顺序排会错一位：
    /// 用 1 参数调 GetBuffer 会破坏调用栈 → AccessViolationException。
    /// 同理 IAudioClient：3=Initialize 8=GetMixFormat 10=Start 11=Stop 14=GetService。
    ///
    /// 另外：所有 GetNextPacketSize / GetBuffer / ReleaseBuffer 必须在同一线程，
    /// 且 Release 与 GetService 同线程。所以整个生命周期放在一个专用线程里。
    ///
    /// 输出 PCM16（float32 转换后），通过 DataReady 回调交给写入端。
    /// </summary>
    internal sealed class AudioCapture : IDisposable
    {
        // ---------------- 裸 vtable 委托 ----------------
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_Release(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_GetDefaultEndpoint(IntPtr self, int flow, int role, IntPtr pp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_Activate(IntPtr self, ref Guid iid, int ctx, IntPtr a, IntPtr pp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_GetMixFormat(IntPtr self, IntPtr pp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_Initialize(IntPtr self, int mode, uint flags, long dur, long per,
            IntPtr fmt, IntPtr g);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_NoArgs(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_GetService(IntPtr self, ref Guid iid, IntPtr pp);
        // IAudioCaptureClient（声明顺序）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_GetBuffer(IntPtr self, IntPtr ppData, IntPtr frames, IntPtr flags,
            IntPtr dev, IntPtr qpc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_ReleaseBuffer(IntPtr self, uint frames);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_GetNextPacketSize(IntPtr self, IntPtr pOut);

        [DllImport("ole32.dll")]
        static extern int CoInitializeEx(IntPtr p, int c);
        [DllImport("ole32.dll")]
        static extern void CoUninitialize();
        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid c, IntPtr o, int x, ref Guid i, out IntPtr p);

        const int CLSCTX_ALL = 23;
        const uint LOOPBACK = 0x00020000;
        const uint SILENT = 0x2;

        static T Slot<T>(IntPtr p, int i) where T : class
        {
            IntPtr vt = Marshal.ReadIntPtr(p);
            IntPtr fn = Marshal.ReadIntPtr(vt, i * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }
        static string H(int hr) { return "0x" + hr.ToString("X8"); }

        // ---------------- 对外 ----------------
        public delegate void DataHandler(byte[] pcm16, int byteCount);

        private DataHandler _onData;
        private Thread _thread;
        private volatile bool _stop;
        private volatile string _error;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);

        public string Error { get { return _error; } }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }

        /// <summary>等待音频初始化完成。返回 false 表示启动失败（原因见 Error）。</summary>
        public bool WaitReady(int ms)
        {
            if (!_ready.WaitOne(ms)) { _error = "音频初始化超时"; return false; }
            return _error == null;
        }

        public void Start(DataHandler onData)
        {
            if (_thread != null) return;
            _onData = onData;
            _stop = false;
            _error = null;
            _ready.Reset();
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "LiteRec.Audio";
            // WASAPI 采集对象要求 STA，且所有方法必须同线程
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            Thread t = _thread;
            if (t != null && !t.Join(3000)) { /* 不强杀 */ }
            _thread = null;
        }

        private void Run()
        {
            int co = CoInitializeEx(IntPtr.Zero, 2); // STA
            IntPtr pEnum = IntPtr.Zero, pDevice = IntPtr.Zero,
                   pCli = IntPtr.Zero, pCap = IntPtr.Zero;
            bool started = false;
            D_NoArgs start = null, stop = null;
            D_GetBuffer gb = null;
            D_ReleaseBuffer rb = null;
            D_GetNextPacketSize gnps = null;
            D_Release rel = null;

            try
            {
                Guid clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
                Guid iidEnum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iidEnum, out pEnum);
                if (hr < 0) { _error = "枚举器创建失败 " + H(hr); return; }

                rel = Slot<D_Release>(pEnum, 2);
                var gdef = Slot<D_GetDefaultEndpoint>(pEnum, 4);
                GC.KeepAlive(rel); GC.KeepAlive(gdef);

                IntPtr h = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(h, IntPtr.Zero);
                hr = gdef(pEnum, 0, 1, h);
                pDevice = Marshal.ReadIntPtr(h);
                Marshal.FreeHGlobal(h);
                if (hr < 0 || pDevice == IntPtr.Zero) { _error = "取默认播放设备失败 " + H(hr); return; }

                var act = Slot<D_Activate>(pDevice, 3);
                Guid iidCli = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
                h = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(h, IntPtr.Zero);
                hr = act(pDevice, ref iidCli, CLSCTX_ALL, IntPtr.Zero, h);
                pCli = Marshal.ReadIntPtr(h);
                Marshal.FreeHGlobal(h);
                if (hr < 0 || pCli == IntPtr.Zero) { _error = "激活音频设备失败 " + H(hr); return; }

                var init = Slot<D_Initialize>(pCli, 3);
                var gmf = Slot<D_GetMixFormat>(pCli, 8);
                start = Slot<D_NoArgs>(pCli, 10);
                stop = Slot<D_NoArgs>(pCli, 11);
                var gsvc = Slot<D_GetService>(pCli, 14);
                GC.KeepAlive(init); GC.KeepAlive(gmf);
                GC.KeepAlive(start); GC.KeepAlive(stop); GC.KeepAlive(gsvc);

                h = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(h, IntPtr.Zero);
                hr = gmf(pCli, h);
                IntPtr pFmt = Marshal.ReadIntPtr(h);
                Marshal.FreeHGlobal(h);
                if (hr < 0 || pFmt == IntPtr.Zero) { _error = "取音频格式失败 " + H(hr); return; }

                Channels = Marshal.ReadInt16(pFmt, 2);
                SampleRate = Marshal.ReadInt32(pFmt, 4);
                ushort bits = (ushort)Marshal.ReadInt16(pFmt, 14);
                // 只处理 float32（WAVE_FORMAT_EXTENSIBLE / IEEE float）
                bool isFloat = bits == 32;

                hr = init(pCli, 0, LOOPBACK, 10000000L, 0, pFmt, IntPtr.Zero);
                if (hr < 0) { _error = "环路初始化失败 " + H(hr); return; }

                Guid iidCap = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
                h = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(h, IntPtr.Zero);
                hr = gsvc(pCli, ref iidCap, h);
                pCap = Marshal.ReadIntPtr(h);
                Marshal.FreeHGlobal(h);
                if (hr < 0 || pCap == IntPtr.Zero) { _error = "取采集接口失败 " + H(hr); return; }

                // ★ 声明顺序：3=GetBuffer 4=ReleaseBuffer 5=GetNextPacketSize
                gb = Slot<D_GetBuffer>(pCap, 3);
                rb = Slot<D_ReleaseBuffer>(pCap, 4);
                gnps = Slot<D_GetNextPacketSize>(pCap, 5);
                GC.KeepAlive(gb); GC.KeepAlive(rb); GC.KeepAlive(gnps);

                hr = start(pCli);
                if (hr < 0) { _error = "启动音频采集失败 " + H(hr); return; }
                started = true;
                _ready.Set();   // 通知录制线程：音频已就绪，可以开始写视频

                IntPtr pNext = Marshal.AllocHGlobal(8);
                IntPtr ppData = Marshal.AllocHGlobal(IntPtr.Size);
                IntPtr pFrames = Marshal.AllocHGlobal(8);
                IntPtr pFlags = Marshal.AllocHGlobal(8);
                byte[] pcm = new byte[8192 * Math.Max(2, Channels) * 2];
                // 复用 float 中转缓冲，避免每个音频包都 new 一个数组制造 GC 压力
                float[] fbuf = new float[8192 * Math.Max(2, Channels)];

                try
                {
                    while (!_stop)
                    {
                        Marshal.WriteInt32(pNext, 0);
                        hr = gnps(pCap, pNext);
                        if (hr < 0) { System.Threading.Thread.Sleep(8); continue; }
                        uint next = (uint)Marshal.ReadInt32(pNext);
                        if (next == 0) { System.Threading.Thread.Sleep(4); continue; }

                        Marshal.WriteIntPtr(ppData, IntPtr.Zero);
                        Marshal.WriteInt32(pFrames, 0);
                        Marshal.WriteInt32(pFlags, 0);
                        hr = gb(pCap, ppData, pFrames, pFlags, IntPtr.Zero, IntPtr.Zero);
                        if (hr < 0) { System.Threading.Thread.Sleep(8); continue; }

                        uint frames = (uint)Marshal.ReadInt32(pFrames);
                        uint flags = (uint)Marshal.ReadInt32(pFlags);
                        IntPtr pData = Marshal.ReadIntPtr(ppData);

                        if (frames > 0)
                        {
                            int samples = (int)frames * Channels;
                            int byteCount = samples * 2;
                            if (pcm.Length < byteCount) pcm = new byte[byteCount];

                            if ((flags & SILENT) != 0 || pData == IntPtr.Zero)
                            {
                                Array.Clear(pcm, 0, byteCount);
                            }
                            else if (isFloat)
                            {
                                if (fbuf.Length < samples) fbuf = new float[samples];
                                Marshal.Copy(pData, fbuf, 0, samples);
                                for (int i = 0; i < samples; i++)
                                {
                                    float v = fbuf[i];
                                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                                    short s = (short)(v * 32767f);
                                    pcm[i * 2] = (byte)(s & 0xFF);
                                    pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                                }
                            }
                            else
                            {
                                // 16bit PCM 直通
                                Marshal.Copy(pData, pcm, 0, byteCount);
                            }

                            DataHandler cb = _onData;
                            if (cb != null) cb(pcm, byteCount);

                            rb(pCap, frames);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pNext); Marshal.FreeHGlobal(ppData);
                    Marshal.FreeHGlobal(pFrames); Marshal.FreeHGlobal(pFlags);
                }
            }
            catch (Exception ex)
            {
                _error = "音频线程异常 " + ex.GetType().Name + " " + ex.Message;
            }
            finally
            {
                if (started && stop != null) { try { stop(pCli); } catch { } }
                if (rel != null)
                {
                    if (pCap != IntPtr.Zero) { try { rel(pCap); } catch { } }
                    if (pCli != IntPtr.Zero) { try { rel(pCli); } catch { } }
                    if (pDevice != IntPtr.Zero) { try { rel(pDevice); } catch { } }
                    if (pEnum != IntPtr.Zero) { try { rel(pEnum); } catch { } }
                    GC.KeepAlive(rel);
                }
                if (co >= 0) CoUninitialize();
                // 兜底：任何失败路径都要唤醒 WaitReady，否则会白等超时
                _ready.Set();
            }
        }

        public void Dispose() { Stop(); }
    }
}
