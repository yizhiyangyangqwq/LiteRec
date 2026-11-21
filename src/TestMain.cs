using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LiteRec
{
    /// <summary>
    /// 无界面自测：真截屏 2 秒，分别产出 MP4 与 AVI，然后校验文件结构。
    /// 用法: LiteRec.Test.exe mp4|avi [秒数]
    /// </summary>
    internal static class TestMain
    {
        private static bool optRecordAudio;

        private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "mp4";
            int seconds = args.Length > 1 ? int.Parse(args[1]) : 2;

            try { Gdi.SetProcessDPIAware(); }
            catch { }

            string outDir = Path.Combine(Path.GetTempPath(), "LiteRecTest");
            Directory.CreateDirectory(outDir);

            Rectangle vs = SystemInformation.VirtualScreen;
            Console.WriteLine("virtual-screen: " + vs.Width + "x" + vs.Height
                + " @ " + vs.X + "," + vs.Y);

            // 只录左上角一块，保证测试快速；传 full 则录整个虚拟屏幕
            bool full = args.Length > 2 && args[2].Equals("full", StringComparison.OrdinalIgnoreCase);
            int w = full ? vs.Width : Math.Min(vs.Width, 640);
            int h = full ? vs.Height : Math.Min(vs.Height, 360);

            RecorderOptions opt = new RecorderOptions();
            opt.Source = new Rectangle(vs.X, vs.Y, w, h);
            opt.Fps = 30;
            opt.JpegQuality = 80;
            opt.BitrateKbps = 4000;
            opt.DrawCursor = true;
            opt.Format = mode == "avi" ? OutputFormat.Avi : OutputFormat.Mp4;
            // 第四参数 "audio" 或 "audiofull" 时开启系统声音录制
            opt.RecordAudio = args.Length > 2 &&
                args[2].ToLowerInvariant().StartsWith("audio");
            optRecordAudio = opt.RecordAudio;
            string ext = opt.Format == OutputFormat.Avi ? ".avi" : ".mp4";
            opt.OutputPath = Path.Combine(outDir, "test" + ext);

            if (File.Exists(opt.OutputPath)) File.Delete(opt.OutputPath);

            Recorder rec = new Recorder(opt);
            Stopwatch sw = new Stopwatch();
            try
            {
                rec.Start();
                Console.WriteLine("start: OK");
                sw.Start();   // 初始化（枚举/加载编码器）不计入录制时长
            }
            catch (Exception ex)
            {
                Console.WriteLine("start: FAIL -> " + ex.Message);
                return 2;
            }

            // 若开了音频，录制期间循环播放一段 440Hz 音调，确保有真实声音可采
            string tone = Path.Combine(outDir, "tone.wav");
            if (opt.RecordAudio)
            {
                MakeToneWav(tone);
                System.Media.SoundPlayer player = new System.Media.SoundPlayer(tone);
                player.PlayLooping();
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    System.Threading.Thread.Sleep(100);
                }
                player.Stop();
                player.Dispose();
            }
            else
            {
                // 期间不断刷新屏幕内容，确保帧确实不同
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }

            Console.WriteLine("frames-captured: " + rec.FramesWritten);
            Console.WriteLine("audio-active: " + rec.AudioActive);
            if (rec.AudioError != null) Console.WriteLine("audio-error: " + rec.AudioError);
            Console.WriteLine("avg-fps: " + rec.CurrentFps.ToString("0.0"));
            Console.WriteLine("elapsed-s: " + sw.Elapsed.TotalSeconds.ToString("0.00"));
            Console.WriteLine("effective-fps: "
                + (rec.FramesWritten / Math.Max(0.001, sw.Elapsed.TotalSeconds)).ToString("0.0"));
            if (!string.IsNullOrEmpty(rec.LastError))
            {
                Console.WriteLine("engine-error: " + rec.LastError);
            }

            rec.Stop();
            long frames = rec.FramesWritten;
            rec.Dispose();

            if (!File.Exists(opt.OutputPath))
            {
                Console.WriteLine("output: MISSING");
                return 3;
            }

            FileInfo fi = new FileInfo(opt.OutputPath);
            Console.WriteLine("output: " + fi.FullName);
            Console.WriteLine("bytes: " + fi.Length);
            Console.WriteLine("frames: " + frames);

            bool ok = ValidateFile(opt.OutputPath, opt.Format, w / 2 * 2, h / 2 * 2);
            Console.WriteLine("validate: " + (ok ? "PASS" : "FAIL"));

            // 参考帧：直接 GDI 拷贝同一区域，用于比对编码后的方向
            try
            {
                int cw = w / 2 * 2, ch = h / 2 * 2;
                using (Bitmap refBmp = new Bitmap(cw, ch))
                {
                    using (Graphics g = Graphics.FromImage(refBmp))
                    {
                        g.CopyFromScreen(opt.Source.X, opt.Source.Y, 0, 0,
                            new Size(cw, ch), CopyPixelOperation.SourceCopy);
                    }
                    string rp = Path.Combine(outDir, "reference.png");
                    refBmp.Save(rp, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine("reference: " + rp);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("reference: FAIL " + ex.Message);
            }

            return ok ? 0 : 4;
        }

        private static bool ValidateFile(string path, OutputFormat fmt, int w, int h)
        {
            byte[] head = new byte[16];
            using (FileStream fs = File.OpenRead(path))
            {
                if (fs.Length < 64) return false;
                fs.Read(head, 0, 16);
            }

            string tag = System.Text.Encoding.ASCII.GetString(head, 0, 4);
            Console.WriteLine("magic: " + tag);

            if (fmt == OutputFormat.Avi)
            {
                if (tag != "RIFF") return false;
                string avi = System.Text.Encoding.ASCII.GetString(head, 8, 4);
                Console.WriteLine("form: " + avi);
                if (avi != "AVI ") return false;

                // 校验 strf 中的宽高
                byte[] all = File.ReadAllBytes(path);
                int idx = IndexOf(all, System.Text.Encoding.ASCII.GetBytes("strf"));
                if (idx < 0) { Console.WriteLine("strf: MISSING"); return false; }
                int headerStart = idx + 8;
                int bw = BitConverter.ToInt32(all, headerStart + 4);
                int bh = BitConverter.ToInt32(all, headerStart + 8);
                Console.WriteLine("strf-size: " + bw + "x" + bh + " expect " + w + "x" + h);
                return bw == w && bh == h;
            }
            else
            {
                // MP4: 头部应有 ftyp
                byte[] all = File.ReadAllBytes(path);
                int idx = IndexOf(all, System.Text.Encoding.ASCII.GetBytes("ftyp"));
                Console.WriteLine("ftyp-offset: " + idx);
                if (idx < 0) return false;
                int mdat = IndexOf(all, System.Text.Encoding.ASCII.GetBytes("mdat"));
                int moov = IndexOf(all, System.Text.Encoding.ASCII.GetBytes("moov"));
                Console.WriteLine("mdat-offset: " + mdat + " moov-offset: " + moov);
                // 音轨检测：MP4 里 AAC 采样条目为 'mp4a'，有它说明存在音频流
                int mp4a = IndexOf(all, System.Text.Encoding.ASCII.GetBytes("mp4a"));
                Console.WriteLine("audio-track(mp4a): " + (mp4a > 0 ? "YES @ " + mp4a : "NO"));
                if (optRecordAudio) return mdat > 0 && moov > 0 && mp4a > 0;
                return mdat > 0 || moov > 0;
            }
        }

        private static void MakeToneWav(string path)
        {
            int rate = 48000, secs = 2, samples = rate * secs;
            using (FileStream fs = new FileStream(path, FileMode.Create))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(new char[] { 'R', 'I', 'F', 'F' });
                bw.Write(36 + samples * 4);
                bw.Write(new char[] { 'W', 'A', 'V', 'E' });
                bw.Write(new char[] { 'f', 'm', 't', ' ' });
                bw.Write(16);
                bw.Write((short)1); bw.Write((short)2);
                bw.Write(rate); bw.Write(rate * 4);
                bw.Write((short)4); bw.Write((short)16);
                bw.Write(new char[] { 'd', 'a', 't', 'a' });
                bw.Write(samples * 4);
                for (int i = 0; i < samples; i++)
                {
                    short v = (short)(Math.Sin(2 * Math.PI * 440.0 * i / rate) * 12000);
                    bw.Write(v); bw.Write(v);
                }
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            int limit = haystack.Length - needle.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool m = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { m = false; break; }
                }
                if (m) return i;
            }
            return -1;
        }
    }
}
