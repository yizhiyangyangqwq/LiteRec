using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;

namespace LiteRec
{
    internal enum OutputFormat
    {
        Mp4 = 0,
        Avi = 1
    }

    internal sealed class RecorderOptions
    {
        public Rectangle Source;
        public int Fps = 30;
        public int BitrateKbps = 8000;
        public int JpegQuality = 80;
        public bool DrawCursor = true;
        /// <summary>光标放大倍数（1.0 = 原始大小）。用于宣传片/教程录制。</summary>
        public double CursorScale = 1.0;
        /// <summary>可选：.cur 光标文件路径，用其中高分辨率帧替代系统光标。</summary>
        public string CursorImagePath;
        public OutputFormat Format = OutputFormat.Mp4;
        public string OutputPath;
        /// <summary>是否录制系统声音（WASAPI 环路捕获）。仅 MP4 生效。</summary>
        public bool RecordAudio = false;
    }

    /// <summary>
    /// 录制引擎：后台线程定速抓帧 + 编码 + 写盘。
    /// 采用"固定节拍 + 丢帧补偿"策略，避免编码慢时雪崩。
    /// </summary>
    internal sealed class Recorder : IDisposable
    {
        private readonly RecorderOptions _opt;
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _running;
        private volatile bool _abandoned;   // Start 超时后标记，Run 看到就立刻退出不再录制
        private volatile string _error;
        private string _fallbackReason;
        private long _framesWritten;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);

        // 用于 UI 显示的实时统计
        private long _statFrames;
        private long _statBytes;
        private readonly Stopwatch _clock = new Stopwatch();

        public Recorder(RecorderOptions opt)
        {
            _opt = opt;
        }

        public bool IsRunning { get { return _running; } }
        public string LastError { get { return _error; } }
        public long FramesWritten { get { return Interlocked.Read(ref _framesWritten); } }
        public TimeSpan Elapsed { get { return _clock.Elapsed; } }

        private string _actualPath;
        public string ActualPath { get { return _actualPath; } }

        /// <summary>若从 MP4 回退到 AVI，这里给出原因；否则为 null。</summary>
        public string FallbackReason { get { return _fallbackReason; } }

        private AudioCapture _audioRef;
        /// <summary>是否真的在录声音（MP4 且音频流创建成功）。</summary>
        public bool AudioActive
        {
            get
            {
                AudioCapture a = _audioRef;
                return a != null && a.Error == null;
            }
        }
        /// <summary>音频采集错误信息（null 表示正常）。</summary>
        public string AudioError
        {
            get { AudioCapture a = _audioRef; return a != null ? a.Error : null; }
        }

        public long OutputBytes
        {
            get { return Interlocked.Read(ref _statBytes); }
        }

        public double CurrentFps
        {
            get
            {
                double s = _clock.Elapsed.TotalSeconds;
                if (s < 0.5) return 0;
                return Interlocked.Read(ref _statFrames) / s;
            }
        }

        /// <summary>启动录制，失败时抛出异常。</summary>
        public void Start()
        {
            if (_running || _thread != null) return;

            _stop = false;
            _error = null;
            _abandoned = false;
            _ready.Reset();

            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Name = "LiteRec.Capture";
            _thread.Start();

            // 等后台线程完成初始化（打开编码器），把初始化错误同步抛给 UI。
            // H.264 编码器冷启动可能较慢（要枚举/加载硬件 MFT），给足时间；
            // 超时视为失败，不能让 UI 以为"已在录制"而实际没画面。
            if (!_ready.WaitOne(30000))
            {
                // 超时：通知后台线程放弃本次启动。可能它正卡在某个 COM 调用里，
                // 无法立即退出，但至少不会在 UI 报错后继续偷偷写文件。
                _stop = true;
                _abandoned = true;
                _thread = null;
                throw new TimeoutException("编码器初始化超时（30 秒）");
            }
            if (_error != null)
            {
                throw new InvalidOperationException(_error);
            }
        }

        public void Stop()
        {
            if (!_running && _thread == null) return;
            _stop = true;
            Thread t = _thread;
            if (t != null)
            {
                if (!t.Join(6000))
                {
                    // 编码器卡死时不强杀，交给 GC
                }
            }
            _thread = null;
            _running = false;
        }

        private void Run()
        {
            // MF 需要 MTA + MFStartup，且必须在本线程完成
            bool mfStarted = false;
            bool comInit = false;
            ScreenCapture cap = null;
            AviWriter avi = null;
            Mp4Writer mp4 = null;
            MemoryStream jpegBuf = null;
            AudioCapture audio = null;

            try
            {
                int hr = MF.CoInitializeEx(IntPtr.Zero, MF.COINIT_MULTITHREADED);
                comInit = true; // S_FALSE(1) 也算成功

                cap = new ScreenCapture(_opt.Source.X, _opt.Source.Y,
                    _opt.Source.Width, _opt.Source.Height, _opt.DrawCursor,
                    _opt.CursorScale, _opt.CursorImagePath);

                _actualPath = _opt.OutputPath;

                if (_opt.Format == OutputFormat.Mp4)
                {
                    MF.Check(MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_FULL), "MFStartup");
                    mfStarted = true;
                    int bitrate = _opt.BitrateKbps * 1000;
                    // 音频只在 MP4 下可用；AVI 容器没有音轨
                    bool withAudio = _opt.RecordAudio;
                    try
                    {
                        mp4 = new Mp4Writer(_opt.OutputPath, cap.Width, cap.Height,
                            _opt.Fps, bitrate, 1, withAudio);
                    }
                    catch (Exception exMp4)
                    {
                        // 硬件/软件 H.264 编码器不可用时，自动回退到零依赖的 AVI 后端，
                        // 而不是让用户什么都录不到。
                        _fallbackReason = exMp4.Message;
                        string aviPath = System.IO.Path.ChangeExtension(_opt.OutputPath, ".avi");
                        try { if (System.IO.File.Exists(_opt.OutputPath))
                                System.IO.File.Delete(_opt.OutputPath); }
                        catch { }
                        avi = new AviWriter(aviPath, cap.Width, cap.Height, _opt.Fps);
                        jpegBuf = new MemoryStream(1 << 20);
                        _actualPath = aviPath;
                    }

                    // 启动音频采集（仅当 MP4 且未回退到 AVI）
                    if (withAudio && mp4 != null && mp4.HasAudio)
                    {
                        audio = new AudioCapture();
                        _audioRef = audio;
                        audio.Start(delegate(byte[] pcm, int count)
                        {
                            Mp4Writer w = mp4;
                            if (w != null) w.EnqueueAudio(pcm, count);
                        });
                        // 等音频真正就绪：若失败，AudioError 会在录制开始前就确定，
                        // UI 可立刻提示"没录到声音"，而不是等录完才发现是静音。
                        audio.WaitReady(8000);
                    }
                }
                else
                {
                    avi = new AviWriter(_opt.OutputPath, cap.Width, cap.Height, _opt.Fps);
                    jpegBuf = new MemoryStream(1 << 20);
                }

                // Start 已超时放弃：不能开始录制（否则 UI 报错但后台偷录）
                if (_abandoned) return;

                _clock.Start();
                _running = true;
                _ready.Set();

                Gdi.timeBeginPeriod(1);

                long frameIndex = 0;
                long frameTicks = Stopwatch.Frequency / _opt.Fps;
                long startTicks = Stopwatch.GetTimestamp();

                while (!_stop)
                {
                    // 目标时间点
                    long deadline = startTicks + frameIndex * frameTicks;
                    long now = Stopwatch.GetTimestamp();
                    if (now < deadline)
                    {
                        long waitMs = (deadline - now) * 1000 / Stopwatch.Frequency;
                        if (waitMs > 0)
                        {
                            Thread.Sleep((int)Math.Min(waitMs, 100));
                        }
                        continue;
                    }

                    cap.Grab();

                    // 每帧把采集线程攒的 PCM 写成音频 sample（同一线程，MF 要求）
                    if (mp4 != null) mp4.PumpAudio();

                    // 用真实经过时间做时间轴基准：掉帧时时间戳继续前进，
                    // 成品时长才等于实际录制时长（否则视频会加速播放）。
                    long nowTicks = Stopwatch.GetTimestamp();
                    long elapsedTicks = nowTicks - startTicks;
                    long hns = elapsedTicks * 10000000L / Stopwatch.Frequency;
                    long hnsFrame = 10000000L / _opt.Fps;

                    if (avi != null)
                    {
                        int n = cap.EncodeJpeg(jpegBuf, _opt.JpegQuality);
                        avi.WriteFrame(jpegBuf.GetBuffer(), n, elapsedTicks, Stopwatch.Frequency);
                        Interlocked.Add(ref _statBytes, n);
                    }
                    else
                    {
                        mp4.WriteFrameFrom(cap.Bits, hns, hnsFrame);
                    }

                    frameIndex++;
                    Interlocked.Increment(ref _framesWritten);
                    Interlocked.Increment(ref _statFrames);

                    // 严重落后（>2 秒）时丢弃时间轴追帧，防止雪崩
                    long after = Stopwatch.GetTimestamp();
                    if ((after - deadline) * 1000 / Stopwatch.Frequency > 2000)
                    {
                        frameIndex = (after - startTicks) / frameTicks + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                if (!_ready.WaitOne(0))
                {
                    _ready.Set();
                }
            }
            finally
            {
                Gdi.timeEndPeriod(1);
                _running = false;
                _clock.Stop();

                // 先停采集线程，再把队列里剩余的音频全部写进文件
                try { if (audio != null) { audio.Stop(); audio.Dispose(); } }
                catch { }
                try { if (mp4 != null) mp4.FlushAudio(); }
                catch { }
                try { if (mp4 != null) mp4.Finish(); }
                catch { }
                try { if (avi != null) { avi.Finish(); } }
                catch { }
                try { if (avi != null) avi.Dispose(); }
                catch { }
                try { if (mp4 != null) mp4.Dispose(); }
                catch { }
                try { if (cap != null) cap.Dispose(); }
                catch { }
                try { if (jpegBuf != null) jpegBuf.Dispose(); }
                catch { }

                if (mfStarted)
                {
                    try { MF.MFShutdown(); } catch { }
                }
                if (comInit)
                {
                    try { MF.CoUninitialize(); } catch { }
                }

                _ready.Set();
            }
        }

        public void Dispose()
        {
            Stop();
            // 不要 Close(_ready)：Run 线程可能在 Stop 返回后才走到 finally 里的
            // _ready.Set()，此时句柄已关闭会抛 ObjectDisposedException。
            // ManualResetEvent 由 GC 回收即可。
        }
    }
}
