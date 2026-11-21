using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace LiteRec
{
    /// <summary>
    /// 主窗口：极简 3 按钮布局 + 设置折叠区。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly ComboBox _modeBox;
        private readonly NumericUpDown _fpsBox;
        private readonly NumericUpDown _qualityBox;
        private readonly ComboBox _formatBox;
        private readonly CheckBox _cursorBox;
        private readonly CheckBox _audioBox;
        private readonly CheckBox _hideBarBox;
        private readonly Button _hotkeyBtn;
        private bool _capturingHotkey;
        private readonly Label _statusLabel;
        private readonly Button _recordBtn;
        private readonly Button _settingsBtn;
        private readonly LinkLabel _folderLink;
        private readonly TextBox _pathBox;
        private readonly Button _browseBtn;
        private readonly Label _pathHint;
        private readonly ToolTip _pathTip;

        private readonly Panel _settingsPanel;
        private bool _settingsOpen;

        private readonly Settings _settings;

        private Rectangle _lastRegion;
        private Recorder _recorder;
        private ControlBar _bar;
        private GlobalHotkey _hotkey;
        private bool _minimizeOnRecord = true;
        private readonly object _recordLock = new object();
        private volatile bool _recordBusy;   // 开始/停止进行中，防止热键重入

        public MainForm()
        {
            _settings = Settings.Load();

            Text = "LiteRec · 轻量录屏";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(32, 32, 36);
            ForeColor = Color.FromArgb(230, 230, 235);
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            ClientSize = new Size(360, 190);
            DoubleBuffered = true;
            KeyPreview = true;   // 让窗体先收到按键，才能捕获快捷键

            // 标题栏与任务栏必须显式设置，否则 WinForms 会用默认的"应用"图标
            Icon appIcon = Program.AppIcon;
            if (appIcon != null) Icon = appIcon;

            // ---- 标题 ----
            Label title = new Label();
            title.Text = "LiteRec";
            title.Font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Point);
            title.ForeColor = Color.FromArgb(245, 245, 250);
            title.AutoSize = true;
            title.Location = new Point(20, 14);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "轻量 · 免安装 · 原生 Windows";
            sub.ForeColor = Color.FromArgb(130, 132, 140);
            sub.AutoSize = true;
            sub.Location = new Point(22, 44);
            Controls.Add(sub);

            // ---- 作者署名 ----
            Label author = new Label();
            author.Text = "Made by 一只杨洋qwq";
            author.Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
            author.ForeColor = Color.FromArgb(108, 110, 118);
            author.AutoSize = true;
            author.Location = new Point(22, 156);
            Controls.Add(author);

            // ---- 主按钮 ----
            _recordBtn = new Button();
            _recordBtn.Text = "开始录制";
            _recordBtn.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
            _recordBtn.FlatStyle = FlatStyle.Flat;
            _recordBtn.FlatAppearance.BorderSize = 0;
            _recordBtn.BackColor = Color.FromArgb(64, 132, 246);
            _recordBtn.ForeColor = Color.White;
            _recordBtn.Cursor = Cursors.Hand;
            _recordBtn.Size = new Size(150, 44);
            _recordBtn.Location = new Point(20, 76);
            _recordBtn.Click += RecordClick;
            Controls.Add(_recordBtn);

            _settingsBtn = new Button();
            _settingsBtn.Text = "设置 ▾";
            _settingsBtn.FlatStyle = FlatStyle.Flat;
            _settingsBtn.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 70);
            _settingsBtn.BackColor = Color.FromArgb(44, 44, 50);
            _settingsBtn.ForeColor = Color.FromArgb(220, 220, 226);
            _settingsBtn.Cursor = Cursors.Hand;
            _settingsBtn.Size = new Size(92, 44);
            _settingsBtn.Location = new Point(180, 76);
            _settingsBtn.Click += SettingsClick;
            Controls.Add(_settingsBtn);

            // ---- 状态行 ----
            _statusLabel = new Label();
            _statusLabel.Text = "就绪";
            _statusLabel.ForeColor = Color.FromArgb(140, 142, 150);
            _statusLabel.AutoSize = false;
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Size = new Size(198, 18);
            _statusLabel.Location = new Point(22, 130);
            Controls.Add(_statusLabel);

            _folderLink = new LinkLabel();
            _folderLink.Text = "打开录像目录";
            _folderLink.LinkColor = Color.FromArgb(96, 156, 246);
            _folderLink.ActiveLinkColor = Color.FromArgb(140, 190, 255);
            _folderLink.AutoSize = false;
            _folderLink.Size = new Size(118, 18);
            _folderLink.TextAlign = ContentAlignment.MiddleRight;
            _folderLink.Location = new Point(222, 130);
            _folderLink.LinkClicked += delegate { OpenOutputFolder(); };
            Controls.Add(_folderLink);

            // ---- 设置面板（默认隐藏） ----
            _settingsPanel = new Panel();
            _settingsPanel.Location = new Point(0, 190);
            _settingsPanel.Size = new Size(360, 304);
            _settingsPanel.BackColor = Color.FromArgb(26, 26, 30);
            _settingsPanel.Visible = false;
            Controls.Add(_settingsPanel);

            int y = 14;

            Label l1 = MakeLabel("录制模式", 20, y);
            _settingsPanel.Controls.Add(l1);

            _modeBox = new ComboBox();
            _modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _modeBox.Items.AddRange(new object[] { "全屏", "选择区域" });
            _modeBox.SelectedIndex = 0;
            _modeBox.Location = new Point(120, y - 3);
            _modeBox.Size = new Size(160, 24);
            _modeBox.SelectedIndexChanged += delegate { _lastRegion = Rectangle.Empty; };
            _settingsPanel.Controls.Add(_modeBox);

            y += 34;
            Label l2 = MakeLabel("帧率", 20, y);
            _settingsPanel.Controls.Add(l2);

            _fpsBox = new NumericUpDown();
            _fpsBox.Minimum = 5;
            _fpsBox.Maximum = 120;
            _fpsBox.Value = 30;
            _fpsBox.Location = new Point(120, y - 3);
            _fpsBox.Size = new Size(80, 24);
            _settingsPanel.Controls.Add(_fpsBox);

            Label l2b = MakeLabel("fps", 208, y);
            _settingsPanel.Controls.Add(l2b);

            y += 34;
            Label l3 = MakeLabel("画质", 20, y);
            _settingsPanel.Controls.Add(l3);

            _qualityBox = new NumericUpDown();
            _qualityBox.Minimum = 3;
            _qualityBox.Maximum = 10;
            _qualityBox.Value = 8;
            _qualityBox.Location = new Point(120, y - 3);
            _qualityBox.Size = new Size(80, 24);
            _settingsPanel.Controls.Add(_qualityBox);

            Label l3b = MakeLabel("档 (越高越清晰)", 208, y);
            _settingsPanel.Controls.Add(l3b);

            y += 34;
            Label l4 = MakeLabel("格式", 20, y);
            _settingsPanel.Controls.Add(l4);

            _formatBox = new ComboBox();
            _formatBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _formatBox.Items.AddRange(new object[] { "MP4 (H.264)", "AVI (MJPEG)" });
            _formatBox.SelectedIndex = 0;
            _formatBox.Location = new Point(120, y - 3);
            _formatBox.Size = new Size(160, 24);
            _settingsPanel.Controls.Add(_formatBox);

            y += 34;
            _cursorBox = new CheckBox();
            _cursorBox.Text = "鼠标指针";
            _cursorBox.Checked = true;
            _cursorBox.ForeColor = Color.FromArgb(210, 210, 216);
            _cursorBox.AutoSize = true;
            _cursorBox.Location = new Point(20, y);
            _settingsPanel.Controls.Add(_cursorBox);

            _audioBox = new CheckBox();
            _audioBox.Text = "录制系统声音";
            _audioBox.Checked = true;   // 默认开：用户的核心诉求
            _audioBox.ForeColor = Color.FromArgb(210, 210, 216);
            _audioBox.AutoSize = true;
            _audioBox.Location = new Point(140, y);
            _settingsPanel.Controls.Add(_audioBox);

            y += 34;
            _hideBarBox = new CheckBox();
            _hideBarBox.Text = "录制时隐藏悬浮框（靠快捷键停止）";
            _hideBarBox.Checked = false;
            _hideBarBox.ForeColor = Color.FromArgb(210, 210, 216);
            _hideBarBox.AutoSize = true;
            _hideBarBox.Location = new Point(20, y);
            _settingsPanel.Controls.Add(_hideBarBox);

            y += 34;
            Label lk = MakeLabel("快捷键", 20, y);
            _settingsPanel.Controls.Add(lk);

            _hotkeyBtn = new Button();
            _hotkeyBtn.FlatStyle = FlatStyle.Flat;
            _hotkeyBtn.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 70);
            _hotkeyBtn.BackColor = Color.FromArgb(52, 52, 60);
            _hotkeyBtn.ForeColor = Color.FromArgb(225, 225, 232);
            _hotkeyBtn.Cursor = Cursors.Hand;
            _hotkeyBtn.Size = new Size(160, 24);
            _hotkeyBtn.Location = new Point(96, y - 3);
            _hotkeyBtn.Click += HotkeyClick;
            _settingsPanel.Controls.Add(_hotkeyBtn);

            // ---- 保存位置 ----
            y += 34;
            Label l5 = MakeLabel("保存位置", 20, y);
            _settingsPanel.Controls.Add(l5);

            _pathBox = new TextBox();
            _pathBox.Location = new Point(96, y - 3);
            _pathBox.Size = new Size(182, 24);
            _pathBox.ReadOnly = true;
            _pathBox.BackColor = Color.FromArgb(40, 40, 46);
            _pathBox.ForeColor = Color.FromArgb(214, 216, 222);
            _pathBox.BorderStyle = BorderStyle.FixedSingle;
            _pathBox.TabStop = false;
            _settingsPanel.Controls.Add(_pathBox);

            _pathTip = new ToolTip();
            _pathTip.AutoPopDelay = 15000;
            _pathTip.InitialDelay = 400;
            _pathTip.ReshowDelay = 100;

            _browseBtn = new Button();
            _browseBtn.Text = "浏览…";
            _browseBtn.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            _browseBtn.FlatStyle = FlatStyle.Flat;
            _browseBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 78);
            _browseBtn.BackColor = Color.FromArgb(52, 52, 60);
            _browseBtn.ForeColor = Color.FromArgb(220, 220, 226);
            _browseBtn.Cursor = Cursors.Hand;
            _browseBtn.Size = new Size(58, 24);
            _browseBtn.Location = new Point(284, y - 3);
            _browseBtn.Click += BrowseClick;
            _settingsPanel.Controls.Add(_browseBtn);

            _pathHint = new Label();
            _pathHint.Text = "";
            _pathHint.ForeColor = Color.FromArgb(240, 120, 120);
            _pathHint.AutoSize = true;
            _pathHint.MaximumSize = new Size(320, 0);
            _pathHint.Location = new Point(20, y + 26);
            _pathHint.Visible = false;
            _settingsPanel.Controls.Add(_pathHint);

            ApplySettingsToUi();
            HookPersistence();

            // 全局热键：即使窗口最小化也能开始/停止（快捷键可配置）
            _hotkey = new GlobalHotkey();
            _hotkey.Pressed += delegate
            {
                if (InvokeRequired) BeginInvoke(new EventHandler(RecordClick), null, EventArgs.Empty);
                else RecordClick(null, EventArgs.Empty);
            };
            ApplyHotkey();

            FormClosing += OnFormClosing;
        }


        private Label MakeLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Color.FromArgb(180, 182, 190);
            l.AutoSize = true;
            l.Location = new Point(x, y);
            return l;
        }

        /// <summary>
        /// 生成不冲突的输出文件名。原来的秒级时间戳在同一秒内连续录制会互相覆盖；
        /// 这里用毫秒 + 序号，保证同名时自动递增。
        /// </summary>
        private static string MakeOutputPath(string dir, string ext)
        {
            string baseName = "LiteRec_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string path = Path.Combine(dir, baseName + ext);
            int n = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(dir, baseName + "_" + n + ext);
                n++;
            }
            return path;
        }

        private void OpenOutputFolder()
        {
            try
            {
                string dir = _settings.OutputDir;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch (Exception ex) { SetStatus("打开目录失败: " + ShortReason(ex)); }
        }

        private void SettingsClick(object sender, EventArgs e)
        {
            _settingsOpen = !_settingsOpen;
            _settingsPanel.Visible = _settingsOpen;
            _settingsBtn.Text = _settingsOpen ? "设置 ▴" : "设置 ▾";
            ClientSize = new Size(360, _settingsOpen ? 494 : 190);

            // 展开后窗口变高：若底部超出工作区，整体上移保证设置项都可见
            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
            if (Bottom > wa.Bottom)
            {
                Location = new Point(Location.X, Math.Max(wa.Top, wa.Bottom - Height));
            }
        }

        // --------------------------------------------------------------
        // 设置读写
        // --------------------------------------------------------------

        private void ApplySettingsToUi()
        {
            _modeBox.SelectedIndex = _settings.Mode;
            _fpsBox.Value = _settings.Fps;
            _qualityBox.Value = _settings.Quality;
            _formatBox.SelectedIndex = _settings.Format;
            _cursorBox.Checked = _settings.DrawCursor;
            _audioBox.Checked = _settings.RecordAudio;
            _hideBarBox.Checked = _settings.HideBar;
            UpdateHotkeyButton();

            _pathBox.Text = _settings.OutputDir;
            ValidatePath();
            UpdateFolderLink();
        }

        private void HookPersistence()
        {
            _modeBox.SelectedIndexChanged += delegate { SaveSettingsFromUi(); };
            _fpsBox.ValueChanged += delegate { SaveSettingsFromUi(); };
            _qualityBox.ValueChanged += delegate { SaveSettingsFromUi(); };
            _formatBox.SelectedIndexChanged += delegate { SaveSettingsFromUi(); };
            _cursorBox.CheckedChanged += delegate { SaveSettingsFromUi(); };
            _audioBox.CheckedChanged += delegate { SaveSettingsFromUi(); };
            _hideBarBox.CheckedChanged += delegate { SaveSettingsFromUi(); };
        }

        // --------------------------------------------------------------
        // 快捷键显示 / 捕获
        // --------------------------------------------------------------

        private static string KeysText(Keys k)
        {
            if (k == Keys.None) return "未设置";
            StringBuilder sb = new StringBuilder();
            if ((k & Keys.Control) == Keys.Control) sb.Append("Ctrl + ");
            if ((k & Keys.Shift) == Keys.Shift) sb.Append("Shift + ");
            if ((k & Keys.Alt) == Keys.Alt) sb.Append("Alt + ");
            Keys bare = k & ~Keys.Control & ~Keys.Shift & ~Keys.Alt;
            // 常用键名美化
            if (bare >= Keys.F1 && bare <= Keys.F24)
                sb.Append("F" + ((int)(bare - Keys.F1) + 1));
            else if (bare == Keys.Space) sb.Append("空格");
            else if (bare == Keys.Enter) sb.Append("Enter");
            else sb.Append(bare.ToString());
            return sb.ToString();
        }

        private void UpdateHotkeyButton()
        {
            if (_hotkeyBtn == null) return;
            _hotkeyBtn.Text = _capturingHotkey
                ? "按下要设置的按键…"
                : "开始 / 停止  " + KeysText((Keys)_settings.Hotkey);
        }

        private void HotkeyClick(object sender, EventArgs e)
        {
            _capturingHotkey = true;
            // 捕获期间临时注销全局热键，避免按到旧键时误触发录制
            if (_hotkey != null) _hotkey.Unregister();
            UpdateHotkeyButton();
            SetStatus("请按下要设置为快捷键的按键（Esc 取消）");
            // 把焦点从按钮移回窗体，否则按键会被按钮自身消费，窗体收不到 KeyDown
            ActiveControl = null;
            Focus();
        }

        /// <summary>捕获按键作为新快捷键。返回 true 表示已消费该按键。</summary>
        private bool CaptureHotkey(KeyEventArgs e)
        {
            if (!_capturingHotkey) return false;
            _capturingHotkey = false;

            if (e.KeyCode == Keys.Escape)
            {
                SetStatus("已取消快捷键设置");
                ApplyHotkey();   // 取消后恢复原热键
                UpdateHotkeyButton();
                return true;
            }

            Keys combo = e.KeyData;
            // 只保留修饰键 + 一个主按键
            Keys mods = Keys.None;
            if ((combo & Keys.Control) == Keys.Control) mods |= Keys.Control;
            if ((combo & Keys.Shift) == Keys.Shift) mods |= Keys.Shift;
            if ((combo & Keys.Alt) == Keys.Alt) mods |= Keys.Alt;
            Keys bare = combo & ~Keys.Control & ~Keys.Shift & ~Keys.Alt;

            // 单独的修饰键不算有效快捷键
            if (bare == Keys.ControlKey || bare == Keys.ShiftKey || bare == Keys.Menu
                || bare == Keys.LWin || bare == Keys.RWin)
            {
                SetStatus("请按一个非修饰键（可搭配 Ctrl/Shift/Alt）");
                UpdateHotkeyButton();
                return true;
            }

            Keys final = mods | bare;
            _settings.Hotkey = (int)final;
            _settings.Save();
            ApplyHotkey();
            UpdateHotkeyButton();
            SetStatus("快捷键已设为 " + KeysText(final));
            return true;
        }

        private void ApplyHotkey()
        {
            if (_hotkey == null) return;
            bool ok = _hotkey.Register((Keys)_settings.Hotkey);
            if (!ok)
            {
                // 注册失败（被占用）时回退到 F9，保证至少有热键可用
                _settings.Hotkey = (int)Settings.DefaultHotkey;
                _hotkey.Register(Settings.DefaultHotkey);
                _settings.Save();
                SetStatus("该快捷键被占用，已恢复默认 F9");
                if (_hotkeyBtn != null) UpdateHotkeyButton();
            }
        }

        private void SaveSettingsFromUi()
        {
            _settings.Mode = _modeBox.SelectedIndex;
            _settings.Fps = (int)_fpsBox.Value;
            _settings.Quality = (int)_qualityBox.Value;
            _settings.Format = _formatBox.SelectedIndex;
            _settings.DrawCursor = _cursorBox.Checked;
            _settings.RecordAudio = _audioBox.Checked;
            _settings.HideBar = _hideBarBox.Checked;
            _settings.Save();
        }

        /// <summary>
        /// 检查保存目录当前是否可用（存在且可写）。
        /// 外接盘拔掉、网络盘断线都是真实会发生的情况，必须在录制前就发现。
        /// </summary>
        private bool ValidatePath()
        {
            string dir = _settings.OutputDir;
            string err = null;

            try
            {
                if (string.IsNullOrEmpty(dir))
                {
                    err = "未设置保存位置";
                }
                else if (!Directory.Exists(dir))
                {
                    // 目录可能只是还没建（默认目录首次运行），尝试创建
                    try
                    {
                        Directory.CreateDirectory(dir);
                    }
                    catch
                    {
                        err = "目录不存在且无法创建（磁盘可能已拔出或离线）";
                    }
                }

                if (err == null)
                {
                    // 真正写一次，确认有权限、有空间
                    string probe = Path.Combine(dir, ".literec_write_test");
                    using (FileStream fs = new FileStream(probe, FileMode.Create,
                        FileAccess.Write, FileShare.None))
                    {
                        fs.WriteByte(0);
                    }
                    try { File.Delete(probe); } catch { }
                }
            }
            catch (Exception ex)
            {
                err = ShortReason(ex);
            }

            if (err == null)
            {
                _pathHint.Visible = false;
                _pathBox.ForeColor = Color.FromArgb(214, 216, 222);
                return true;
            }

            _pathHint.Text = "⚠ " + err + "，请重新选择";
            _pathHint.Visible = true;
            _pathBox.ForeColor = Color.FromArgb(245, 130, 130);
            return false;
        }

        private static string ShortReason(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "没有写入权限";
            if (ex is DirectoryNotFoundException) return "目录不存在";
            if (ex is PathTooLongException) return "路径过长";
            if (ex is IOException) return "磁盘不可写（可能已满或已离线）";
            return ex.Message;
        }

        private void BrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择录像保存位置";
                dlg.ShowNewFolderButton = true;
                if (Directory.Exists(_settings.OutputDir))
                {
                    dlg.SelectedPath = _settings.OutputDir;
                }

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string chosen = dlg.SelectedPath;
                if (string.IsNullOrEmpty(chosen)) return;

                string previous = _settings.OutputDir;
                _settings.OutputDir = chosen;
                _pathBox.Text = chosen;

                if (!ValidatePath())
                {
                    // 选了不可写的目录：保留选择让用户看到问题，但也给出退路
                    DialogResult r = MessageBox.Show(this,
                        _pathHint.Text + "\n\n要恢复到原来的位置吗？",
                        "LiteRec", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r == DialogResult.Yes)
                    {
                        _settings.OutputDir = previous;
                        _pathBox.Text = previous;
                        ValidatePath();
                        _settings.Save();
                        UpdateFolderLink();
                        return;
                    }
                }

                _settings.Save();
                UpdateFolderLink();
                SetStatus("已改为 " + ShortFolder(chosen));
            }
        }

        private void UpdateFolderLink()
        {
            // 输入框可能截断长路径，悬停提示给完整路径
            if (_pathBox != null && _settings != null)
            {
                _pathBox.Text = _settings.OutputDir;
                _pathTip.SetToolTip(_pathBox, _settings.OutputDir);
            }
        }

        private void RestoreDefaultPath()
        {
            _settings.OutputDir = Settings.DefaultOutputDir;
            _pathBox.Text = _settings.OutputDir;
            ValidatePath();
            _settings.Save();
        }

        private void SetStatus(string s)
        {
            if (_statusLabel != null) _statusLabel.Text = s;
        }

        private void RecordClick(object sender, EventArgs e)
        {
            // 开始/停止是耗时操作（启动编码器、等待写盘），期间锁住防止重入：
            // 连按两次热键不能变成"启动两个录制线程"或"停止两次"。
            lock (_recordLock)
            {
                if (_recordBusy) return;
                _recordBusy = true;
            }

            try
            {
                if (_recorder != null && _recorder.IsRunning)
                {
                    StopRecording();
                }
                else
                {
                    StartRecording();
                }
            }
            finally
            {
                _recordBusy = false;
            }
        }

        private void StartRecording()
        {
            // 录制前先确认保存位置可用。否则录到最后才发现写不进去，
            // 用户会白录一场——这是最该在开始前拦住的失败。
            if (!ValidatePath())
            {
                SetStatus("保存位置不可用");
                DialogResult r = MessageBox.Show(this,
                    "保存位置不可用：\n\n" + _settings.OutputDir + "\n\n" + _pathHint.Text
                    + "\n\n要立即选择一个新位置吗？",
                    "LiteRec", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) BrowseClick(null, EventArgs.Empty);
                return;
            }

            Rectangle region;
            if (_modeBox.SelectedIndex == 0)
            {
                region = SystemInformation.VirtualScreen;
            }
            else
            {
                if (_lastRegion.Width < 32 || _lastRegion.Height < 32)
                {
                    using (RegionSelector sel = new RegionSelector())
                    {
                        Hide();
                        DialogResult dr = sel.ShowDialog();
                        Show();
                        if (dr != DialogResult.OK || sel.SelectedRegion.Width < 32)
                        {
                            SetStatus("已取消");
                            return;
                        }
                        _lastRegion = sel.SelectedRegion;
                    }
                }
                region = _lastRegion;
            }

            OutputFormat fmt = _formatBox.SelectedIndex == 0 ? OutputFormat.Mp4 : OutputFormat.Avi;
            string ext = fmt == OutputFormat.Mp4 ? ".mp4" : ".avi";
            string path = MakeOutputPath(_settings.OutputDir, ext);

            RecorderOptions opt = new RecorderOptions();
            opt.Source = region;
            opt.Fps = (int)_fpsBox.Value;
            opt.JpegQuality = (int)_qualityBox.Value * 10;
            opt.BitrateKbps = (int)_qualityBox.Value * 1000;
            opt.DrawCursor = _cursorBox.Checked;
            opt.CursorScale = Program.CursorScale;
            opt.CursorImagePath = Program.CursorImagePath;
            // 音频仅 MP4 支持；AVI 容器没有音轨，自动禁用
            opt.RecordAudio = _audioBox.Checked && fmt == OutputFormat.Mp4;
            opt.Format = fmt;
            opt.OutputPath = path;

            try
            {
                _recorder = new Recorder(opt);
                _recorder.Start();

                // 隐藏悬浮框模式：不显示控制条，完全靠全局快捷键停止
                if (!_settings.HideBar)
                {
                    _bar = new ControlBar(_recorder);
                    // 走 RecordClick 而非直接 StopRecording，共用重入锁
                    _bar.StopRequested += delegate { RecordClick(null, EventArgs.Empty); };
                    _bar.Show();
                }

                _recordBtn.Text = "停止录制";
                _recordBtn.BackColor = Color.FromArgb(226, 74, 74);
                SetStatus("录制中 → " + Path.GetFileName(_recorder.ActualPath));

                if (_settings.HideBar)
                {
                    SetStatus("录制中（悬浮框已隐藏）· 按 "
                        + KeysText((Keys)_settings.Hotkey) + " 停止");
                }

                if (_recorder.FallbackReason != null)
                {
                    // 编码器回退是静默发生的，必须让用户知道实际拿到的是什么格式
                    SetStatus("H.264 不可用，已自动改用 AVI → "
                        + Path.GetFileName(_recorder.ActualPath));
                }

                // 音频失败要在开始前就说清楚，别等录完才发现是静音文件。
                // 优先级最高（覆盖上面的提示），因为这是用户最容易忽略的失败。
                if (opt.RecordAudio && !string.IsNullOrEmpty(_recorder.AudioError))
                {
                    SetStatus("⚠ 系统声音未启动：" + _recorder.AudioError + "（本次只有画面）");
                    MessageBox.Show(this,
                        "系统声音没能启动，本次录像将只有画面。\n\n原因：" + _recorder.AudioError
                        + "\n\n常见原因：没有播放设备、音频设备被占用或被禁用。",
                        "LiteRec", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                if (_minimizeOnRecord) WindowState = FormWindowState.Minimized;
            }
            catch (Exception ex)
            {
                if (_recorder != null)
                {
                    _recorder.Dispose();
                    _recorder = null;
                }
                SetStatus("启动失败");
                MessageBox.Show(this,
                    "无法开始录制：\n\n" + ex.Message +
                    "\n\n如果 MP4 失败，可在设置里改用 AVI (MJPEG) 格式。",
                    "LiteRec", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StopRecording()
        {
            if (_recorder == null) return;

            try
            {
                _recorder.Stop();
                SetStatus("正在保存…");
            }
            catch (Exception ex)
            {
                SetStatus("停止失败: " + ex.Message);
            }

            try
            {
                if (_bar != null) { _bar.Close(); _bar.Dispose(); _bar = null; }
            }
            catch { }

            try
            {
                _recorder.Dispose();
            }
            catch { }
            _recorder = null;

            _recordBtn.Text = "开始录制";
            _recordBtn.BackColor = Color.FromArgb(64, 132, 246);

            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;

            SetStatus("已保存到 " + ShortFolder(_settings.OutputDir));
        }

        /// <summary>
        /// 状态栏宽度有限，长路径会把链接挤掉。只显示最后一级目录名，
        /// 完整路径在输入框和悬停提示里都能看到。
        /// </summary>
        private static string ShortFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            try
            {
                string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) return dir;   // 根目录如 D:\
                return name;
            }
            catch { return dir; }
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            // 捕获模式：把任意按键设为新快捷键
            if (CaptureHotkey(e))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // 普通模式：按配置的快捷键开始/停止
            if ((int)e.KeyData == _settings.Hotkey || e.KeyCode == (Keys)_settings.Hotkey)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                RecordClick(null, EventArgs.Empty);
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_recorder != null && _recorder.IsRunning)
            {
                DialogResult r = MessageBox.Show(this,
                    "正在录制中，确定要停止并退出吗？",
                    "LiteRec", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                StopRecording();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_recorder != null)
                {
                    try { _recorder.Dispose(); } catch { }
                    _recorder = null;
                }
                if (_bar != null)
                {
                    try { _bar.Dispose(); } catch { }
                    _bar = null;
                }
                if (_hotkey != null)
                {
                    try { _hotkey.Dispose(); } catch { }
                    _hotkey = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
