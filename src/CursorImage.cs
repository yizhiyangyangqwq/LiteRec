using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LiteRec
{
    /// <summary>
    /// 从 .cur 文件里取出指定尺寸（默认最大）的帧，作为高分辨率光标叠加。
    ///
    /// 为什么需要它：系统光标只有 SM_CXCURSOR（通常 32px）那么大，想放大只能拉伸，
    /// 必然锯齿。而现代光标主题（如 W11 概念版）文件里往往内嵌多个尺寸
    /// （实测 W11_light_v2.2 含 32/48/64/96），直接用 96px 就是 1:1 清晰输出。
    /// </summary>
    internal sealed class CursorImage : IDisposable
    {
        public Bitmap Image { get; private set; }
        /// <summary>热点（相对于光标图像左上角，以图像像素为单位）。</summary>
        public int HotspotX { get; private set; }
        public int HotspotY { get; private set; }

        /// <summary>加载 .cur，选用不超过 maxSize 的最大帧。</summary>
        public static CursorImage Load(string path, int maxSize)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到光标文件", path);
            byte[] all = File.ReadAllBytes(path);
            if (all.Length < 6) throw new InvalidDataException("光标文件太小");

            int type = BitConverter.ToUInt16(all, 2);
            int count = BitConverter.ToUInt16(all, 4);
            if (type != 2) throw new InvalidDataException("不是 .cur 光标文件（type=" + type + "）");

            int bestIdx = -1, bestW = 0;
            for (int i = 0; i < count; i++)
            {
                int off = 6 + i * 16;
                if (off + 16 > all.Length) break;
                int w = all[off]; if (w == 0) w = 256;
                if (w <= maxSize && w > bestW) { bestW = w; bestIdx = i; }
            }
            // 所有帧都比 maxSize 大时，退回最小的一帧
            if (bestIdx < 0 && count > 0)
            {
                bestIdx = 0; bestW = all[6]; if (bestW == 0) bestW = 256;
            }
            if (bestIdx < 0) throw new InvalidDataException("光标文件里没有可用帧");

            int e = 6 + bestIdx * 16;
            int hx = BitConverter.ToUInt16(all, e + 4);
            int hy = BitConverter.ToUInt16(all, e + 6);
            int bytes = BitConverter.ToInt32(all, e + 8);
            int dataOff = BitConverter.ToInt32(all, e + 12);
            if (dataOff < 0 || bytes <= 0 || dataOff + bytes > all.Length)
                throw new InvalidDataException("光标帧数据越界");

            bool isPng = all[dataOff] == 0x89 && all[dataOff + 1] == 0x50;
            Bitmap bmp;
            if (isPng)
            {
                using (MemoryStream ms = new MemoryStream(all, dataOff, bytes))
                    bmp = new Bitmap(ms);
            }
            else
            {
                bmp = DecodeDib(all, dataOff, bestW);
            }

            // 热点是按该帧尺寸记录的；缩放到实际图像尺寸
            CursorImage ci = new CursorImage();
            ci.Image = bmp;
            if (bmp.Width != bestW && bestW > 0)
            {
                double k = (double)bmp.Width / bestW;
                ci.HotspotX = (int)Math.Round(hx * k);
                ci.HotspotY = (int)Math.Round(hy * k);
            }
            else
            {
                ci.HotspotX = hx;
                ci.HotspotY = hy;
            }
            return ci;
        }

        /// <summary>解析 CUR 内嵌的 32bpp BMP（自下而上，含 alpha）。</summary>
        private static Bitmap DecodeDib(byte[] all, int dataOff, int expectW)
        {
            // 直接从字节数组读 BITMAPINFOHEADER 字段，避免指针/unsafe
            int biSize = BitConverter.ToInt32(all, dataOff);
            int biWidth = BitConverter.ToInt32(all, dataOff + 4);
            int biHeight = BitConverter.ToInt32(all, dataOff + 8);
            int biBitCount = BitConverter.ToUInt16(all, dataOff + 14);
            int biCompression = BitConverter.ToInt32(all, dataOff + 16);

            int w = biWidth;
            int h = Math.Abs(biHeight) / 2;   // CUR 的 biHeight = XOR + AND 高度之和
            if (h <= 0) h = Math.Abs(biHeight);

            int headerSize = biSize < 40 ? 40 : biSize;
            int bpp = biBitCount <= 0 ? 32 : biBitCount;
            if (bpp != 32)
                throw new InvalidDataException("只支持 32 位光标帧（当前 " + bpp + " 位）");
            if (biCompression != 0)
                throw new InvalidDataException("不支持的压缩方式: " + biCompression);

            int xorBytes = w * h * 4;
            if (dataOff + headerSize + xorBytes > all.Length)
                throw new InvalidDataException("BMP 帧数据不完整");

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    // 源是自下而上，需要翻转行序
                    int srcRow = dataOff + headerSize + (h - 1 - y) * w * 4;
                    Buffer.BlockCopy(all, srcRow, row, 0, w * 4);
                    IntPtr dst = new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride);
                    Marshal.Copy(row, 0, dst, w * 4);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        public void Dispose()
        {
            if (Image != null) { Image.Dispose(); Image = null; }
        }
    }
}
