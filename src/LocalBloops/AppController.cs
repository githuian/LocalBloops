using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LocalBloops.Laps;
using LocalBloops.Overlay;
using LocalBloops.Telemetry;

namespace LocalBloops
{
    public enum ReferenceKind
    {
        PersonalBest,
        LastLap,
        Ibt,
        Garage61,
    }

    public sealed class ReferenceLap
    {
        public ReferenceLap(LapData lap, ReferenceKind kind, string label)
        {
            Lap = lap;
            Kind = kind;
            Label = label;
        }

        public LapData Lap { get; }
        public ReferenceKind Kind { get; }
        public string Label { get; }
    }

    /// <summary>Everything the overlay needs for one frame, captured in one go.</summary>
    public readonly struct RenderState
    {
        public RenderState(bool connected, bool hasFrame, TelemetryFrame frame, LiveLap? live, ReferenceLap? reference, double trackLength)
        {
            Connected = connected;
            HasFrame = hasFrame;
            Frame = frame;
            Live = live;
            Reference = reference;
            TrackLengthMeters = trackLength;
        }

        public bool Connected { get; }
        public bool HasFrame { get; }
        public TelemetryFrame Frame { get; }
        public LiveLap? Live { get; }
        public ReferenceLap? Reference { get; }
        public double TrackLengthMeters { get; }
    }

    /// <summary>
    /// Wires live telemetry, the lap recorder, the reference lap, the overlay and the tray.
    /// Telemetry callbacks run on the reader thread; UI work is marshalled to the dispatcher.
    /// </summary>
    public sealed class AppController : IDisposable
    {
        private const string PbLabel = "Local PB";

        private readonly Dispatcher _ui;
        private readonly LiveTelemetry _live = new LiveTelemetry();
        private readonly LapRecorder _recorder = new LapRecorder(trackLive: true);
        private readonly object _frameLock = new object();

        private TelemetryFrame _frame;
        private bool _hasFrame;
        private volatile bool _connected;
        private volatile SessionInfo? _session;
        private volatile ReferenceLap? _reference;
        private volatile LapData? _best;
        private volatile LapData? _last;

        private OverlayWindow? _overlay;
        private TrayIcon? _tray;
        private bool _disposed;

        public AppController(Dispatcher ui)
        {
            _ui = ui;
            Settings = Settings.Load();
            _recorder.OffTrackInvalidates = Settings.OffTrackInvalidatesLap;
        }

        public Settings Settings { get; }
        public bool Connected => _connected;
        public SessionInfo? Session => _session;
        public ReferenceLap? Reference => _reference;
        public bool OverlayUnlocked => _overlay?.IsUnlocked ?? false;
        /// <summary>The user's hide/show choice (the overlay is also auto-hidden outside a session).</summary>
        public bool OverlayVisible => !Settings.OverlayHidden;

        public void Start(string[] args)
        {
            StartupRegistration.RefreshPathIfEnabled();
            CreateOverlay();

            _tray = new TrayIcon(this);
            if (_overlay?.HotkeyError != null) _tray.Notify("Hotkey unavailable", _overlay.HotkeyError);

            _live.ConnectionChanged += OnConnectionChanged;
            _live.SessionInfoChanged += OnSessionInfoChanged;
            _live.FrameReceived += OnFrame;
            _recorder.LapCompleted += OnLapCompleted;
            _live.Start();

            var ibt = args.FirstOrDefault(a => a.EndsWith(".ibt", StringComparison.OrdinalIgnoreCase));
            var csv = args.FirstOrDefault(a => a.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            if (ibt != null) LoadReferenceFromIbt(ibt);
            else if (csv != null) LoadReferenceFromGarage61Csv(csv);
            else if (!string.IsNullOrEmpty(Settings.Garage61Reference) && File.Exists(Settings.Garage61Reference))
                LoadReferenceFromGarage61Csv(Settings.Garage61Reference, quiet: true);
        }

        /// <summary>
        /// The overlay is owned by the sim window while it exists; if Windows takes the overlay down
        /// with the sim, build a new one rather than leaving the app without an overlay.
        /// </summary>
        private void CreateOverlay()
        {
            var overlay = new OverlayWindow(this);
            overlay.Closed += (_, _) =>
            {
                if (_disposed || _overlay != overlay) return;
                _ui.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    CreateOverlay();
                    _tray?.Refresh();
                }));
            };
            _overlay = overlay;
            overlay.Show(); // creates the HWND (hotkeys need it) before possibly hiding
            RefreshOverlayVisibility();
        }

        /// <summary>
        /// The overlay is on screen only while iRacing is in a session and the user hasn't hidden it.
        /// While unlocked it stays visible so it can be positioned without the sim running.
        /// </summary>
        public void RefreshOverlayVisibility()
        {
            var o = _overlay;
            if (o == null) return;
            o.SetShown(!Settings.OverlayHidden && (_connected || o.IsUnlocked || Settings.ShowOutsideSession));
        }

        public void SetShowOutsideSession(bool show)
        {
            Settings.ShowOutsideSession = show;
            Settings.Save();
            RefreshOverlayVisibility();
        }

        public RenderState GetRenderState()
        {
            TelemetryFrame frame;
            bool hasFrame;
            lock (_frameLock)
            {
                frame = _frame;
                hasFrame = _hasFrame;
            }

            var reference = _reference;
            double len = _session?.TrackLengthMeters ?? 0;
            if (len <= 0 && reference != null) len = reference.Lap.TrackLengthMeters;
            if (len <= 0 && hasFrame && frame.LapDistPct > 0.05f) len = frame.LapDist / frame.LapDistPct;
            if (len <= 0) len = 4000;

            return new RenderState(_connected, hasFrame, frame, _recorder.Current, reference, len);
        }

        // ---- telemetry thread ----

        private void OnConnectionChanged(bool connected)
        {
            _connected = connected;
            if (!connected)
            {
                _recorder.Reset();
                lock (_frameLock) _hasFrame = false;
            }
            Ui(() =>
            {
                RefreshOverlayVisibility();
                _tray?.Refresh();
            });
        }

        private void OnSessionInfoChanged(SessionInfo info)
        {
            var old = _session;
            _session = info;
            if (!string.IsNullOrEmpty(info.CarPath) && old?.CarPath != info.CarPath)
            {
                var car = info.CarPath;
                Ui(() => _overlay?.ApplyCar(car));
            }
            if (old?.ComboKey == info.ComboKey) return;

            _recorder.Reset();
            _last = null;
            _best = LapStore.Load(info.ComboKey);

            // A Garage 61 lap has no iRacing combo key, and the user picked it on purpose: keep it.
            var current = _reference;
            if (current == null || (current.Kind != ReferenceKind.Garage61 && current.Lap.ComboKey != info.ComboKey))
            {
                _reference = _best != null ? new ReferenceLap(_best, ReferenceKind.PersonalBest, PbLabel) : null;
            }
            Ui(() =>
            {
                _tray?.Refresh();
                WarnIfTrackMismatch();
            });
        }

        /// <summary>A Garage 61 reference for another track would give a meaningless delta.</summary>
        private void WarnIfTrackMismatch()
        {
            var r = _reference;
            var s = _session;
            if (r == null || r.Kind != ReferenceKind.Garage61 || s == null || s.TrackLengthMeters <= 0) return;
            double diff = Math.Abs(r.Lap.TrackLengthMeters - s.TrackLengthMeters) / s.TrackLengthMeters;
            if (diff > 0.03)
            {
                _tray?.Notify("Reference may be for another track",
                    $"Garage 61 lap: {r.Lap.TrackName} ({r.Lap.TrackLengthMeters / 1000:0.00} km). Session: {s.TrackFullName} ({s.TrackLengthMeters / 1000:0.00} km).");
            }
        }

        private void OnFrame(TelemetryFrame f)
        {
            lock (_frameLock)
            {
                _frame = f;
                _hasFrame = true;
            }
            _recorder.Add(f);
        }

        private void OnLapCompleted(LapData lap)
        {
            var s = _session;
            if (s != null) Stamp(lap, s);
            _last = lap;

            var best = _best;
            if (!lap.IsValid || s == null || !s.HasCombo || (best != null && best.LapTime <= lap.LapTime)) return;

            _best = lap;
            try
            {
                LapStore.Save(lap);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Saving PB failed: " + ex.Message);
            }

            var current = _reference;
            if (current == null || current.Kind == ReferenceKind.PersonalBest)
            {
                _reference = new ReferenceLap(lap, ReferenceKind.PersonalBest, PbLabel);
            }
            Ui(() => _tray?.Notify("New personal best", $"{LapData.FormatLapTime(lap.LapTime)} saved for {s.Describe()}"));
        }

        private static void Stamp(LapData lap, SessionInfo s)
        {
            lap.ComboKey = s.ComboKey;
            lap.TrackName = s.TrackFullName;
            lap.CarName = s.CarScreenName;
            lap.TrackLengthMeters = s.TrackLengthMeters;
        }

        // ---- UI commands ----

        public void BrowseForIbt()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load reference lap from .ibt",
                Filter = "iRacing telemetry (*.ibt)|*.ibt|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(AppPaths.TelemetryDir) ? AppPaths.TelemetryDir : "",
            };
            if (dlg.ShowDialog() == true) LoadReferenceFromIbt(dlg.FileName);
        }

        public void LoadReferenceFromIbt(string path)
        {
            bool offTrackInvalidates = Settings.OffTrackInvalidatesLap;
            Task.Run(() => ScanIbt(path, offTrackInvalidates)).ContinueWith(t => Ui(() =>
            {
                if (t.IsFaulted)
                {
                    var msg = t.Exception?.GetBaseException().Message ?? "Unknown error";
                    MessageBox.Show($"Couldn't read {Path.GetFileName(path)}:\n{msg}", "LocalBloops",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var lap = t.Result;
                if (lap == null)
                {
                    MessageBox.Show($"No valid timed lap found in {Path.GetFileName(path)}.", "LocalBloops",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string label = $"IBT {Path.GetFileNameWithoutExtension(path)} L{lap.LapNumber}";
                _reference = new ReferenceLap(lap, ReferenceKind.Ibt, label);
                _tray?.Refresh();
                _tray?.Notify("Reference loaded", $"{LapData.FormatLapTime(lap.LapTime)} · {lap.TrackName} · {lap.CarName}");
            }), TaskScheduler.Default);
        }

        private static LapData? ScanIbt(string path, bool offTrackInvalidates)
        {
            using var ibt = IbtFile.Open(path);
            var recorder = new LapRecorder(trackLive: false) { OffTrackInvalidates = offTrackInvalidates };
            LapData? fastest = null;
            recorder.LapCompleted += lap =>
            {
                if (lap.IsValid && (fastest == null || lap.LapTime < fastest.LapTime)) fastest = lap;
            };
            foreach (var frame in ibt.ReadFrames()) recorder.Add(frame);

            if (fastest != null)
            {
                Stamp(fastest, ibt.Session);
                if (fastest.TrackLengthMeters <= 0) fastest.TrackLengthMeters = ibt.Session.TrackLengthMeters;
            }
            return fastest;
        }

        public void BrowseForGarage61Csv()
        {
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load reference lap from a Garage 61 CSV export",
                Filter = "Garage 61 CSV (*.csv)|*.csv|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(downloads) ? downloads : "",
            };
            if (dlg.ShowDialog() == true) LoadReferenceFromGarage61Csv(dlg.FileName);
        }

        public void LoadReferenceFromGarage61Csv(string path, bool quiet = false)
        {
            Task.Run(() => Garage61Csv.Load(path)).ContinueWith(t => Ui(() =>
            {
                if (t.IsFaulted)
                {
                    if (quiet) return;
                    var msg = t.Exception?.GetBaseException().Message ?? "Unknown error";
                    MessageBox.Show($"Couldn't read {Path.GetFileName(path)}:\n{msg}", "LocalBloops",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = t.Result;
                var lap = result.Lap;
                string who = result.Driver.Length > 0 ? result.Driver : Path.GetFileNameWithoutExtension(path);
                _reference = new ReferenceLap(lap, ReferenceKind.Garage61, "G61 " + who);
                Settings.Garage61Reference = path;
                Settings.Save();
                _tray?.Refresh();
                if (!quiet)
                {
                    _tray?.Notify("Garage 61 reference loaded",
                        $"{LapData.FormatLapTime(lap.LapTime)} · {who} · {lap.TrackName}");
                }
                WarnIfTrackMismatch();
            }), TaskScheduler.Default);
        }

        /// <summary>Picking another reference means the Garage 61 lap shouldn't come back on restart.</summary>
        private void ForgetGarage61Reference()
        {
            if (string.IsNullOrEmpty(Settings.Garage61Reference)) return;
            Settings.Garage61Reference = null;
            Settings.Save();
        }

        public void UseBestLap()
        {
            var best = _best;
            var s = _session;
            if (best == null && s != null) best = _best = LapStore.Load(s.ComboKey);
            if (best == null)
            {
                _tray?.Notify("No personal best yet", "Drive a clean lap for this track/car first.");
                return;
            }
            _reference = new ReferenceLap(best, ReferenceKind.PersonalBest, PbLabel);
            _tray?.Refresh();
        }

        public void UseLastLap()
        {
            var last = _last;
            if (last == null)
            {
                _tray?.Notify("No completed lap yet", "Finish a lap first.");
                return;
            }
            string label = $"Last lap L{last.LapNumber}" + (last.IsValid ? "" : " (invalid)");
            _reference = new ReferenceLap(last, ReferenceKind.LastLap, label);
            _tray?.Refresh();
        }

        public void ToggleOverlayUnlocked() => _overlay?.SetUnlocked(!_overlay.IsUnlocked);

        public void ToggleOverlayVisible() => _overlay?.ToggleVisible();

        public void SetUseMph(bool mph)
        {
            Settings.SpeedUnit = mph ? "mph" : "kmh";
            Settings.Save();
        }

        /// <summary>Speed chart visibility for the current car (or the default when no car is loaded).</summary>
        public bool ShowSpeedChart => Settings.GetShowSpeedChart(_session?.CarPath);

        public void SetShowSpeedChart(bool show)
        {
            Settings.SetShowSpeedChart(_session?.CarPath, show);
            Settings.Save();
        }

        public void SetPinnableWindow(bool pinnable)
        {
            Settings.PinnableWindow = pinnable;
            Settings.Save();
            _overlay?.ApplyPinnableStyle();
        }

        public void SetOffTrackInvalidates(bool value)
        {
            Settings.OffTrackInvalidatesLap = value;
            _recorder.OffTrackInvalidates = value;
            Settings.Save();
        }

        /// <summary>
        /// Opens Garage 61's lap search in the default browser, filtered to the current track/car
        /// while iRacing is in a session. LocalBloops itself still never contacts Garage 61.
        /// </summary>
        public void OpenGarage61Search()
        {
            var session = _connected ? _session : null;
            var url = Garage61Links.SearchUrl(session, Settings, out bool filtered);
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _tray?.Notify("Couldn't open browser", ex.Message);
                return;
            }
            if (!filtered && Garage61Links.InSession(session))
            {
                _tray?.Notify("Garage 61 doesn't know this combo yet",
                    "Pick this track and car in the search, copy the address bar, then choose \"Save Garage 61 link\" in the tray menu.");
            }
        }

        /// <summary>Remembers the Garage 61 IDs from a filtered search URL on the clipboard for this track/car.</summary>
        public void SaveGarage61LinkFromClipboard()
        {
            var session = _connected ? _session : null;
            string? text = null;
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsText()) text = System.Windows.Forms.Clipboard.GetText();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Clipboard: " + ex.Message);
            }

            if (!Garage61Links.TryLearn(text, session, Settings))
            {
                _tray?.Notify("No Garage 61 link on the clipboard",
                    "Copy the address of a Garage 61 lap search filtered to one track and one car, e.g. garage61.net/app/laps/396/8.");
                return;
            }
            Settings.Save();
            _tray?.Notify("Garage 61 link saved", $"The search now opens filtered for {session!.Describe()}.");
        }

        public void OpenLapsFolder()
        {
            Directory.CreateDirectory(AppPaths.LapsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.LapsDir + "\"") { UseShellExecute = true });
        }

        public void OnTrayStateChanged() => _tray?.Refresh();

        public void Exit()
        {
            Dispose();
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _live.Dispose();
            _overlay?.SaveBounds();
            _overlay?.Close();
            _tray?.Dispose();
            Settings.Save();
        }

        private void Ui(Action action)
        {
            if (!_disposed) _ui.BeginInvoke(action);
        }
    }
}
