using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace IconGen
{
    /// <summary>
    /// 生成多尺寸 .ico：小尺寸用 BMP 条目（.NET Icon 兼容性最好），
    /// 256 用 PNG 条目（体积小，资源管理器大图标清晰）。
    /// 用法: IconGen.exe out.ico [preview.png]
    /// </summary>
    internal static class Program
    {
        // 小尺寸（BMP 存法）。64px 实际几乎不被 Windows 取用，
        // 去掉可省约 17 KB（图标要进 exe 两次，等于省 34 KB）。
        private static readonly int[] BmpSizes = new int[] { 16, 20, 24, 32, 48 };

        private static int Main(string[] args)
        {
            string outIco = args.Length > 0 ? args[0] : "icon.ico";
            string preview = args.Length > 1 ? args[1] : null;
            if (args.Length > 2) Variant = int.Parse(args[2]);

            List<byte[]> images = new List<byte[]>();
            List<int> sizes = new List<int>();
            List<bool> isPng = new List<bool>();

            foreach (int s in BmpSizes)
            {
                using (Bitmap b = Draw(s))
                {
                    images.Add(ToDib(b));
                    sizes.Add(s);
                    isPng.Add(false);
                }
            }

            // 256x256：用 PNG 压缩，否则单这一张就是 256 KB
            using (Bitmap b = Draw(256))
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                    sizes.Add(256);
                    isPng.Add(true);
                }
            }

            WriteIco(outIco, sizes, images, isPng);
            Console.WriteLine("ico: " + outIco + "  bytes=" + new FileInfo(outIco).Length);

            if (preview != null)
            {
                MakePreview(preview);
                Console.WriteLine("preview: " + preview);
            }
            return 0;
        }

        /// <summary>图标造型：蓝色圆角方块 + 红色录制圆点。variant 0=纯平 1=带屏幕框</summary>
        private static Bitmap Draw(int size)
        {
            return Draw(size, Variant);
        }

        private static int Variant = 0;

        private static Bitmap Draw(int size, int variant)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float pad = size * 0.055f;
                RectangleF rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
                float radius = size * 0.24f;

                using (GraphicsPath path = RoundRect(rect, radius))
                {
                    // 左上亮、右下深，营造一点体积感
                    using (LinearGradientBrush br = new LinearGradientBrush(
                        new RectangleF(0, 0, size, size),
                        Color.FromArgb(255, 92, 160, 255),
                        Color.FromArgb(255, 30, 92, 210),
                        45f))
                    {
                        g.FillPath(br, path);
                    }
                }

                if (variant == 1)
                {
                    // 内部浅色"屏幕"，强化"录屏"语义；小尺寸下会糊，所以只在大尺寸用
                    float m = size * 0.20f;
                    RectangleF scr = new RectangleF(m, m * 1.05f,
                        size - m * 2, size - m * 2.10f);
                    using (GraphicsPath sp = RoundRect(scr, size * 0.10f))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(255, 240, 246, 255)))
                    {
                        g.FillPath(sb, sp);
                    }
                }

                // 红色录制点
                float d = size * (variant == 1 ? 0.40f : 0.44f);
                float cx = size / 2f;
                float cy = size / 2f;
                RectangleF dot = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);

                using (SolidBrush red = new SolidBrush(Color.FromArgb(255, 250, 60, 62)))
                {
                    g.FillEllipse(red, dot);
                }

                // 只在 256 大图标上加极轻的高光，小尺寸保持纯平（缩放最干净）
                if (size >= 128)
                {
                    float hd = d * 0.46f;
                    RectangleF hi = new RectangleF(
                        cx - hd * 0.60f, cy - d * 0.40f, hd, hd * 0.62f);
                    using (SolidBrush w = new SolidBrush(Color.FromArgb(38, 255, 255, 255)))
                    {
                        g.FillEllipse(w, hi);
                    }
                }
            }
            return bmp;
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>把位图转成 ICO 内部的 BMP 条目（DIB + AND 掩码，行序自下而上）。</summary>
        private static byte[] ToDib(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            int xorSize = w * h * 4;
            int maskRow = ((w + 31) / 32) * 4;
            int maskSize = maskRow * h;

            byte[] result = new byte[40 + xorSize + maskSize];
            using (MemoryStream ms = new MemoryStream(result))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                // BITMAPINFOHEADER
                bw.Write(40);              // biSize
                bw.Write(w);               // biWidth
                bw.Write(h * 2);           // biHeight = XOR + AND
                bw.Write((short)1);        // biPlanes
                bw.Write((short)32);       // biBitCount
                bw.Write(0);               // biCompression = BI_RGB
                bw.Write(xorSize + maskSize);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);

                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[w * 4];
                    // 自下而上写
                    for (int y = h - 1; y >= 0; y--)
                    {
                        IntPtr src = new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride);
                        System.Runtime.InteropServices.Marshal.Copy(src, row, 0, w * 4);
                        bw.Write(row);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                // AND 掩码全 0：透明度交给 alpha 通道
                bw.Write(new byte[maskSize]);
            }
            return result;
        }

        private static void WriteIco(string path, List<int> sizes, List<byte[]> images,
            List<bool> isPng)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((short)0);            // reserved
                bw.Write((short)1);            // type = icon
                bw.Write((short)sizes.Count);

                int offset = 6 + 16 * sizes.Count;
                for (int i = 0; i < sizes.Count; i++)
                {
                    int s = sizes[i];
                    bw.Write((byte)(s >= 256 ? 0 : s));   // width（0 表示 256）
                    bw.Write((byte)(s >= 256 ? 0 : s));   // height
                    bw.Write((byte)0);                    // color count
                    bw.Write((byte)0);                    // reserved
                    bw.Write((short)1);                   // planes
                    bw.Write((short)32);                  // bit count
                    bw.Write(images[i].Length);
                    bw.Write(offset);
                    offset += images[i].Length;
                }

                for (int i = 0; i < images.Count; i++)
                {
                    bw.Write(images[i]);
                }
            }
        }

        /// <summary>把各尺寸放大 4 倍拼在一起，方便肉眼判断 16px 下是否还认得出。</summary>
        private static void MakePreview(string path)
        {
            int[] show = new int[] { 16, 24, 32, 48, 64, 256 };
            int scale = 4;
            int gap = 12;

            // 256 太大，单独缩到 64 展示
            int totalW = gap;
            foreach (int s in show) totalW += (s == 256 ? 64 : s) * scale + gap;

            int maxH = 64 * scale;
            using (Bitmap sheet = new Bitmap(totalW, maxH + 24))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(32, 32, 36));
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                int x = gap;
                foreach (int s in show)
                {
                    int src = s == 256 ? 256 : s;
                    int drawSize = (s == 256 ? 64 : s) * scale;
                    using (Bitmap b = Draw(src))
                    {
                        int y = (maxH - drawSize) / 2 + 4;
                        g.DrawImage(b, new Rectangle(x, y, drawSize, drawSize));
                    }
                    using (Font f = new Font("Segoe UI", 9f))
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(200, 200, 206)))
                    {
                        g.DrawString(s + "px", f, fb, x, maxH + 2);
                    }
                    x += drawSize + gap;
                }
                sheet.Save(path, ImageFormat.Png);
            }
        }
    }
}
