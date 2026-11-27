using System;
using LocalBloops.Laps;
using LocalBloops.Overlay;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace LocalBloops
{
    /// <summary>WinForms NotifyIcon hosted on the WPF dispatcher thread.</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly AppController _controller;
        private readonly WF.NotifyIcon _icon;
        private readonly SD.Icon _iconImage;
        private readonly IntPtr _hIcon;
        private readonly WF.ToolStripMenuItem _status;
        private readonly WF.ToolStripMenuItem _reference;
        private readonly WF.ToolStripMenuItem _unlock;
        private readonly WF.ToolStripMenuItem _hide;
        private readonly WF.ToolStripMenuItem _mph;
        private readonly WF.ToolStripMenuItem _speedChart;
        private readonly WF.ToolStripMenuItem _offTrack;
        private readonly WF.ToolStripMenuItem _startup;

        public TrayIcon(AppController controller)
        {
            _controller = controller;

            _status = new WF.ToolStripMenuItem { Enabled = false };
            _reference = new WF.ToolStripMenuItem { Enabled = false };
            _unlock = new WF.ToolStripMenuItem("Unlock overlay", null, (_, _) => _controller.ToggleOverlayUnlocked()) { ShortcutKeyDisplayString = "Ctrl+Shift+L" };
            _hide = new WF.ToolStripMenuItem("Hide overlay", null, (_, _) => _controller.ToggleOverlayVisible()) { ShortcutKeyDisplayString = "Ctrl+Shift+H" };
            _mph = new WF.ToolStripMenuItem("Speed in mph", null, (_, _) => { _controller.SetUseMph(!_controller.Settings.UseMph); Refresh(); });
            _speedChart = new WF.ToolStripMenuItem("Show speed chart", null, (_, _) => { _controller.SetShowSpeedChart(!_controller.ShowSpeedChart); Refresh(); });
            _offTrack = new WF.ToolStripMenuItem("Off-track invalidates lap", null, (_, _) => { _controller.SetOffTrackInvalidates(!_controller.Settings.OffTrackInvalidatesLap); Refresh(); });

            _startup = new WF.ToolStripMenuItem("Start with Windows", null, (_, _) =>
            {
                if (!StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled))
                    Notify("Couldn't change startup", "Windows didn't allow LocalBloops to update its startup entry.");
                Refresh();
            });

            var menu = new WF.ContextMenuStrip();
            menu.Items.Add(_status);
            menu.Items.Add(_reference);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(new WF.ToolStripMenuItem("Load reference from Garage 61 CSV…", null, (_, _) => _controller.BrowseForGarage61Csv()));
            menu.Items.Add(new WF.ToolStripMenuItem("Load reference from .ibt…", null, (_, _) => _controller.BrowseForIbt()));
            menu.Items.Add(new WF.ToolStripMenuItem("Use my best lap", null, (_, _) => _controller.UseBestLap()));
            menu.Items.Add(new WF.ToolStripMenuItem("Use last lap", null, (_, _) => _controller.UseLastLap()));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(_unlock);
            menu.Items.Add(_hide);
            menu.Items.Add(_speedChart);
            menu.Items.Add(_mph);
            menu.Items.Add(_offTrack);
            menu.Items.Add(_startup);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(new WF.ToolStripMenuItem("Open laps folder", null, (_, _) => _controller.OpenLapsFolder()));
            menu.Items.Add(new WF.ToolStripMenuItem("Exit", null, (_, _) => _controller.Exit()));
            menu.Opening += (_, _) => Refresh();

            (_iconImage, _hIcon) = CreateIcon();
            _icon = new WF.NotifyIcon
            {
                Icon = _iconImage,
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.DoubleClick += (_, _) => _controller.ToggleOverlayVisible();
            Refresh();
        }

        public void Refresh()
        {
            var session = _controller.Session;
            _status.Text = !_controller.Connected
                ? "iRacing: not connected"
                : session != null && session.HasCombo ? Truncate(session.Describe(), 60) : "iRacing: connected";

            var r = _controller.Reference;
            _reference.Text = r != null ? $"Ref: {LapData.FormatLapTime(r.Lap.LapTime)} ({Truncate(r.Label, 40)})" : "Ref: none";

            _unlock.Text = _controller.OverlayUnlocked ? "Lock overlay" : "Unlock overlay (move/resize)";
            _hide.Text = _controller.OverlayVisible ? "Hide overlay" : "Show overlay";
            _mph.Checked = _controller.Settings.UseMph;
            _speedChart.Checked = _controller.ShowSpeedChart;
            _offTrack.Checked = _controller.Settings.OffTrackInvalidatesLap;
            _startup.Checked = StartupRegistration.IsEnabled;

            string tip = "LocalBloops — " + (_controller.Connected ? "connected" : "waiting for iRacing");
            _icon.Text = Truncate(tip, 63);
        }

        public void Notify(string title, string text)
        {
            _icon.ShowBalloonTip(3000, title, text, WF.ToolTipIcon.Info);
            Refresh();
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _iconImage.Dispose();
            NativeMethods.DestroyIcon(_hIcon);
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        /// <summary>A small drawn icon so we don't need to ship a resource.</summary>
        private static (SD.Icon, IntPtr) CreateIcon()
        {
            using var bmp = new SD.Bitmap(32, 32);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(SD.Color.Transparent);
                using var bg = new SD.SolidBrush(SD.Color.FromArgb(255, 24, 26, 32));
                g.FillEllipse(bg, 1, 1, 30, 30);
                using var green = new SD.SolidBrush(SD.Color.FromArgb(255, 46, 204, 85));
                g.FillRectangle(green, 6, 13, 10, 6);
                using var red = new SD.SolidBrush(SD.Color.FromArgb(255, 232, 58, 58));
                g.FillRectangle(red, 16, 13, 10, 6);
                using var pen = new SD.Pen(SD.Color.White, 2);
                g.DrawLine(pen, 16, 8, 16, 24);
            }
            IntPtr h = bmp.GetHicon();
            return (SD.Icon.FromHandle(h), h);
        }
    }
}
