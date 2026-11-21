using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiteRec
{
    /// <summary>
    /// 系统级全局热键。默认 F9 = 开始/停止录制。
    /// 用隐藏消息窗口接收 WM_HOTKEY，窗口最小化或后台也能触发。
    /// </summary>
    internal sealed class GlobalHotkey : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_ALT = 0x0001;
        private const int HOTKEY_ID = 0xB1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly MessageWindow _window;
        private readonly int _id;
        private bool _registered;

        public event EventHandler Pressed;

        /// <summary>
        /// 注册全局热键。key 是 Keys 枚举值，可含修饰键（Ctrl/Shift/Alt）。
        /// 失败（例如已被别的程序占用）返回 false，不抛异常。
        /// </summary>
        public bool Register(Keys key)
        {
            // 先注销旧的，再注册新的（换快捷键时调用）
            if (_registered)
            {
                try { UnregisterHotKey(_window.Handle, _id); } catch { }
                _registered = false;
            }

            uint mods = MOD_NOREPEAT;
            Keys bare = key;
            if ((key & Keys.Control) == Keys.Control) { mods |= MOD_CONTROL; bare &= ~Keys.Control; }
            if ((key & Keys.Shift) == Keys.Shift) { mods |= MOD_SHIFT; bare &= ~Keys.Shift; }
            if ((key & Keys.Alt) == Keys.Alt) { mods |= MOD_ALT; bare &= ~Keys.Alt; }

            _key = bare;
            try
            {
                _registered = RegisterHotKey(_window.Handle, _id, mods, (uint)bare);
            }
            catch
            {
                _registered = false;
            }
            return _registered;
        }

        public Keys RegisteredKey { get { return _key; } }
        private Keys _key;

        public bool IsRegistered { get { return _registered; } }

        /// <summary>临时注销热键（捕获新按键期间调用）。</summary>
        public void Unregister()
        {
            if (_registered)
            {
                try { UnregisterHotKey(_window.Handle, _id); } catch { }
                _registered = false;
            }
        }

        public GlobalHotkey()
        {
            _id = HOTKEY_ID;
            _window = new MessageWindow(this);
        }

        private void OnHotkey()
        {
            EventHandler h = Pressed;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_registered)
            {
                try { UnregisterHotKey(_window.Handle, _id); } catch { }
                _registered = false;
            }
            if (_window != null) _window.DestroyHandle();
        }

        /// <summary>只用来接收 WM_HOTKEY 的不可见原生窗口。</summary>
        private sealed class MessageWindow : NativeWindow
        {
            private readonly GlobalHotkey _owner;

            public MessageWindow(GlobalHotkey owner)
            {
                _owner = owner;
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == _owner._id)
                {
                    _owner.OnHotkey();
                    return;
                }
                base.WndProc(ref m);
            }
        }
    }
}
