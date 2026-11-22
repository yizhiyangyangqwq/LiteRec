using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace LiteRec
{
    /// <summary>
    /// 置顶的录制控制条：拖动移动、显示时长/帧率、停止按钮。
    /// </summary>
    internal sealed class ControlBar : Form
    {
        private readonly Recorder _rec;
        private readonly Timer _uiTimer;
        private readonly Font _font;
        private readonly Font _fontSmall;
        private bool _dragging;
        private Point _dragOffset;
        private bool _hoverStop;

        private Rectangle _stopRect;

        public event EventHandler StopRequested;

        public ControlBar(Recorder rec)
        {
            _rec = rec;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(28, 28, 30);
            DoubleBuffered = true;
            ClientSize = new Size(232, 52);
            Text = "LiteRec";
            // 先算好停止按钮区域：否则在首次 OnPaint 之前点击会被当成拖动
            _stopRect = new Rectangle(ClientSize.Width - 48, 10, 34, 32);

            Icon appIcon = Program.AppIcon;
            if (appIcon != null) Icon = appIcon;

            _font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            _fontSmall = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            // 定位到主屏右上角
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 24, wa.Top + 24);

            _uiTimer = new Timer();
            _uiTimer.Interval = 200;
            _uiTimer.Tick += delegate { Invalidate(); };
            _uiTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (GraphicsPath path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(28, 28, 30)))
            {
                g.FillPath(bg, path);
            }

            // 录制红点（呼吸效果）
            int pulse = (int)(Environment.TickCount / 20 % 100);
            int alpha = 150 + (int)(105 * Math.Abs(Math.Sin(pulse * Math.PI / 100.0)));
            using (SolidBrush red = new SolidBrush(Color.FromArgb(alpha, 235, 70, 70)))
            {
                g.FillEllipse(red, 14, 20, 12, 12);
            }

            // 时长
            TimeSpan t = _rec.Elapsed;
            string dur = string.Format("{0:00}:{1:00}:{2:00}",
                (int)t.TotalHours, t.Minutes, t.Seconds);
            using (SolidBrush white = new SolidBrush(Color.FromArgb(242, 242, 245)))
            {
                g.DrawString(dur, _font, white, 34, 8);
            }

            // 帧率 / 文件名提示
            string sub = string.Format("{0:0} fps  ·  {1}", _rec.CurrentFps, FormatSize(_rec.OutputBytes));
            using (SolidBrush gray = new SolidBrush(Color.FromArgb(150, 152, 158)))
            {
                g.DrawString(sub, _fontSmall, gray, 34, 30);
            }

            // 停止按钮
            _stopRect = new Rectangle(Width - 48, 10, 34, 32);
            using (GraphicsPath p2 = Rounded(_stopRect, 7))
            using (SolidBrush bb = new SolidBrush(_hoverStop
                ? Color.FromArgb(235, 70, 70) : Color.FromArgb(56, 56, 62)))
            {
                g.FillPath(bb, p2);
            }
            using (SolidBrush sq = new SolidBrush(Color.FromArgb(250, 250, 252)))
            {
                g.FillRectangle(sq, _stopRect.X + 12, _stopRect.Y + 11, 10, 10);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.0") + " MB";
            return (bytes / 1073741824.0).ToString("0.00") + " GB";
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_stopRect.Contains(e.Location))
            {
                EventHandler h = StopRequested;
                if (h != null) h(this, EventArgs.Empty);
                return;
            }
            _dragging = true;
            _dragOffset = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool hover = _stopRect.Contains(e.Location);
            if (hover != _hoverStop)
            {
                _hoverStop = hover;
                Cursor = hover ? Cursors.Hand : Cursors.SizeAll;
                Invalidate();
            }

            if (_dragging)
            {
                Point p = PointToScreen(e.Location);
                Location = new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 不抢焦点，避免干扰被录制的程序
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_uiTimer != null) { _uiTimer.Stop(); _uiTimer.Dispose(); }
                if (_font != null) _font.Dispose();
                if (_fontSmall != null) _fontSmall.Dispose();
            }
            base.Dispose(disposing);
        }

        private static class NativeMethods
        {
            public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOMOVE = 0x0002;
            public const uint SWP_NOACTIVATE = 0x0010;

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                int X, int Y, int cx, int cy, uint uFlags);
        }
    }
}
