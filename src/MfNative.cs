using System;
using System.Runtime.InteropServices;

namespace LiteRec
{
    /// <summary>
    /// Media Foundation 原生 vtable 访问层。
    ///
    /// 为什么不用 COM interop cast：
    /// 本机实测 MFCreateSample 返回的对象对 IMFSample 做 QueryInterface 会返回
    /// E_NOINTERFACE (0x80004002)，但按 vtable 直接调用其方法全部返回 S_OK。
    /// 因此对 sample / buffer 一律走裸 vtable，绕开 QI 依赖。
    ///
    /// vtable 布局（已用时间值往返实测确认）：
    ///   IUnknown            0..2   (QI, AddRef, Release)
    ///   IMFAttributes       3..32  (30 个方法)
    ///   IMFSample          33 GetSampleFlags, 34 SetSampleFlags,
    ///                      35 GetSampleTime,  36 SetSampleTime,
    ///                      37 GetSampleDuration, 38 SetSampleDuration,
    ///                      39 GetBufferCount, 40 GetBufferByIndex,
    ///                      41 ConvertToContiguousBuffer, 42 AddBuffer,
    ///                      43 RemoveBufferByIndex, 44 RemoveAllBuffers,
    ///                      45 GetTotalLength, 46 CopyToBuffer
    ///   IMFMediaBuffer      3 Lock, 4 Unlock, 5 GetCurrentLength,
    ///                       6 SetCurrentLength, 7 GetMaxLength
    /// </summary>
    internal static class MfVt
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int D_Release(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int D_BufLock(IntPtr self, out IntPtr ppb, out int max, out int cur);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int D_BufUnlock(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int D_BufSetLength(IntPtr self, int len);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int D_SampleSetTime(IntPtr self, long hns);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int D_SampleSetDuration(IntPtr self, long hns);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int D_SampleAddBuffer(IntPtr self, IntPtr buf);

        internal static Delegate Slot(IntPtr p, int index, Type t)
        {
            IntPtr vtbl = Marshal.ReadIntPtr(p);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, t);
        }

        internal static void Release(IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            try
            {
                D_Release r = (D_Release)Slot(p, 2, typeof(D_Release));
                r(p);
            }
            catch { }
        }
    }

    /// <summary>IMFMediaBuffer 的托管包装（裸 vtable）。</summary>
    internal sealed class MfBuffer : IDisposable
    {
        private IntPtr _p;
        private MfVt.D_BufLock _lock;
        private MfVt.D_BufUnlock _unlock;
        private MfVt.D_BufSetLength _setLen;

        public IntPtr Ptr { get { return _p; } }

        public MfBuffer(IntPtr p)
        {
            _p = p;
            _lock = (MfVt.D_BufLock)MfVt.Slot(p, 3, typeof(MfVt.D_BufLock));
            _unlock = (MfVt.D_BufUnlock)MfVt.Slot(p, 4, typeof(MfVt.D_BufUnlock));
            _setLen = (MfVt.D_BufSetLength)MfVt.Slot(p, 6, typeof(MfVt.D_BufSetLength));
        }

        /// <summary>把托管数组写入缓冲并设置长度。</summary>
        public void Write(byte[] data, int count)
        {
            IntPtr dst;
            int max, cur;
            MF.Check(_lock(_p, out dst, out max, out cur), "IMFMediaBuffer.Lock");
            if (count > max) count = max;
            Marshal.Copy(data, 0, dst, count);
            _unlock(_p);
            MF.Check(_setLen(_p, count), "IMFMediaBuffer.SetCurrentLength");
        }

        /// <summary>
        /// 从原生内存直接拷贝进缓冲。全程零托管分配，
        /// 是高帧率大分辨率的性能关键路径。
        /// </summary>
        public void WriteFrom(IntPtr src, int count)
        {
            IntPtr dst;
            int max, cur;
            MF.Check(_lock(_p, out dst, out max, out cur), "IMFMediaBuffer.Lock");
            if (count > max) count = max;
            Gdi.CopyMemory(dst, src, (IntPtr)count);
            _unlock(_p);
            MF.Check(_setLen(_p, count), "IMFMediaBuffer.SetCurrentLength");
        }

        public void Dispose()
        {
            MfVt.Release(_p);
            _p = IntPtr.Zero;
        }
    }

    /// <summary>IMFSample 的托管包装（裸 vtable）。</summary>
    internal sealed class MfSample : IDisposable
    {
        private IntPtr _p;
        private MfVt.D_SampleSetTime _setTime;
        private MfVt.D_SampleSetDuration _setDur;
        private MfVt.D_SampleAddBuffer _addBuf;

        public IntPtr Ptr { get { return _p; } }

        public MfSample(IntPtr p)
        {
            _p = p;
            _setTime = (MfVt.D_SampleSetTime)MfVt.Slot(p, 36, typeof(MfVt.D_SampleSetTime));
            _setDur = (MfVt.D_SampleSetDuration)MfVt.Slot(p, 38, typeof(MfVt.D_SampleSetDuration));
            _addBuf = (MfVt.D_SampleAddBuffer)MfVt.Slot(p, 42, typeof(MfVt.D_SampleAddBuffer));
        }

        public void SetTime(long hns)
        {
            MF.Check(_setTime(_p, hns), "IMFSample.SetSampleTime");
        }

        public void SetDuration(long hns)
        {
            MF.Check(_setDur(_p, hns), "IMFSample.SetSampleDuration");
        }

        public void AddBuffer(MfBuffer b)
        {
            MF.Check(_addBuf(_p, b.Ptr), "IMFSample.AddBuffer");
        }

        public void Dispose()
        {
            MfVt.Release(_p);
            _p = IntPtr.Zero;
        }
    }
}
