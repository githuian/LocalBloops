using System;
using System.Collections.Generic;
using LocalBloops.Telemetry;

namespace LocalBloops.Laps
{
    /// <summary>Per-bin live channels for the lap in progress; NaN where the car hasn't been yet.</summary>
    public sealed class LiveGrid
    {
        public readonly float[] Speed = NewNaN();
        public readonly float[] Throttle = NewNaN();
        public readonly float[] Brake = NewNaN();

        private static float[] NewNaN()
        {
            var a = new float[LapData.Bins];
            Array.Fill(a, float.NaN);
            return a;
        }
    }

    /// <summary>
    /// The lap in progress, as seen by the overlay. Arrays are written by the telemetry
    /// thread and read by the UI thread; single float writes are atomic so that's fine.
    /// </summary>
    public sealed class LiveLap
    {
        public LiveLap(double startTime, bool timed, LiveGrid? previousGrid)
        {
            StartTime = startTime;
            Timed = timed;
            PreviousGrid = previousGrid;
        }

        /// <summary>SessionTime at which this lap started (interpolated line crossing when Timed).</summary>
        public double StartTime { get; }

        /// <summary>True when the lap started at the start/finish line, so elapsed time is meaningful.</summary>
        public bool Timed { get; }

        public volatile bool Valid = true;
        public LiveGrid Grid { get; } = new LiveGrid();

        /// <summary>The grid of the lap before this one, for the part of the trace behind the line.</summary>
        public LiveGrid? PreviousGrid { get; }
    }

    /// <summary>
    /// Turns a stream of frames (live or from an .ibt) into completed laps.
    /// Not thread-safe: feed it from one thread.
    /// </summary>
    public sealed class LapRecorder
    {
        private const double MaxTimeGap = 1.0;      // s
        private const double BackwardJump = 0.002;  // pct; anything further back is a reset/tow
        private const double ForwardJump = 0.03;    // pct per tick; anything further is a teleport
        private const double MinLapTime = 5.0;      // s

        internal struct Sample
        {
            public double Pct, T;
            public float Speed, Throttle, Brake, Gear, Steer;

            public static Sample Lerp(in Sample a, in Sample b, double f) => new Sample
            {
                Pct = a.Pct + (b.Pct - a.Pct) * f,
                T = a.T + (b.T - a.T) * f,
                Speed = (float)(a.Speed + (b.Speed - a.Speed) * f),
                Throttle = (float)(a.Throttle + (b.Throttle - a.Throttle) * f),
                Brake = (float)(a.Brake + (b.Brake - a.Brake) * f),
                Gear = f < 0.5 ? a.Gear : b.Gear,
                Steer = (float)(a.Steer + (b.Steer - a.Steer) * f),
            };
        }

        private readonly List<Sample> _samples = new List<Sample>(8192);
        private readonly bool _trackLive;
        private TelemetryFrame _prev;
        private bool _havePrev;
        private bool _inLap;
        private bool _startedAtLine;
        private bool _valid;
        private double _lapStart;
        private int _lapNumber;
        private volatile LiveLap? _live;

        /// <param name="trackLive">Maintain <see cref="Current"/> for the overlay (off for .ibt scans).</param>
        public LapRecorder(bool trackLive)
        {
            _trackLive = trackLive;
        }

        /// <summary>When true, PlayerTrackSurface == OffTrack invalidates the lap.</summary>
        public bool OffTrackInvalidates { get; set; } = true;

        /// <summary>Raised for every lap that started at the line and ended at the line (valid or not).</summary>
        public event Action<LapData>? LapCompleted;

        public LiveLap? Current => _live;

        public void Reset()
        {
            _havePrev = false;
            _inLap = false;
            _samples.Clear();
            _live = null;
        }

        public void Add(in TelemetryFrame f)
        {
            if (!f.IsOnTrack || f.TrackSurface == TrackSurface.NotInWorld || f.LapDistPct < 0 || f.LapDistPct > 1.0001f)
            {
                Reset();
                return;
            }

            var cur = ToSample(f);
            if (!_havePrev)
            {
                StartLap(f.SessionTime, atLine: false, valid: false);
                cur.T = 0;
                AddSample(cur);
                Remember(f);
                return;
            }

            double dt = f.SessionTime - _prev.SessionTime;
            if (dt == 0) return; // duplicate tick
            if (dt < 0)
            {
                // Time went backwards (new session, replay rewind): start over mid-lap.
                StartLap(f.SessionTime, atLine: false, valid: false);
                cur.T = 0;
                AddSample(cur);
                Remember(f);
                return;
            }

            bool gap = dt > MaxTimeGap;
            if (gap) _valid = false;

            double dp = f.LapDistPct - _prev.LapDistPct;
            if (dp < -0.5)
            {
                // Crossed start/finish: interpolate the crossing time between the two ticks.
                var prevSample = ToSample(_prev);
                double before = 1.0 - _prev.LapDistPct;
                double after = f.LapDistPct;
                double frac = before / Math.Max(1e-9, before + after);
                double tCross = _prev.SessionTime + frac * dt;

                var unwrappedCur = cur;
                unwrappedCur.Pct += 1.0;
                var atLine = Sample.Lerp(prevSample, unwrappedCur, frac);

                if (_inLap)
                {
                    var end = atLine;
                    end.Pct = 1.0;
                    end.T = tCross - _lapStart;
                    AddSample(end);
                    FinishLap(tCross);
                }

                StartLap(tCross, atLine: true, valid: !gap);
                var start = atLine;
                start.Pct = 0.0;
                start.T = 0.0;
                AddSample(start);
                cur.T = f.SessionTime - _lapStart;
                AddSample(cur);
            }
            else if (dp > 0.5 || dp < -BackwardJump || dp > ForwardJump)
            {
                // Reversed over the line, reset, or tow: restart mid-lap (never a timed lap).
                StartLap(f.SessionTime, atLine: false, valid: false);
                cur.T = 0;
                AddSample(cur);
            }
            else
            {
                AddSample(cur);
            }

            if (f.OnPitRoad) Invalidate();
            if (OffTrackInvalidates && f.TrackSurface == TrackSurface.OffTrack) Invalidate();
            if (f.LapDistPct > 0.1f && f.LapDistPct < 0.9f) _lapNumber = f.Lap;

            Remember(f);
        }

        private void Remember(in TelemetryFrame f)
        {
            _prev = f;
            _havePrev = true;
        }

        private Sample ToSample(in TelemetryFrame f) => new Sample
        {
            Pct = f.LapDistPct,
            T = _inLap ? f.SessionTime - _lapStart : 0,
            Speed = f.Speed,
            Throttle = f.Throttle,
            Brake = f.Brake,
            Gear = f.Gear,
            Steer = f.Steer,
        };

        private void Invalidate()
        {
            _valid = false;
            var live = _live;
            if (live != null) live.Valid = false;
        }

        private void StartLap(double startTime, bool atLine, bool valid)
        {
            _samples.Clear();
            _inLap = true;
            _startedAtLine = atLine;
            _valid = atLine && valid;
            _lapStart = startTime;
            if (_trackLive)
            {
                var previous = atLine ? _live?.Grid : null;
                _live = new LiveLap(startTime, atLine, previous) { Valid = _valid };
            }
        }

        private void AddSample(Sample s)
        {
            if (!_inLap) return;
            if (_samples.Count > 0)
            {
                var last = _samples[_samples.Count - 1];
                if (s.Pct <= last.Pct) return; // keep the lap monotonic; ignore tiny backward jitter
                s.T = Math.Max(s.T, last.T);
                if (_live != null) FillGrid(_live.Grid, last, s);
            }
            else if (_live != null)
            {
                FillGrid(_live.Grid, s, s);
            }
            _samples.Add(s);
        }

        private static void FillGrid(LiveGrid g, in Sample a, in Sample b)
        {
            int n = LapData.Bins;
            int from = (int)Math.Ceiling(a.Pct * n);
            int to = (int)Math.Floor(b.Pct * n);
            if (a.Pct == b.Pct) from = to = (int)(a.Pct * n);
            from = Math.Max(0, from);
            to = Math.Min(n - 1, to);
            double span = b.Pct - a.Pct;
            for (int i = from; i <= to; i++)
            {
                double f = span > 1e-12 ? Math.Clamp(((double)i / n - a.Pct) / span, 0, 1) : 0;
                g.Speed[i] = (float)(a.Speed + (b.Speed - a.Speed) * f);
                g.Throttle[i] = (float)(a.Throttle + (b.Throttle - a.Throttle) * f);
                g.Brake[i] = (float)(a.Brake + (b.Brake - a.Brake) * f);
            }
        }

        private void FinishLap(double endTime)
        {
            double lapTime = endTime - _lapStart;
            if (!_startedAtLine || lapTime < MinLapTime || _samples.Count < 2) return;
            var lap = Resample(_samples, lapTime);
            lap.IsValid = _valid;
            lap.LapNumber = _lapNumber;
            LapCompleted?.Invoke(lap);
        }

        internal static LapData Resample(List<Sample> s, double lapTime)
        {
            var lap = new LapData { LapTime = lapTime, RecordedUtc = DateTime.UtcNow };
            int n = LapData.Bins;
            int j = 0;
            for (int i = 0; i < n; i++)
            {
                double p = (double)i / n;
                while (j < s.Count - 2 && s[j + 1].Pct < p) j++;
                var a = s[j];
                var b = s[j + 1];
                double span = b.Pct - a.Pct;
                double f = span > 1e-12 ? Math.Clamp((p - a.Pct) / span, 0, 1) : 0;
                var v = Sample.Lerp(a, b, f);
                lap.Time[i] = (float)v.T;
                lap.Speed[i] = v.Speed;
                lap.Throttle[i] = v.Throttle;
                lap.Brake[i] = v.Brake;
                lap.Gear[i] = v.Gear;
                lap.Steer[i] = v.Steer;
            }
            return lap;
        }
    }
}
