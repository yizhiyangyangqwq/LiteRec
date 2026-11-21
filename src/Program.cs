using System;
using System.Drawing;
using System.Windows.Forms;

namespace LiteRec
{
    internal static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy,
            IntPtr[] icons, uint[] ids, uint nIcons, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        /// <summary>
        /// exe 的窗口图标。直接从 exe 自身的 PE 资源里按需取尺寸，
        /// 而不是把同一个 .ico 再嵌一份进托管资源——那会让体积翻倍。
        /// </summary>
        internal static Icon AppIcon
        {
            get
            {
                if (_appIcon == null)
                {
                    _appIcon = ExtractFromSelf(48)  // 优先高分辨率，Win 会自行缩放
                             ?? ExtractFromSelf(32)
                             ?? ExtractFromSelf(16);
                }
                return _appIcon;
            }
        }

        private static Icon _appIcon;

        private static Icon ExtractFromSelf(int size)
        {
            try
            {
                IntPtr[] arr = new IntPtr[1];
                uint[] ids = new uint[1];
                uint n = PrivateExtractIconsW(Application.ExecutablePath, 0, size, size,
                    arr, ids, 1, 0);
                if (n > 0 && arr[0] != IntPtr.Zero)
                {
                    // Icon.FromHandle 不拥有句柄，Clone 后即可安全释放原生句柄
                    using (Icon tmp = Icon.FromHandle(arr[0]))
                    {
                        Icon copy = (Icon)tmp.Clone();
                        DestroyIcon(arr[0]);
                        return copy;
                    }
                }
            }
            catch { }
            return null;
        }

        [STAThread]
        private static void Main()
        {
            // DPI 感知：保证坐标与像素 1:1，避免缩放导致模糊/裁切
            try { Gdi.SetProcessDPIAware(); }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 全局异常兜底，避免录屏中途静默崩溃
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("发生错误：\n\n" + e.Exception.Message, "LiteRec",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new MainForm());
        }
    }
}
