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
        private readonly DispatcherTimer _saveTimer;
        private string? _carPath;
        private bool _applyingBounds;
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
            _carPath = controller.Session?.CarPath;
            if (string.IsNullOrEmpty(_carPath)) _carPath = null;
            var carBounds = s.GetCarBounds(_carPath);
            if (carBounds != null) SetBoundsFrom(carBounds);
            else if (_carPath == null) PlaceBottomCenter();
            EnsureOnScreen();

            // Auto-save shortly after the user stops moving/resizing, so a drag isn't a write per pixel.
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveBounds();
            };

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

            // Hooked after the initial bounds are applied so startup doesn't count as a change.
            LocationChanged += (_, _) => OnBoundsChanged();
            SizeChanged += (_, _) => OnBoundsChanged();
        }

        private void OnBoundsChanged()
        {
            if (_applyingBounds) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>
        /// Switches to the car's saved position and size. A car without saved bounds keeps the
        /// overlay where it is; moving or resizing it then saves bounds for that car.
        /// </summary>
        public void ApplyCar(string? carPath)
        {
            if (string.Equals(carPath, _carPath, StringComparison.Ordinal)) return;
            if (_saveTimer.IsEnabled)
            {
                // Pending change belongs to the previous car.
                _saveTimer.Stop();
                SaveBounds();
            }
            _carPath = carPath;

            var b = _controller.Settings.GetCarBounds(carPath);
            if (b == null) return;
            _applyingBounds = true;
            try
            {
                SetBoundsFrom(b);
                EnsureOnScreen();
            }
            finally
            {
                _applyingBounds = false;
            }
        }

        /// <summary>Until a car is known, the overlay sits at the bottom centre of the primary screen.</summary>
        private void PlaceBottomCenter()
        {
            const double margin = 40;
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Bottom - Height - margin;
        }

        private void SetBoundsFrom(OverlayBounds b)
        {
            Left = b.Left;
            Top = b.Top;
            Width = Math.Max(MinWidth, b.Width);
            Height = Math.Max(MinHeight, b.Height);
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
                if (_resizeEdge != 0) NativeMethods.ReleaseCapture();
                NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT, 0);
                _frame.BorderBrush = Brushes.Transparent;
                SaveBounds();
                _controller.RefreshOverlayVisibility(); // may hide again if iRacing isn't in a session
            }
            _controller.OnTrayStateChanged();
        }

        /// <summary>Flips the user's hide/show choice; whether it's actually on screen is up to the controller.</summary>
        public void ToggleVisible()
        {
            var s = _controller.Settings;
            if (!s.OverlayHidden && IsUnlocked) SetUnlocked(false);
            s.OverlayHidden = !s.OverlayHidden;
            s.Save();
            _controller.RefreshOverlayVisibility();
            _controller.OnTrayStateChanged();
        }

        public void SetShown(bool show)
        {
            if (show && !IsVisible) ShowOverlay();
            else if (!show && IsVisible) Hide();
        }

        public void SaveBounds()
        {
            var s = _controller.Settings;
            if (_carPath == null)
            {
                // No car yet: the position is always bottom centre, so only the size is kept.
                s.Width = ActualWidth > 0 ? ActualWidth : Width;
                s.Height = ActualHeight > 0 ? ActualHeight : Height;
                s.Save();
                return;
            }
            s.SetBounds(_carPath, Left, Top,
                ActualWidth > 0 ? ActualWidth : Width,
                ActualHeight > 0 ? ActualHeight : Height);
            s.Save();
        }

        protected override void OnClosed(EventArgs e)
        {
            _topmostTimer.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveBounds();
            }
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
                if (_resizeEdge != 0) return new IntPtr(_resizeEdge);
                return new IntPtr(IsUnlocked ? HitTest(lParam) : NativeMethods.HTTRANSPARENT);
            }

            // The window has no WS_THICKFRAME (it's a transparent layered popup), so Windows ignores
            // sizing hit-test codes. Edge/corner drags are done here instead: capture the mouse on
            // button-down and resize from the cursor delta until the button is released.
            if (msg == NativeMethods.WM_NCLBUTTONDOWN && IsUnlocked && IsSizingEdge(wParam.ToInt32()))
            {
                BeginResize(wParam.ToInt32());
                handled = true;
                return IntPtr.Zero;
            }
            if (_resizeEdge != 0)
            {
                switch (msg)
                {
                    case NativeMethods.WM_MOUSEMOVE:
                        ContinueResize();
                        handled = true;
                        return IntPtr.Zero;
                    case NativeMethods.WM_LBUTTONUP:
                        NativeMethods.ReleaseCapture(); // triggers WM_CAPTURECHANGED
                        handled = true;
                        return IntPtr.Zero;
                    case NativeMethods.WM_CAPTURECHANGED:
                        _resizeEdge = 0;
                        break;
                }
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

        private int _resizeEdge;
        private NativeMethods.POINT _resizeStartCursor;
        private NativeMethods.RECT _resizeStartRect;

        private static bool IsSizingEdge(int hit) => hit >= NativeMethods.HTLEFT && hit <= NativeMethods.HTBOTTOMRIGHT;

        private void BeginResize(int edge)
        {
            if (!NativeMethods.GetCursorPos(out _resizeStartCursor) || !NativeMethods.GetWindowRect(_hwnd, out _resizeStartRect)) return;
            _resizeEdge = edge;
            NativeMethods.SetCapture(_hwnd);
        }

        private void ContinueResize()
        {
            if (!NativeMethods.GetCursorPos(out var p)) return;
            int dx = p.X - _resizeStartCursor.X, dy = p.Y - _resizeStartCursor.Y;
            var r = _resizeStartRect;
            int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

            var dpi = VisualTreeHelper.GetDpi(this);
            int minW = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
            int minH = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);

            int e = _resizeEdge;
            bool l = e == NativeMethods.HTLEFT || e == NativeMethods.HTTOPLEFT || e == NativeMethods.HTBOTTOMLEFT;
            bool rt = e == NativeMethods.HTRIGHT || e == NativeMethods.HTTOPRIGHT || e == NativeMethods.HTBOTTOMRIGHT;
            bool t = e == NativeMethods.HTTOP || e == NativeMethods.HTTOPLEFT || e == NativeMethods.HTTOPRIGHT;
            bool b = e == NativeMethods.HTBOTTOM || e == NativeMethods.HTBOTTOMLEFT || e == NativeMethods.HTBOTTOMRIGHT;

            if (l) left = Math.Min(r.Left + dx, right - minW);
            if (rt) right = Math.Max(r.Right + dx, left + minW);
            if (t) top = Math.Min(r.Top + dy, bottom - minH);
            if (b) bottom = Math.Max(r.Bottom + dy, top + minH);

            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
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
