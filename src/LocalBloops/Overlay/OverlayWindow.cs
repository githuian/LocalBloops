using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalBloops.Overlay
{
    /// <summary>
    /// Borderless, transparent, always-on-top, click-through window hosting the overlay renderer.
    /// Ctrl+Shift+L unlocks it for dragging/resizing; Ctrl+Shift+H hides/shows it.
    /// </summary>
    public sealed class OverlayWindow : Window
    {
        private const int HotkeyUnlock = 0xB10C;
        private const int HotkeyHide = 0xB10D;

        private readonly AppController _controller;
        private readonly Border _frame;
        private readonly DispatcherTimer _topmostTimer;
        private IntPtr _hwnd;
        private HwndSource? _source;

        public OverlayWindow(AppController controller)
        {
            _controller = controller;
            var s = controller.Settings;

            Title = "LocalBloops";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            MinWidth = 260;
            MinHeight = 120;
            Left = s.Left;
            Top = s.Top;
            Width = Math.Max(MinWidth, s.Width);
            Height = Math.Max(MinHeight, s.Height);
            EnsureOnScreen();

            _frame = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Child = new OverlayView(controller),
            };
            Content = _frame;

            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _topmostTimer.Tick += (_, _) =>
            {
                AttachToSim();
                AssertTopmost();
            };
        }

        public bool IsUnlocked { get; private set; }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);

            NativeMethods.SetExStyle(_hwnd,
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT,
                0);

            uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
            var failed = new List<string>();
            if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyUnlock, mods, NativeMethods.VK_L)) failed.Add("Ctrl+Shift+L");
            if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyHide, mods, NativeMethods.VK_H)) failed.Add("Ctrl+Shift+H");
            HotkeyError = failed.Count == 0 ? null : string.Join(", ", failed) + " is in use by another app; use the tray menu instead.";

            _topmostTimer.Start();
            AttachToSim();
            AssertTopmost();
        }

        /// <summary>
        /// In borderless mode the sim makes its own window topmost and keeps re-asserting it, which
        /// beats any periodic HWND_TOPMOST from us. Windows always keeps an owned window above its
        /// owner, so while the sim window exists we make it our owner.
        /// </summary>
        private void AttachToSim()
        {
            if (_hwnd == IntPtr.Zero) return;
            IntPtr sim = NativeMethods.FindWindowW(SimWindowClass, null);
            if (sim == _simOwner) return;
            _simOwner = sim;
            new WindowInteropHelper(this).Owner = sim; // IntPtr.Zero detaches when the sim is gone
        }

        private const string SimWindowClass = "SimWinClass";
        private IntPtr _simOwner;

        /// <summary>Set when a global hotkey couldn't be registered.</summary>
        public string? HotkeyError { get; private set; }

        public void SetUnlocked(bool unlocked)
        {
            if (_hwnd == IntPtr.Zero || unlocked == IsUnlocked) return;
            IsUnlocked = unlocked;

            // Move/resize is done by WM_NCHITTEST (caption + sizing borders), so no DragMove
            // and no ResizeMode change are needed; only click-through is toggled here.
            if (unlocked)
            {
                if (!IsVisible) ShowOverlay();
                NativeMethods.SetExStyle(_hwnd, 0, NativeMethods.WS_EX_TRANSPARENT);
                _frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xA0, 0x20));
            }
            else
            {
                NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT, 0);
                _frame.BorderBrush = Brushes.Transparent;
                SaveBounds();
            }
            _controller.OnTrayStateChanged();
        }

        public void ToggleVisible()
        {
            if (IsVisible)
            {
                if (IsUnlocked) SetUnlocked(false);
                Hide();
                _controller.Settings.OverlayHidden = true;
            }
            else
            {
                ShowOverlay();
                _controller.Settings.OverlayHidden = false;
            }
            _controller.Settings.Save();
            _controller.OnTrayStateChanged();
        }

        public void SaveBounds()
        {
            var s = _controller.Settings;
            s.Left = Left;
            s.Top = Top;
            s.Width = ActualWidth > 0 ? ActualWidth : Width;
            s.Height = ActualHeight > 0 ? ActualHeight : Height;
            s.Save();
        }

        protected override void OnClosed(EventArgs e)
        {
            _topmostTimer.Stop();
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_hwnd, HotkeyUnlock);
                NativeMethods.UnregisterHotKey(_hwnd, HotkeyHide);
            }
            _source?.RemoveHook(WndProc);
            base.OnClosed(e);
        }

        private void ShowOverlay()
        {
            Show();
            AssertTopmost();
        }

        private void AssertTopmost()
        {
            if (_hwnd == IntPtr.Zero || !IsVisible) return;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_NCHITTEST)
            {
                handled = true;
                return new IntPtr(IsUnlocked ? HitTest(lParam) : NativeMethods.HTTRANSPARENT);
            }

            if (msg == NativeMethods.WM_HOTKEY)
            {
                switch (wParam.ToInt32())
                {
                    case HotkeyUnlock:
                        SetUnlocked(!IsUnlocked);
                        handled = true;
                        break;
                    case HotkeyHide:
                        ToggleVisible();
                        handled = true;
                        break;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>Edges and corners resize (bottom-right corner is extra large); everything else moves.</summary>
        private int HitTest(IntPtr lParam)
        {
            long lp = lParam.ToInt64();
            int x = (short)(lp & 0xFFFF);
            int y = (short)((lp >> 16) & 0xFFFF);
            if (!NativeMethods.GetWindowRect(_hwnd, out var r)) return NativeMethods.HTCAPTION;

            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int edge = (int)Math.Ceiling(8 * scale);
            int grip = (int)Math.Ceiling(18 * scale);

            if (x >= r.Right - grip && y >= r.Bottom - grip) return NativeMethods.HTBOTTOMRIGHT;

            bool left = x < r.Left + edge, right = x >= r.Right - edge;
            bool top = y < r.Top + edge, bottom = y >= r.Bottom - edge;
            if (top && left) return NativeMethods.HTTOPLEFT;
            if (top && right) return NativeMethods.HTTOPRIGHT;
            if (bottom && left) return NativeMethods.HTBOTTOMLEFT;
            if (bottom && right) return NativeMethods.HTBOTTOMRIGHT;
            if (left) return NativeMethods.HTLEFT;
            if (right) return NativeMethods.HTRIGHT;
            if (top) return NativeMethods.HTTOP;
            if (bottom) return NativeMethods.HTBOTTOM;
            return NativeMethods.HTCAPTION;
        }

        private void EnsureOnScreen()
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!vs.IntersectsWith(new Rect(Left, Top, Width, Height)))
            {
                Left = vs.Left + 40;
                Top = vs.Top + 40;
            }
        }
    }
}
