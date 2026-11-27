using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LocalBloops.Laps;
using LocalBloops.Telemetry;

namespace LocalBloops.Overlay
{
    /// <summary>
    /// Immediate-mode renderer: delta bar on top, then speed and throttle/brake traces for a
    /// window around the car (reference dimmed, live bright). Redrawn at ~60 fps.
    /// </summary>
    public sealed class OverlayView : FrameworkElement
    {
        private const double Pad = 8;
        private const double HeaderHeight = 40;
        private const double DeltaRange = 2.0; // s at full bar width

        private static readonly Typeface Ui = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface UiBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private static readonly Typeface Mono = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x12, 0x16)));
        private static readonly Brush PanelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00)));
        private static readonly Brush BarTrack = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush Ahead = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x55)));
        private static readonly Brush Behind = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x3A, 0x3A)));
        private static readonly Brush Text = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)));
        private static readonly Brush DimText = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0xF2, 0xF2, 0xF2)));
        private static readonly Brush Warn = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x20)));

        private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), 1);
        private static readonly Pen GripPen = FrozenPen(Color.FromRgb(0xFF, 0xA0, 0x20), 1.5);
        private static readonly Pen CarPen = FrozenPen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), 1.5);
        private static readonly Pen CenterPen = FrozenPen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), 1);
        private static readonly Pen SpeedRef = FrozenPen(Color.FromArgb(0x70, 0x5A, 0xB4, 0xFF), 1.5);
        private static readonly Pen SpeedLive = FrozenPen(Color.FromRgb(0x5A, 0xB4, 0xFF), 2.2);
        private static readonly Pen ThrottleRef = FrozenPen(Color.FromArgb(0x60, 0x2E, 0xCC, 0x55), 1.5);
        private static readonly Pen ThrottleLive = FrozenPen(Color.FromRgb(0x2E, 0xE0, 0x5A), 2.2);
        private static readonly Pen BrakeRef = FrozenPen(Color.FromArgb(0x60, 0xE8, 0x3A, 0x3A), 1.5);
        private static readonly Pen BrakeLive = FrozenPen(Color.FromRgb(0xFF, 0x45, 0x45), 2.2);

        private readonly AppController _controller;
        private TimeSpan _lastRender;

        public OverlayView(AppController controller)
        {
            _controller = controller;
            SnapsToDevicePixels = true;
            Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
            Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!IsVisible) return;
            var t = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
            if (t - _lastRender < TimeSpan.FromMilliseconds(15)) return; // cap at ~60 fps
            _lastRender = t;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 20 || h < 20) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            dc.DrawRoundedRectangle(Background, null, new Rect(0, 0, w, h), 6, 6);
            RenderContent(dc, w, h, dpi);
            if (_controller.OverlayUnlocked) DrawUnlockedHint(dc, w, h, dpi);
        }

        private void DrawUnlockedHint(DrawingContext dc, double w, double h, double dpi)
        {
            var ft = Format("Drag to move · edges/corner to resize · Ctrl+Shift+L to lock", 11, Warn, dpi, UiBold);
            ft.MaxTextWidth = Math.Max(10, w - 40);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            var box = new Rect((w - ft.Width) / 2 - 6, h - ft.Height - 10, ft.Width + 12, ft.Height + 4);
            dc.DrawRoundedRectangle(PanelBrush, null, box, 3, 3);
            dc.DrawText(ft, new Point(box.Left + 6, box.Top + 2));

            for (int i = 1; i <= 3; i++)
            {
                double o = 4 * i + 2;
                dc.DrawLine(GripPen, new Point(w - o, h - 3), new Point(w - 3, h - o));
            }
        }

        private void RenderContent(DrawingContext dc, double w, double h, double dpi)
        {
            var st = _controller.GetRenderState();
            if (!st.Connected || !st.HasFrame)
            {
                DrawCentered(dc, st.Connected ? "Waiting for car on track…" : "Waiting for iRacing…", new Rect(0, 0, w, h), 14, DimText, dpi);
                DrawReferenceLabel(dc, st.Reference, w, dpi);
                return;
            }

            var f = st.Frame;
            DrawDeltaBar(dc, st, new Rect(Pad, Pad, Math.Max(40, w * 0.58 - Pad * 1.5), HeaderHeight - 4), dpi);
            DrawReferenceLabel(dc, st.Reference, w, dpi);

            double top = Pad + HeaderHeight + 4;
            double avail = h - top - Pad;
            if (avail < 30) return;

            // Big gear readout in a column on the right, spanning both trace panels.
            // At least ~28% of the width so the speed delta and gear stay big on short overlays.
            double gearW = Math.Clamp(Math.Max(avail * 0.75, w * 0.28), 56, Math.Max(56, w * 0.35));
            var gearRect = new Rect(w - Pad - gearW, top, gearW, avail);
            DrawGearPanel(dc, gearRect, st, _controller.Settings.UseMph, dpi);

            var s = _controller.Settings;
            double traceW = Math.Max(20, gearRect.Left - 6 - Pad);
            bool showSpeed = _controller.ShowSpeedChart;
            Rect speedRect = Rect.Empty, pedalRect;
            if (showSpeed)
            {
                speedRect = new Rect(Pad, top, traceW, avail * 0.56 - 3);
                pedalRect = new Rect(Pad, speedRect.Bottom + 6, traceW, avail - speedRect.Height - 6);
            }
            else
            {
                pedalRect = new Rect(Pad, top, traceW, avail); // pedals take the full height
            }

            double behindPct = s.MetersBehind / st.TrackLengthMeters;
            double aheadPct = s.MetersAhead / st.TrackLengthMeters;
            var trace = new TraceWindow(f.LapDistPct, behindPct, aheadPct);

            if (showSpeed) DrawSpeedPanel(dc, speedRect, trace, st, s.UseMph, dpi);
            DrawPedalPanel(dc, pedalRect, trace, st, dpi);
        }

        // ---- header ----

        private void DrawDeltaBar(DrawingContext dc, RenderState st, Rect r, double dpi)
        {
            dc.DrawRoundedRectangle(BarTrack, null, r, 4, 4);
            double cx = r.Left + r.Width / 2;

            var live = st.Live;
            var reference = st.Reference;
            string text;
            Brush textBrush = Text;

            if (reference == null)
            {
                text = "no reference";
                textBrush = DimText;
            }
            else if (live == null || !live.Timed)
            {
                text = "–.–––";
                textBrush = DimText;
            }
            else
            {
                double elapsed = st.Frame.SessionTime - live.StartTime;
                double delta = elapsed - reference.Lap.TimeAt(st.Frame.LapDistPct);
                double frac = Math.Clamp(delta / DeltaRange, -1, 1);
                double bw = Math.Abs(frac) * (r.Width / 2);
                var bar = delta < 0
                    ? new Rect(cx - bw, r.Top + 2, bw, r.Height - 4)
                    : new Rect(cx, r.Top + 2, bw, r.Height - 4);
                dc.DrawRoundedRectangle(delta < 0 ? Ahead : Behind, null, bar, 3, 3);
                text = (delta < 0 ? "-" : "+") + Math.Abs(delta).ToString("0.000", CultureInfo.InvariantCulture);
            }

            dc.DrawLine(CenterPen, new Point(cx, r.Top), new Point(cx, r.Bottom));
            DrawCentered(dc, text, r, 20, textBrush, dpi, Mono);

            if (live != null && !live.Valid)
            {
                var ft = Format("INVALID", 10, Warn, dpi, UiBold);
                dc.DrawText(ft, new Point(r.Left + 6, r.Top + (r.Height - ft.Height) / 2));
            }
        }

        private void DrawReferenceLabel(DrawingContext dc, ReferenceLap? reference, double w, double dpi)
        {
            double x = w * 0.58 + Pad / 2;
            double maxW = Math.Max(10, w - x - Pad);
            string time = reference != null ? "REF " + LapData.FormatLapTime(reference.Lap.LapTime) : "REF --:--.---";
            var t1 = Format(time, 15, Text, dpi, Mono);
            t1.MaxTextWidth = maxW;
            t1.MaxLineCount = 1;
            t1.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(t1, new Point(x, Pad));

            var t2 = Format(reference?.Label ?? "no reference lap", 11, DimText, dpi, Ui);
            t2.MaxTextWidth = maxW;
            t2.MaxLineCount = 1;
            t2.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(t2, new Point(x, Pad + t1.Height));
        }

        // ---- traces ----

        private void DrawSpeedPanel(DrawingContext dc, Rect r, TraceWindow tw, RenderState st, bool mph, double dpi)
        {
            if (r.Height < 10) return;
            dc.DrawRoundedRectangle(PanelBrush, null, r, 4, 4);

            var reference = st.Reference?.Lap;
            double maxV = Math.Max(50, reference?.MaxSpeed() ?? 0);
            maxV = Math.Max(maxV, st.Frame.Speed) * 1.08;

            for (int i = 1; i < 4; i++)
            {
                double y = r.Top + r.Height * i / 4;
                dc.DrawLine(GridPen, new Point(r.Left, y), new Point(r.Right, y));
            }

            double Y(float v) => r.Bottom - Math.Clamp(v / maxV, 0, 1) * r.Height;

            if (reference != null) DrawPolyline(dc, SpeedRef, r, tw, pct => Y(LapData.Sample(reference.Speed, pct)), true);
            DrawLive(dc, SpeedLive, r, tw, st.Live, g => g.Speed, Y);
            DrawCar(dc, r, tw);

            double k = mph ? 2.236936 : 3.6;
            string unit = mph ? "mph" : "km/h";
            string label = $"{st.Frame.Speed * k:0} {unit}";
            if (reference != null) label += $"   ref {LapData.Sample(reference.Speed, st.Frame.LapDistPct) * k:0}";
            var ft = Format(label, 11, Text, dpi, Ui);
            dc.DrawText(ft, new Point(r.Left + 5, r.Top + 2));
        }

        private void DrawPedalPanel(DrawingContext dc, Rect r, TraceWindow tw, RenderState st, double dpi)
        {
            if (r.Height < 10) return;
            dc.DrawRoundedRectangle(PanelBrush, null, r, 4, 4);
            double mid = r.Top + r.Height / 2;
            dc.DrawLine(GridPen, new Point(r.Left, mid), new Point(r.Right, mid));

            double inner = r.Height - 4;
            double Y(float v) => r.Bottom - 2 - Math.Clamp(v, 0, 1) * inner;

            var reference = st.Reference?.Lap;
            if (reference != null)
            {
                DrawPolyline(dc, ThrottleRef, r, tw, pct => Y(LapData.Sample(reference.Throttle, pct)), true);
                DrawPolyline(dc, BrakeRef, r, tw, pct => Y(LapData.Sample(reference.Brake, pct)), true);
            }
            DrawLive(dc, ThrottleLive, r, tw, st.Live, g => g.Throttle, Y);
            DrawLive(dc, BrakeLive, r, tw, st.Live, g => g.Brake, Y);
            DrawCar(dc, r, tw);

            var ft = Format($"THR {st.Frame.Throttle * 100:0}%   BRK {st.Frame.Brake * 100:0}%", 11, Text, dpi, Ui);
            dc.DrawText(ft, new Point(r.Left + 5, r.Top + 2));
        }

        /// <summary>
        /// Speed delta to the reference on top (green when faster, red when slower), then the current
        /// gear in large text: green when lower than the reference gear at this point of the lap, red
        /// when higher, white when equal (or when there's no reference).
        /// </summary>
        private static void DrawGearPanel(DrawingContext dc, Rect r, RenderState st, bool mph, double dpi)
        {
            if (r.Height < 10 || r.Width < 10) return;
            dc.DrawRoundedRectangle(PanelBrush, null, r, 4, 4);

            int gear = st.Frame.Gear;
            int? refGear = null;
            var reference = st.Reference?.Lap;
            if (reference != null)
                refGear = (int)Math.Round(reference.Gear[Bin(st.Frame.LapDistPct)]);

            Brush brush = Text;
            if (refGear is int rg)
            {
                if (gear < rg) brush = Ahead;
                else if (gear > rg) brush = Behind;
            }

            // Speed delta (live minus reference at the same lap-distance), in the chosen units.
            string deltaText = "–";
            Brush deltaBrush = DimText;
            if (reference != null)
            {
                double k = mph ? 2.236936 : 3.6;
                double dv = (st.Frame.Speed - LapData.Sample(reference.Speed, st.Frame.LapDistPct)) * k;
                int rounded = (int)Math.Round(dv);
                deltaText = (rounded > 0 ? "+" : rounded < 0 ? "-" : "±") + Math.Abs(rounded).ToString(CultureInfo.InvariantCulture);
                deltaBrush = rounded > 0 ? Ahead : rounded < 0 ? Behind : Text;
            }
            string sub = refGear is int g ? "ref " + GearText(g) : "gear";

            // Short, wide panel: speed delta + "ref N" on the left, gear on the right, so both get
            // the full height. Otherwise stack delta / gear / "ref N" vertically.
            if (r.Width >= r.Height * 1.4)
            {
                double leftW = r.Width * 0.58;
                var left = new Rect(r.Left, r.Top, leftW, r.Height);
                var right = new Rect(r.Left + leftW, r.Top, r.Width - leftW, r.Height);
                double labelH = Math.Max(16, r.Height * 0.22);
                DrawSpeedDelta(dc, deltaText, deltaBrush, new Rect(left.Left, left.Top, left.Width, left.Height - labelH), dpi);
                DrawSubLabel(dc, sub, new Rect(left.Left, left.Bottom - labelH, left.Width, labelH), dpi);
                DrawGearDigit(dc, GearText(gear), brush, right, dpi);
            }
            else
            {
                // "ref N" caps at 14px, so it needs little room; the speed delta gets a third of the panel.
                double labelH = Math.Max(20, r.Height * 0.12);
                double deltaH = r.Height * 0.34;
                DrawSpeedDelta(dc, deltaText, deltaBrush, new Rect(r.Left, r.Top, r.Width, deltaH), dpi);
                DrawGearDigit(dc, GearText(gear), brush, new Rect(r.Left, r.Top + deltaH, r.Width, r.Height - deltaH - labelH), dpi);
                DrawSubLabel(dc, sub, new Rect(r.Left, r.Bottom - labelH, r.Width, labelH), dpi);
            }
        }

        private static void DrawSpeedDelta(DrawingContext dc, string text, Brush brush, Rect r, double dpi)
        {
            // Consolas is ~0.55em per char and its line ~1.17em, so "-162" fits at these sizes.
            double size = Math.Max(10, Math.Min(r.Height * 0.8, (r.Width - 8) / 2.4));
            var ft = Format(text, size, brush, dpi, Mono);
            ft.MaxTextWidth = Math.Max(10, r.Width - 4);
            ft.MaxLineCount = 1;
            dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) / 2, r.Top + Math.Max(2, (r.Height - ft.Height) / 2)));
        }

        private static void DrawGearDigit(DrawingContext dc, string text, Brush brush, Rect r, double dpi)
        {
            // Line height is ~1.33x the font size, so this keeps the digit inside its slot.
            double size = Math.Max(10, Math.Min(r.Height * 0.75, r.Width * 1.1));
            var ft = Format(text, size, brush, dpi, UiBold);
            dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) / 2, r.Top + (r.Height - ft.Height) / 2));
        }

        private static void DrawSubLabel(DrawingContext dc, string text, Rect r, double dpi)
        {
            var ft = Format(text, Math.Clamp(r.Height * 0.6, 9, 14), DimText, dpi, Ui);
            dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) / 2, r.Bottom - ft.Height - 3));
        }

        private static void DrawCar(DrawingContext dc, Rect r, TraceWindow tw)
        {
            double x = r.Left + tw.CarFraction * r.Width;
            dc.DrawLine(CarPen, new Point(x, r.Top), new Point(x, r.Bottom));
        }

        /// <summary>Plots a reference channel across the whole window (wraps over start/finish).</summary>
        private static void DrawPolyline(DrawingContext dc, Pen pen, Rect r, TraceWindow tw, Func<double, double> yAt, bool wholeWindow)
        {
            int n = Math.Max(2, (int)(r.Width / 2));
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < n; i++)
                {
                    double u = (double)i / (n - 1);
                    if (!wholeWindow && u > tw.CarFraction) break;
                    var p = new Point(r.Left + u * r.Width, yAt(tw.PctAt(u)));
                    if (i == 0) ctx.BeginFigure(p, false, false);
                    else ctx.LineTo(p, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        /// <summary>
        /// Plots the live channel from the left edge up to the car. Points behind the line come from
        /// the previous lap's grid; bins the car hasn't covered (NaN) break the line.
        /// </summary>
        private static void DrawLive(DrawingContext dc, Pen pen, Rect r, TraceWindow tw, LiveLap? live, Func<LiveGrid, float[]> channel, Func<float, double> y)
        {
            if (live == null) return;
            var current = channel(live.Grid);
            var previous = live.PreviousGrid != null ? channel(live.PreviousGrid) : null;

            int n = Math.Max(2, (int)(r.Width / 2));
            var geo = new StreamGeometry();
            bool any = false;
            using (var ctx = geo.Open())
            {
                bool open = false;
                for (int i = 0; i < n; i++)
                {
                    double u = (double)i / (n - 1);
                    if (u > tw.CarFraction) break;
                    double raw = tw.RawPctAt(u);
                    float v;
                    if (raw >= 0) v = current[Bin(raw)];
                    else v = previous != null ? previous[Bin(raw + 1)] : float.NaN;

                    if (float.IsNaN(v))
                    {
                        open = false;
                        continue;
                    }
                    var p = new Point(r.Left + u * r.Width, y(v));
                    if (!open) ctx.BeginFigure(p, false, false);
                    else ctx.LineTo(p, true, false);
                    open = true;
                    any = true;
                }
            }
            if (!any) return;
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        private static int Bin(double pct) => Math.Clamp((int)(pct * LapData.Bins), 0, LapData.Bins - 1);

        private static string GearText(int gear) => gear switch
        {
            < 0 => "R",
            0 => "N",
            _ => gear.ToString(CultureInfo.InvariantCulture),
        };

        // ---- text helpers ----

        private static FormattedText Format(string s, double size, Brush brush, double dpi, Typeface face) =>
            new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dpi);

        private static void DrawCentered(DrawingContext dc, string s, Rect r, double size, Brush brush, double dpi, Typeface? face = null)
        {
            var ft = Format(s, size, brush, dpi, face ?? Ui);
            dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) / 2, r.Top + (r.Height - ft.Height) / 2));
        }

        private static Brush Frozen(SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }

        private static Pen FrozenPen(Color c, double thickness)
        {
            var p = new Pen(new SolidColorBrush(c), thickness) { LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        /// <summary>Maps horizontal position u (0..1) to LapDistPct around the car.</summary>
        private readonly struct TraceWindow
        {
            private readonly double _carPct, _behind, _span;

            public TraceWindow(double carPct, double behind, double ahead)
            {
                _carPct = carPct;
                _behind = behind;
                _span = Math.Max(1e-6, behind + ahead);
            }

            public double CarFraction => _behind / _span;

            /// <summary>Unwrapped pct; negative means "on the previous lap".</summary>
            public double RawPctAt(double u) => _carPct - _behind + u * _span;

            public double PctAt(double u)
            {
                double p = RawPctAt(u);
                return p - Math.Floor(p);
            }
        }
    }
}
