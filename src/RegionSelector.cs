using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiteRec
{
    /// <summary>
    /// 全屏半透明遮罩，拖拽选择录制区域。双击/回车=全屏。
    /// </summary>
    internal sealed class RegionSelector : Form
    {
        private Point _start;
        private Point _current;
        private bool _selecting;
        /// <summary>选区（窗口客户区坐标，仅用于绘制）。</summary>
        private Rectangle _region;
        private readonly Rectangle _virtual;

        /// <summary>
        /// 选中的区域，已换算成**屏幕坐标**（录制抓屏用的是屏幕坐标）。
        /// 之前直接返回客户区坐标，多显示器且虚拟桌面原点非 (0,0) 时会整体偏移。
        /// </summary>
        public Rectangle SelectedRegion
        {
            get
            {
                if (_region.Width <= 0 || _region.Height <= 0) return Rectangle.Empty;
                return new Rectangle(_region.X + _virtual.X, _region.Y + _virtual.Y,
                    _region.Width, _region.Height);
            }
        }

        /// <summary>整个虚拟桌面（屏幕坐标）。</summary>
        public Rectangle FullVirtualScreen { get { return _virtual; } }

        public RegionSelector()
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            _virtual = vs;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            Opacity = 0.35;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r;
            if (_selecting)
            {
                r = Normalize(_start, _current);
            }
            else if (_region.Width > 0)
            {
                r = _region;
            }
            else
            {
                DrawHint(g);
                return;
            }

            if (r.Width < 2 || r.Height < 2)
            {
                DrawHint(g);
                return;
            }

            // 高亮区域：把选区"挖空"——画四块遮罩
            using (SolidBrush mask = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                g.FillRectangle(mask, 0, 0, Width, r.Top);
                g.FillRectangle(mask, 0, r.Bottom, Width, Height - r.Bottom);
                g.FillRectangle(mask, 0, r.Top, r.Left, r.Height);
                g.FillRectangle(mask, r.Right, r.Top, Width - r.Right, r.Height);
            }

            using (Pen pen = new Pen(Color.FromArgb(84, 160, 255), 2))
            {
                g.DrawRectangle(pen, r);
            }

            // 尺寸标签
            string label = r.Width + " x " + r.Height;
            using (Font f = new Font("Segoe UI", 11f, FontStyle.Bold))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(230, 20, 20, 24)))
            using (SolidBrush fg = new SolidBrush(Color.White))
            {
                SizeF sz = g.MeasureString(label, f);
                float lx = r.X + 8;
                float ly = r.Y - sz.Height - 10;
                if (ly < 0) ly = r.Y + 10;
                RectangleF box = new RectangleF(lx, ly, sz.Width + 16, sz.Height + 8);
                using (GraphicsPath p = Rounded(Rectangle.Round(box), 6))
                {
                    g.FillPath(bg, p);
                }
                g.DrawString(label, f, fg, lx + 8, ly + 4);
            }

            DrawHint(g);
        }

        private void DrawHint(Graphics g)
        {
            string hint = "拖拽选择录制区域   ·   双击 = 整个屏幕   ·   Esc 取消";
            using (Font f = new Font("Segoe UI", 12f))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(210, 16, 16, 20)))
            using (SolidBrush fg = new SolidBrush(Color.FromArgb(235, 235, 240)))
            {
                SizeF sz = g.MeasureString(hint, f);
                float x = (Width - sz.Width) / 2;
                float y = Height / 2 - sz.Height - 30;
                using (GraphicsPath p = Rounded(
                    Rectangle.Round(new RectangleF(x - 18, y - 12, sz.Width + 36, sz.Height + 24)), 10))
                {
                    g.FillPath(bg, p);
                }
                g.DrawString(hint, f, fg, x, y);
            }
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

        private static Rectangle Normalize(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X);
            int y = Math.Min(a.Y, b.Y);
            int w = Math.Abs(a.X - b.X);
            int h = Math.Abs(a.Y - b.Y);
            return new Rectangle(x, y, w, h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _selecting = true;
                _start = e.Location;
                _current = e.Location;
                _region = Rectangle.Empty;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_selecting)
            {
                _current = e.Location;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _selecting)
            {
                _selecting = false;
                Rectangle r = Normalize(_start, e.Location);
                if (r.Width >= 32 && r.Height >= 32)
                {
                    _region = r;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    _region = Rectangle.Empty;
                    Invalidate();
                }
            }
        }

        /// <summary>整屏 = 客户区从 (0,0) 开始的全部（SelectedRegion 会换算成屏幕坐标）。</summary>
        private Rectangle WholeClient
        {
            get { return new Rectangle(0, 0, _virtual.Width, _virtual.Height); }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            _region = WholeClient;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                _region = Rectangle.Empty;
                DialogResult = DialogResult.Cancel;
                Close();
            }
            else if (e.KeyCode == Keys.Enter)
            {
                _region = WholeClient;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
