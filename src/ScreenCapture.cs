using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        private readonly double _cursorScale;

        private IntPtr _memDC;
        private IntPtr _hBitmap;
        private IntPtr _oldBitmap;
        private IntPtr _bits;
        private Bitmap _wrapped;
        private bool _disposed;

        // 光标热点缓存：每帧都调 GetIconInfo 会分配两张位图，太浪费。
        // 只在光标句柄变化时查一次（箭头/文字/手型切换时才变）。
        private IntPtr _lastCursor;
        private int _hotspotX;
        private int _hotspotY;

        // 高分辨率光标（从 .cur 里取 96px 帧），画的时候 1:1，不拉伸所以不糊
        private CursorImage _cursorImg;
        private Graphics _memG;

        public ScreenCapture(int srcX, int srcY, int width, int height, bool drawCursor)
            : this(srcX, srcY, width, height, drawCursor, 1.0, null, 0)
        {
        }

        public ScreenCapture(int srcX, int srcY, int width, int height,
            bool drawCursor, double cursorScale)
            : this(srcX, srcY, width, height, drawCursor, cursorScale, null, 0)
        {
        }

        public ScreenCapture(int srcX, int srcY, int width, int height,
            bool drawCursor, double cursorScale, string cursorImagePath)
            : this(srcX, srcY, width, height, drawCursor, cursorScale, cursorImagePath, 0)
        {
        }

        /// <param name="cursorScale">光标放大倍数，1.0 = 原始大小。</param>
        /// <param name="cursorImagePath">可选：.cur 文件路径，用其中的高分辨率帧替代系统光标。</param>
        /// <param name="targetCursorPx">可选：目标光标像素尺寸（如 48/64/96）。
        /// 大于 0 时忽略 cursorScale，直接按目标像素换算——这样"中号"就真的是 48px，
        /// 不会因为源图是 96px 而放大成 144px。</param>
        public ScreenCapture(int srcX, int srcY, int width, int height,
            bool drawCursor, double cursorScale, string cursorImagePath, int targetCursorPx)
        {
            // JPEG/视频编码要求偶数尺寸
            _srcX = srcX;
            _srcY = srcY;
            _width = (width / 2) * 2;
            _height = (height / 2) * 2;
            if (_width < 16) _width = 16;
            if (_height < 16) _height = 16;
            _drawCursor = drawCursor;
            _cursorScale = cursorScale < 0.5 ? 0.5 : (cursorScale > 8.0 ? 8.0 : cursorScale);

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

                // 可选：加载高分辨率光标
                if (drawCursor && !string.IsNullOrEmpty(cursorImagePath))
                {
                    _cursorImg = CursorImage.Load(cursorImagePath, 256);
                    _memG = Graphics.FromImage(_wrapped);
                    _memG.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    _memG.PixelOffsetMode = PixelOffsetMode.HighQuality;
                }

                // 按目标像素换算缩放：这样"中号"就是真的 48px，
                // 不会因为源图是 96px 而变成 144px。
                if (targetCursorPx > 0)
                {
                    int basePx = (_cursorImg != null)
                        ? _cursorImg.Image.Width
                        : Gdi.GetSystemMetrics(13);
                    if (basePx <= 0) basePx = 32;
                    double s = (double)targetCursorPx / basePx;
                    if (s < 0.2) s = 0.2;
                    if (s > 8.0) s = 8.0;
                    _cursorScale = s;
                }
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
                            DrawCursor(ci.hCursor, cx, cy);
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

        /// <summary>把光标画到 DIB 上，支持放大。热点位置保证缩放后仍对准真实点击点。</summary>
        private void DrawCursor(IntPtr hCursor, int x, int y)
        {
            // 优先用高分辨率光标图（1:1 或缩放后都清晰）
            if (_cursorImg != null && _memG != null)
            {
                int w = (int)Math.Round(_cursorImg.Image.Width * _cursorScale);
                int h = (int)Math.Round(_cursorImg.Image.Height * _cursorScale);
                if (w < 1) w = 1;
                if (h < 1) h = 1;
                int dx = x - (int)Math.Round(_cursorImg.HotspotX * _cursorScale);
                int dy = y - (int)Math.Round(_cursorImg.HotspotY * _cursorScale);

                // 1:1 用最近邻（最锐），缩放用双三次（最平滑）
                _memG.InterpolationMode = (_cursorScale == 1.0)
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.HighQualityBicubic;
                _memG.DrawImage(_cursorImg.Image, new Rectangle(dx, dy, w, h),
                    0, 0, _cursorImg.Image.Width, _cursorImg.Image.Height,
                    GraphicsUnit.Pixel);
                _memG.Flush(FlushIntention.Sync);
                return;
            }

            // 光标句柄变了才查热点（避免每帧分配两张 GDI 位图）
            if (hCursor != _lastCursor)
            {
                _lastCursor = hCursor;
                _hotspotX = 0;
                _hotspotY = 0;
                ICONINFO ii;
                if (Gdi.GetIconInfo(hCursor, out ii))
                {
                    _hotspotX = ii.xHotspot;
                    _hotspotY = ii.yHotspot;
                    // GetIconInfo 会创建两张位图，必须释放，否则每换一次光标泄漏一次
                    if (ii.hbmMask != IntPtr.Zero) Gdi.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) Gdi.DeleteObject(ii.hbmColor);
                }
            }

            if (_cursorScale <= 1.0)
            {
                // 原始尺寸：和以前的 0,0 默认尺寸一致
                Gdi.DrawIconEx(_memDC, x, y, hCursor, 0, 0, 0, IntPtr.Zero, Gdi.DI_NORMAL);
                return;
            }

            int baseW = Gdi.GetSystemMetrics(13);   // SM_CXCURSOR
            int baseH = Gdi.GetSystemMetrics(14);   // SM_CYCURSOR
            if (baseW <= 0) baseW = 32;
            if (baseH <= 0) baseH = 32;

            int scaledW = (int)Math.Round(baseW * _cursorScale);
            int scaledH = (int)Math.Round(baseH * _cursorScale);

            // DrawIconEx 以左上角定位；放大后要按热点偏移回去，尖角才对得准
            int ox = (int)Math.Round(_hotspotX * (_cursorScale - 1.0));
            int oy = (int)Math.Round(_hotspotY * (_cursorScale - 1.0));

            Gdi.DrawIconEx(_memDC, x - ox, y - oy, hCursor, scaledW, scaledH, 0, IntPtr.Zero, Gdi.DI_NORMAL);
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

            // 先放 Graphics（它引用着 _wrapped），再放 Bitmap
            if (_memG != null)
            {
                _memG.Dispose();
                _memG = null;
            }
            if (_wrapped != null)
            {
                _wrapped.Dispose();
                _wrapped = null;
            }
            if (_cursorImg != null)
            {
                _cursorImg.Dispose();
                _cursorImg = null;
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
