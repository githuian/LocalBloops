using System;

namespace LocalBloops.Laps
{
    /// <summary>
    /// A complete lap resampled onto a fixed LapDistPct grid: bin i is at pct = i / Bins.
    /// Serialized as-is to the laps folder.
    /// </summary>
    public sealed class LapData
    {
        public const int Bins = 2000;

        public int FormatVersion { get; set; } = 1;
        public string ComboKey { get; set; } = "";
        public string TrackName { get; set; } = "";
        public string CarName { get; set; } = "";
        public double TrackLengthMeters { get; set; }
        public int LapNumber { get; set; }
        public double LapTime { get; set; }
        public bool IsValid { get; set; }
        public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;

        public float[] Time { get; set; } = new float[Bins];      // s since lap start
        public float[] Speed { get; set; } = new float[Bins];     // m/s
        public float[] Throttle { get; set; } = new float[Bins];  // 0..1
        public float[] Brake { get; set; } = new float[Bins];     // 0..1
        public float[] Gear { get; set; } = new float[Bins];
        public float[] Steer { get; set; } = new float[Bins];     // rad

        public bool IsWellFormed =>
            LapTime > 0 && Time?.Length == Bins && Speed?.Length == Bins && Throttle?.Length == Bins &&
            Brake?.Length == Bins && Gear?.Length == Bins && Steer?.Length == Bins;

        /// <summary>Elapsed lap time at the given LapDistPct (0..1).</summary>
        public double TimeAt(double pct)
        {
            pct = Math.Clamp(pct, 0, 1);
            double x = pct * Bins;
            int i = Math.Min((int)x, Bins - 1);
            double t0 = Time[i];
            double t1 = i + 1 < Bins ? Time[i + 1] : LapTime;
            return t0 + (t1 - t0) * (x - i);
        }

        /// <summary>Linear interpolation of a channel at pct, wrapping across start/finish.</summary>
        public static float Sample(float[] channel, double pct)
        {
            pct -= Math.Floor(pct);
            double x = pct * Bins;
            int i0 = Math.Min((int)x, Bins - 1);
            int i1 = (i0 + 1) % Bins;
            double f = x - i0;
            return (float)(channel[i0] + (channel[i1] - channel[i0]) * f);
        }

        public float MaxSpeed()
        {
            float m = 0;
            foreach (var v in Speed) if (v > m) m = v;
            return m;
        }

        public static string FormatLapTime(double seconds)
        {
            if (seconds <= 0 || double.IsNaN(seconds)) return "--:--.---";
            int minutes = (int)(seconds / 60);
            double rest = seconds - minutes * 60;
            return $"{minutes}:{rest.ToString("00.000", System.Globalization.CultureInfo.InvariantCulture)}";
        }
    }
}
