using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LiteRec
{
    /// <summary>
    /// 屏幕采集器：持久化 DIB 段 + BitBlt，零逐帧分配。
    /// 可选叠加鼠标指针。
    /// </summary>
    internal sealed class ScreenCapture : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _srcX;
        private readonly int _srcY;
        private readonly bool _drawCursor;

        private IntPtr _memDC;
        private IntPtr _hBitmap;
        private IntPtr _oldBitmap;
        private IntPtr _bits;
        private Bitmap _wrapped;
        private bool _disposed;

        public ScreenCapture(int srcX, int srcY, int width, int height, bool drawCursor)
        {
            // JPEG/视频编码要求偶数尺寸
            _srcX = srcX;
            _srcY = srcY;
            _width = (width / 2) * 2;
            _height = (height / 2) * 2;
            if (_width < 16) _width = 16;
            if (_height < 16) _height = 16;
            _drawCursor = drawCursor;

            IntPtr screenDC = Gdi.GetDC(IntPtr.Zero);
            try
            {
                _memDC = Gdi.CreateCompatibleDC(screenDC);

                BITMAPINFO bi = new BITMAPINFO();
                bi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bi.bmiHeader.biWidth = _width;
                bi.bmiHeader.biHeight = -_height;   // 负值 = 自上而下
                bi.bmiHeader.biPlanes = 1;
                bi.bmiHeader.biBitCount = 32;
                bi.bmiHeader.biCompression = (uint)Gdi.BI_RGB;
                bi.bmiHeader.biSizeImage = (uint)(_width * _height * 4);

                _hBitmap = Gdi.CreateDIBSection(screenDC, ref bi, Gdi.DIB_RGB_COLORS,
                    out _bits, IntPtr.Zero, 0);
                if (_hBitmap == IntPtr.Zero)
                    throw new InvalidOperationException("CreateDIBSection 失败");

                _oldBitmap = Gdi.SelectObject(_memDC, _hBitmap);

                // 用持久 Bitmap 包裹同一块 DIB 内存，避免每帧复制
                _wrapped = new Bitmap(_width, _height, _width * 4,
                    PixelFormat.Format32bppRgb, _bits);
            }
            finally
            {
                Gdi.ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }

        /// <summary>DIB 像素首地址（自上而下 BGRA）。供零拷贝编码路径使用。</summary>
        public IntPtr Bits { get { return _bits; } }

        /// <summary>抓一帧到内部 DIB。</summary>
        public void Grab()
        {
            IntPtr screenDC = Gdi.GetDC(IntPtr.Zero);
            try
            {
                Gdi.BitBlt(_memDC, 0, 0, _width, _height, screenDC, _srcX, _srcY, Gdi.SRCCOPY);

                if (_drawCursor)
                {
                    CURSORINFO ci = new CURSORINFO();
                    ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                    if (Gdi.GetCursorInfo(ref ci) && ci.flags == Gdi.CURSOR_SHOWING && ci.hCursor != IntPtr.Zero)
                    {
                        int cx = ci.ptScreenPos.X - _srcX;
                        int cy = ci.ptScreenPos.Y - _srcY;
                        if (cx > -64 && cy > -64 && cx < _width + 64 && cy < _height + 64)
                        {
                            Gdi.DrawIconEx(_memDC, cx, cy, ci.hCursor, 0, 0, 0, IntPtr.Zero, Gdi.DI_NORMAL);
                        }
                    }
                }
                Gdi.GdiFlush();
            }
            finally
            {
                Gdi.ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        /// <summary>
        /// 把当前 DIB 帧编码成 JPEG 写入 stream（stream 会被重置）。
        /// GDI+ 直接写 GDI 流，仅用于小区域或低帧率；大分辨率请用 EncodeJpegToPtr。
        /// </summary>
        public int EncodeJpeg(Stream stream, long quality)
        {
            stream.Position = 0;
            stream.SetLength(0);

            // 复用编码器参数对象：每帧新建会产生大量可回收垃圾，
            // 在高帧率下直接体现为掉帧。
            EnsureEncoderParams(quality);
            _wrapped.Save(stream, GetJpegCodec(), _encoderParams);
            return (int)stream.Length;
        }

        private EncoderParameters _encoderParams;
        private long _encoderQuality = -1;

        private void EnsureEncoderParams(long quality)
        {
            if (_encoderParams == null)
            {
                _encoderParams = new EncoderParameters(1);
                _encoderQuality = -1;
            }
            if (_encoderQuality != quality)
            {
                _encoderParams.Param[0] = new EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality, quality);
                _encoderQuality = quality;
            }
        }

        private static ImageCodecInfo _jpegCodec;

        private static ImageCodecInfo GetJpegCodec()
        {
            if (_jpegCodec != null) return _jpegCodec;
            ImageCodecInfo[] all = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].FormatID == ImageFormat.Jpeg.Guid)
                {
                    _jpegCodec = all[i];
                    break;
                }
            }
            if (_jpegCodec == null) throw new InvalidOperationException("找不到 JPEG 编码器");
            return _jpegCodec;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_wrapped != null)
            {
                _wrapped.Dispose();
                _wrapped = null;
            }
            if (_encoderParams != null)
            {
                _encoderParams.Dispose();
                _encoderParams = null;
            }
            // Bitmap 释放不拥有外部内存，DIB 需自行删除
            if (_memDC != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            {
                Gdi.SelectObject(_memDC, _oldBitmap);
                _oldBitmap = IntPtr.Zero;
            }
            if (_hBitmap != IntPtr.Zero)
            {
                Gdi.DeleteObject(_hBitmap);
                _hBitmap = IntPtr.Zero;
            }
            if (_memDC != IntPtr.Zero)
            {
                Gdi.DeleteDC(_memDC);
                _memDC = IntPtr.Zero;
            }
        }
    }
}
