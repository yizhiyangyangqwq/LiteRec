using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LiteRec
{
    /// <summary>
    /// 极简 MJPEG-AVI 封装器。纯托管写文件，零外部依赖，用于兜底。
    /// </summary>
    internal sealed class AviWriter : IDisposable
    {
        private const uint AVIF_HASINDEX = 0x00000010;
        private const uint AVIIF_KEYFRAME = 0x00000010;
        private const uint MKTAG_VIDS = 0x73646976; // 'vids'
        private const uint MKTAG_MJPG = 0x47504A4D; // 'MJPG'

        private FileStream _fs;
        private BinaryWriter _bw;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;

        private long _riffSizePos;
        private long _hdrlSizePos;
        private long _moviSizePos;
        private long _moviContentStart;
        private long _avihTotalFramesPos;
        private long _avihMaxBytesPos;
        private long _avihMicroSecPos;
        private long _strhRatePos;
        private long _strhLengthPos;
        private long _strhBufPos;
        private long _strfSizeImagePos;

        private int _frameCount;
        private long _maxFrameSize;
        private double _effectiveRate;
        private bool _disposed;
        private readonly List<long[]> _index = new List<long[]>();

        public AviWriter(string path, int width, int height, int fps)
        {
            _width = width;
            _height = height;
            _fps = fps;

            _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
            _bw = new BinaryWriter(_fs, Encoding.ASCII);
            WriteHeader();
        }

        public int FrameCount { get { return _frameCount; } }

        private static uint FourCC(string s)
        {
            return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
        }

        private long Pos()
        {
            _bw.Flush();
            return _fs.Position;
        }

        private void WriteHeader()
        {
            _bw.Write(FourCC("RIFF"));
            _riffSizePos = Pos();
            _bw.Write(0u);
            _bw.Write(FourCC("AVI "));

            _bw.Write(FourCC("LIST"));
            _hdrlSizePos = Pos();
            _bw.Write(0u);
            long hdrlContentStart = Pos();
            _bw.Write(FourCC("hdrl"));

            // ---- avih ----
            _bw.Write(FourCC("avih"));
            _bw.Write(56u);
            _avihMicroSecPos = Pos();
            _bw.Write((uint)(1000000 / _fps));
            _avihMaxBytesPos = Pos();
            _bw.Write(0u);
            _bw.Write(0u);
            _bw.Write(AVIF_HASINDEX);
            _avihTotalFramesPos = Pos();
            _bw.Write(0u);
            _bw.Write(0u);
            _bw.Write(1u);
            _bw.Write(0u);
            _bw.Write((uint)_width);
            _bw.Write((uint)_height);
            _bw.Write(0u); _bw.Write(0u); _bw.Write(0u); _bw.Write(0u);

            // ---- LIST strl ----
            _bw.Write(FourCC("LIST"));
            long strlSizePos = Pos();
            _bw.Write(0u);
            long strlContentStart = Pos();
            _bw.Write(FourCC("strl"));

            // ---- strh ----
            _bw.Write(FourCC("strh"));
            _bw.Write(56u);
            _bw.Write(MKTAG_VIDS);
            _bw.Write(MKTAG_MJPG);
            _bw.Write(0u);
            _bw.Write((ushort)0);
            _bw.Write((ushort)0);
            _bw.Write(0u);
            _bw.Write(1u);
            _strhRatePos = Pos();
            _bw.Write((uint)_fps);
            _bw.Write(0u);
            _strhLengthPos = Pos();
            _bw.Write(0u);
            _strhBufPos = Pos();
            _bw.Write(0u);
            _bw.Write(0xFFFFFFFFu);
            _bw.Write(0u);
            _bw.Write((short)0);
            _bw.Write((short)0);
            _bw.Write((short)_width);
            _bw.Write((short)_height);

            // ---- strf ----
            _bw.Write(FourCC("strf"));
            _bw.Write(40u);
            _bw.Write(40u);
            _bw.Write(_width);
            _bw.Write(_height);
            _bw.Write((ushort)1);
            _bw.Write((ushort)24);
            _bw.Write(MKTAG_MJPG);
            _strfSizeImagePos = Pos();
            _bw.Write(0u);
            _bw.Write(0);
            _bw.Write(0);
            _bw.Write(0u);
            _bw.Write(0u);

            long strlEnd = Pos();
            PatchSize(strlSizePos, strlEnd - strlContentStart);

            long hdrlEnd = Pos();
            PatchSize(_hdrlSizePos, hdrlEnd - hdrlContentStart);

            // ---- LIST movi ----
            _bw.Write(FourCC("LIST"));
            _moviSizePos = Pos();
            _bw.Write(0u);
            long moviContent = Pos();
            _bw.Write(FourCC("movi"));
            // idx1 里的偏移基准 = "movi" 四字符码的位置（首块偏移为 4，符合通用约定）
            _moviContentStart = moviContent;
        }

        private void PatchSize(long pos, long value)
        {
            _bw.Flush();
            long save = _fs.Position;
            _fs.Position = pos;
            byte[] b = BitConverter.GetBytes((uint)value);
            _fs.Write(b, 0, 4);
            _fs.Flush();
            _fs.Position = save;
        }

        public void WriteFrame(byte[] data, int length)
        {
            long chunkPos = Pos();
            _bw.Write(FourCC("00dc"));
            _bw.Write((uint)length);
            _bw.Write(data, 0, length);
            if ((length & 1) != 0) _bw.Write((byte)0);

            long offset = chunkPos - _moviContentStart;
            _index.Add(new long[] { offset, length });
            _frameCount++;
            if (length > _maxFrameSize) _maxFrameSize = length;
        }

        /// <summary>
        /// 用真实经过时间登记一帧：AVI 的 dwRate/dwScale 只能表达固定帧率，
        /// 因此掉帧时改写 avih/strh 的有效帧率，使总时长仍然正确。
        /// </summary>
        public void WriteFrame(byte[] data, int length, long elapsedTicks, long freq)
        {
            WriteFrame(data, length);
            if (freq > 0 && elapsedTicks > 0)
            {
                double secs = (double)elapsedTicks / freq;
                if (secs > 0.05)
                {
                    // 有效帧率 = 已写帧数 / 实际秒数，最大限度保持时长正确
                    double effRate = _frameCount / secs;
                    _effectiveRate = effRate;
                }
            }
        }

        public void Finish()
        {
            if (_bw == null) return;

            long moviEnd = Pos();
            uint moviSize = (uint)(moviEnd - (_moviSizePos + 4));
            PatchSize(_moviSizePos, moviSize);

            _bw.Write(FourCC("idx1"));
            _bw.Write((uint)(_index.Count * 16));
            for (int i = 0; i < _index.Count; i++)
            {
                _bw.Write(FourCC("00dc"));
                _bw.Write(AVIIF_KEYFRAME);
                _bw.Write((uint)_index[i][0]);
                _bw.Write((uint)_index[i][1]);
            }

            long fileEnd = Pos();
            PatchSize(_riffSizePos, fileEnd - (_riffSizePos + 4));

            PatchSize(_avihTotalFramesPos, (long)_frameCount);
            PatchSize(_avihMaxBytesPos, _maxFrameSize * _fps);
            PatchSize(_strfSizeImagePos, _maxFrameSize);
            PatchSize(_strhLengthPos, (long)_frameCount);
            PatchSize(_strhBufPos, _maxFrameSize);

            // 掉帧时用有效帧率覆盖标称帧率，保证播放时长正确
            if (_effectiveRate > 0.5 && Math.Abs(_effectiveRate - _fps) > 0.5)
            {
                int rate = (int)Math.Round(_effectiveRate);
                if (rate < 1) rate = 1;
                PatchSize(_strhRatePos, (long)rate);
                PatchSize(_avihMicroSecPos, (long)(1000000 / rate));
            }

            _bw.Flush();
            _fs.Flush(true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_bw != null) { _bw.Flush(); _bw = null; }
                if (_fs != null) { _fs.Dispose(); _fs = null; }
            }
            catch { }
        }
    }
}
