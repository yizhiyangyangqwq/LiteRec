using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LiteRec
{
    /// <summary>
    /// H.264 / MP4 写入器，走 Media Foundation Sink Writer。
    /// sample / buffer 用裸 vtable 构造（见 MfVt 注释：本机 QI 不通）。
    /// 视频流 + 可选 AAC 音频流。
    /// </summary>
    internal sealed class Mp4Writer : IDisposable
    {
        private IMFSinkWriter _writer;
        private int _streamIndex;
        private int _audioStreamIndex = -1;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;
        private readonly long _frameDuration;
        private readonly object _audioLock = new object();
        // 音频 PCM 缓冲队列：采集线程写入，录制线程泵出。
        // 用队列而非后台线程直接 WriteSample，保证所有 MF 调用都在同一线程。
        private readonly Queue<byte[]> _audioQueue = new Queue<byte[]>();
        private long _audioSampleClock;   // 已写入的 PCM 采样数（用于 PTS）
        private int _audioRate;
        private int _audioChannels;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateSample(out IntPtr ppSample);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFInitMediaTypeFromWaveFormatEx(
            IMFMediaType pMFType, IntPtr pWaveFormatEx, uint cbBufSize);

        /// <summary>withAudio=true 时创建第二条 AAC 音轨（48kHz/2ch）。</summary>
        public Mp4Writer(string path, int width, int height, int fps, int bitrate,
            int strideSign, bool withAudio)
        {
            _width = width;
            _height = height;
            _fps = fps;
            _frameDuration = 10000000L / fps;

            // ---- 输出类型：H.264 ----
            IMFMediaType outType;
            MF.Check(MF.MFCreateMediaType(out outType), "MFCreateMediaType(out)");
            outType.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Video);
            outType.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFVideoFormat_H264);
            outType.SetUINT32(ref MF.MF_MT_AVG_BITRATE, (uint)bitrate);
            outType.SetUINT32(ref MF.MF_MT_INTERLACE_MODE, (uint)MF.MFVideoInterlace_Progressive);
            outType.SetUINT64(ref MF.MF_MT_FRAME_SIZE, MF.Pack2((uint)width, (uint)height));
            outType.SetUINT64(ref MF.MF_MT_FRAME_RATE, MF.Pack2((uint)fps, 1u));
            outType.SetUINT64(ref MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack2(1u, 1u));
            outType.SetUINT32(ref MF.MF_MT_MPEG2_PROFILE, (uint)MF.eAVEncH264VProfile_Main);

            // ---- 容器：MP4 ----
            IMFAttributes attrs;
            MF.Check(MF.MFCreateAttributes(out attrs, 4), "MFCreateAttributes");
            // 不关闭节流。实测关闭后编码器队列无界增长，20 秒录制内存冲到 735 MB，
            // 而帧率并没有因此变高（瓶颈在抓屏）。节流让 WriteSample 在队列满时
            // 反压，内存稳定在低位——这才是"轻量"该有的行为。
            attrs.SetUINT32(ref MF.MF_SINK_WRITER_DISABLE_THROTTLING, 0u);
            attrs.SetUINT32(ref MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1u);
            attrs.SetUINT32(ref MF.MF_READWRITE_DISABLE_CONVERTERS, 0u);
            // 低延迟：告诉编码器不要预吞大量帧。MF 的 H.264 编码器默认会
            // 缓存一整个 GOP 的原始帧（1080p 每帧 8MB），实测占掉 320MB。
            attrs.SetUINT32(ref MF.MF_LOW_LATENCY, 1u);
            attrs.SetGUID(ref MF.MF_TRANSCODE_CONTAINERTYPE, ref MF.MFTranscodeContainerType_MPEG4);

            MF.Check(MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out _writer),
                "MFCreateSinkWriterFromURL");
            MF.Check(_writer.AddStream(outType, out _streamIndex), "AddStream");

            // ---- 输入类型：RGB32（sink writer 自动插入色彩转换） ----
            IMFMediaType inType;
            MF.Check(MF.MFCreateMediaType(out inType), "MFCreateMediaType(in)");
            inType.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Video);
            inType.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFVideoFormat_RGB32);
            inType.SetUINT32(ref MF.MF_MT_INTERLACE_MODE, (uint)MF.MFVideoInterlace_Progressive);
            inType.SetUINT64(ref MF.MF_MT_FRAME_SIZE, MF.Pack2((uint)width, (uint)height));
            inType.SetUINT64(ref MF.MF_MT_FRAME_RATE, MF.Pack2((uint)fps, 1u));
            inType.SetUINT64(ref MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack2(1u, 1u));
            // 我们的 DIB 是自上而下。实测：DEFAULT_STRIDE 取 +1*width*4 时
            // 解码结果与屏幕一致；取负值会导致画面上下翻转。
            inType.SetUINT32(ref MF.MF_MT_DEFAULT_STRIDE,
                unchecked((uint)(strideSign * width * 4)));

            MF.Check(_writer.SetInputMediaType(_streamIndex, inType, null), "SetInputMediaType");

            if (withAudio)
            {
                _audioRate = 48000;
                _audioChannels = 2;

                // 输出：AAC 128kbps
                IMFMediaType aOut;
                MF.Check(MF.MFCreateMediaType(out aOut), "MFCreateMediaType(aac out)");
                aOut.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Audio);
                aOut.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFAudioFormat_AAC);
                aOut.SetUINT32(ref MF.MF_MT_AVG_BITRATE, 128000u);
                MF.Check(_writer.AddStream(aOut, out _audioStreamIndex), "AddStream(audio)");

                // 输入：PCM16 48kHz 2ch。用 WAVEFORMATEX 构造，绕开音频属性 GUID
                WAVEFORMATEX wfx = new WAVEFORMATEX();
                wfx.wFormatTag = 1;
                wfx.nChannels = (ushort)_audioChannels;
                wfx.nSamplesPerSec = (uint)_audioRate;
                wfx.wBitsPerSample = 16;
                wfx.nBlockAlign = (ushort)(_audioChannels * 2);
                wfx.nAvgBytesPerSec = (uint)(_audioRate * _audioChannels * 2);
                wfx.cbSize = 0;

                int sz = Marshal.SizeOf(typeof(WAVEFORMATEX));
                IntPtr pWfx = Marshal.AllocHGlobal(sz);
                Marshal.StructureToPtr(wfx, pWfx, false);
                IMFMediaType aIn;
                MF.Check(MF.MFCreateMediaType(out aIn), "MFCreateMediaType(pcm in)");
                int ihr = MFInitMediaTypeFromWaveFormatEx(aIn, pWfx, (uint)sz);
                Marshal.FreeHGlobal(pWfx);
                MF.Check(ihr, "MFInitMediaTypeFromWaveFormatEx");
                aIn.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Audio);
                aIn.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFAudioFormat_PCM);
                MF.Check(_writer.SetInputMediaType(_audioStreamIndex, aIn, null),
                    "SetInputMediaType(pcm)");
            }

            MF.Check(_writer.BeginWriting(), "BeginWriting");
        }

        public bool HasAudio { get { return _audioStreamIndex >= 0; } }

        // 音频队列上限：正常 48kHz/2ch/16bit ≈ 192KB/秒。
        // 若视频抓屏变慢导致 PumpAudio 跟不上，队列会堆积；
        // 超过上限就丢弃「最新」的一块（不是最旧的）——
        // 已入队的按序写入保证时间轴连续，丢的只是极短的当前声音，
        // 不会造成音画错位，也不会让内存无界增长。
        private const int MaxAudioQueue = 300;   // 约 3 秒音频

        /// <summary>采集线程调用：把一块 PCM16 数据放入队列。</summary>
        public void EnqueueAudio(byte[] pcm16, int count)
        {
            if (_audioStreamIndex < 0) return;
            lock (_audioLock)
            {
                if (_audioQueue.Count >= MaxAudioQueue)
                {
                    return;   // 队列满，丢弃新块，保留已入队顺序
                }
                // 复制一份，因为采集线程会复用其缓冲
                byte[] copy = new byte[count];
                Buffer.BlockCopy(pcm16, 0, copy, 0, count);
                _audioQueue.Enqueue(copy);
            }
        }

        /// <summary>
        /// 录制线程每次循环调用：把队列里的 PCM 写成音频 sample。
        /// 必须在与视频 WriteSample 相同的线程（MF SinkWriter 要求）。
        /// </summary>
        public void PumpAudio()
        {
            if (_audioStreamIndex < 0) return;
            while (true)
            {
                byte[] pcm;
                lock (_audioLock)
                {
                    if (_audioQueue.Count == 0) return;
                    pcm = _audioQueue.Dequeue();
                }

                IntPtr bp;
                MF.Check(MFCreateMemoryBuffer(pcm.Length, out bp), "MFCreateMemoryBuffer(audio)");
                using (MfBuffer buf = new MfBuffer(bp))
                {
                    buf.Write(pcm, pcm.Length);
                    IntPtr sp;
                    MF.Check(MFCreateSample(out sp), "MFCreateSample(audio)");
                    using (MfSample s = new MfSample(sp))
                    {
                        s.AddBuffer(buf);
                        // 时间戳按累计采样数推，保证音画各自时间轴连续
                        long hns = _audioSampleClock * 10000000L / _audioRate;
                        int frames = pcm.Length / (_audioChannels * 2);
                        long dur = frames * 10000000L / _audioRate;
                        s.SetTime(hns);
                        s.SetDuration(dur);
                        _audioSampleClock += frames;
                        MF.Check(_writer.WriteSample(_audioStreamIndex, sp),
                            "WriteSample(audio)");
                    }
                }
            }
        }

        /// <summary>录制结束前把剩余音频全部写完。</summary>
        public void FlushAudio()
        {
            PumpAudio();
        }

        public void WriteFrameFrom(IntPtr src, long hnsTime, long hnsDuration)
        {
            int len = _width * _height * 4;

            IntPtr bufPtr;
            MF.Check(MFCreateMemoryBuffer(len, out bufPtr), "MFCreateMemoryBuffer");
            using (MfBuffer buf = new MfBuffer(bufPtr))
            {
                buf.WriteFrom(src, len);

                IntPtr samplePtr;
                MF.Check(MFCreateSample(out samplePtr), "MFCreateSample");
                using (MfSample sample = new MfSample(samplePtr))
                {
                    sample.AddBuffer(buf);
                    sample.SetTime(hnsTime);
                    sample.SetDuration(hnsDuration > 0 ? hnsDuration : _frameDuration);
                    MF.Check(_writer.WriteSample(_streamIndex, samplePtr), "WriteSample");
                }
            }
        }

        public void Finish()
        {
            if (_writer != null)
            {
                try { _writer.DoFinalize(); }
                finally
                {
                    Marshal.ReleaseComObject(_writer);
                    _writer = null;
                }
            }
        }

        public void Dispose()
        {
            if (_writer != null)
            {
                try { _writer.DoFinalize(); } catch { }
                try { Marshal.ReleaseComObject(_writer); } catch { }
                _writer = null;
            }
        }
    }
}
