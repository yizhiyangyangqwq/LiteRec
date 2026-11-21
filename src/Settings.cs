using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace LiteRec
{
    /// <summary>
    /// 极简配置持久化。写在 %APPDATA%\LiteRec\settings.ini。
    /// 刻意不用 XML/JSON 序列化：省掉 System.Xml 依赖，也能让人直接手改。
    /// </summary>
    internal sealed class Settings
    {
        private const string FileName = "settings.ini";

        public string OutputDir;
        public int Fps = 30;
        public int Quality = 8;
        public int Format;          // 0 = MP4, 1 = AVI
        public bool DrawCursor = true;
        public int Mode;            // 0 = 全屏, 1 = 选择区域
        public bool RecordAudio = true;
        // 全局快捷键的 Keys 值（默认 F9 = 120）。0 表示无效。
        public int Hotkey = 120;
        // 录制时是否隐藏右上角悬浮控制条
        public bool HideBar = false;

        public static Keys DefaultHotkey { get { return Keys.F9; } }

        public static string DefaultOutputDir
        {
            get
            {
                string videos = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyVideos);
                // 某些精简系统上 MyVideos 可能返回空
                if (string.IsNullOrEmpty(videos))
                {
                    videos = Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile);
                }
                if (string.IsNullOrEmpty(videos))
                {
                    videos = Path.GetTempPath();
                }
                return Path.Combine(videos, "LiteRec");
            }
        }

        public static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LiteRec");
                return Path.Combine(dir, FileName);
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            s.OutputDir = DefaultOutputDir;

            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) return s;

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "outputdir":
                            if (val.Length > 0) s.OutputDir = val;
                            break;
                        case "fps":
                            s.Fps = Clamp(ParseInt(val, s.Fps), 5, 120);
                            break;
                        case "quality":
                            s.Quality = Clamp(ParseInt(val, s.Quality), 3, 10);
                            break;
                        case "format":
                            s.Format = Clamp(ParseInt(val, s.Format), 0, 1);
                            break;
                        case "cursor":
                            s.DrawCursor = ParseBool(val, s.DrawCursor);
                            break;
                        case "mode":
                            s.Mode = Clamp(ParseInt(val, s.Mode), 0, 1);
                            break;
                        case "audio":
                            s.RecordAudio = ParseBool(val, s.RecordAudio);
                            break;
                        case "hotkey":
                            s.Hotkey = ParseInt(val, (int)DefaultHotkey);
                            break;
                        case "hidebar":
                            s.HideBar = ParseBool(val, s.HideBar);
                            break;
                    }
                }
            }
            catch
            {
                // 配置损坏时回到默认值，绝不让它阻止程序启动
            }

            return s;
        }

        public void Save()
        {
            try
            {
                string path = ConfigPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# LiteRec settings");
                sb.AppendLine("OutputDir=" + OutputDir);
                sb.AppendLine("Fps=" + Fps);
                sb.AppendLine("Quality=" + Quality);
                sb.AppendLine("Format=" + Format);
                sb.AppendLine("Cursor=" + (DrawCursor ? 1 : 0));
                sb.AppendLine("Mode=" + Mode);
                sb.AppendLine("Audio=" + (RecordAudio ? 1 : 0));
                sb.AppendLine("Hotkey=" + Hotkey);
                sb.AppendLine("HideBar=" + (HideBar ? 1 : 0));

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // 写配置失败不影响录制
            }
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            if (int.TryParse(s, out v)) return v;
            return fallback;
        }

        private static bool ParseBool(string s, bool fallback)
        {
            bool b;
            if (bool.TryParse(s, out b)) return b;
            int i;
            if (int.TryParse(s, out i)) return i != 0;
            return fallback;
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
