using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace LocalBloops.Laps
{
    /// <summary>
    /// Imports a lap exported from Garage 61 as CSV (read from disk; nothing is fetched).
    /// Columns: Speed, LapDistPct, Lat, Lon, Brake, Throttle, RPM, SteeringWheelAngle, Gear, ...
    /// There's no time column, so elapsed time is integrated from speed over distance and then
    /// scaled to the lap time in the file name ("... - 07.48.607 - id.csv") when present.
    /// </summary>
    public static class Garage61Csv
    {
        public sealed class Result
        {
            public Result(LapData lap, string driver)
            {
                Lap = lap;
                Driver = driver;
            }

            public LapData Lap { get; }
            public string Driver { get; }
        }

        public static Result Load(string path)
        {
            var meta = ParseFileName(Path.GetFileNameWithoutExtension(path));

            var pct = new List<double>();
            var row = new List<int>();
            var lat = new List<double>();
            var lon = new List<double>();
            var speed = new List<float>();
            var throttle = new List<float>();
            var brake = new List<float>();
            var gear = new List<float>();
            var steer = new List<float>();

            using (var reader = new StreamReader(path))
            {
                string? header = reader.ReadLine();
                if (header == null) throw new InvalidDataException("The file is empty.");
                var cols = header.Split(',');
                int Col(string name) => Array.FindIndex(cols, c => string.Equals(c.Trim(), name, StringComparison.OrdinalIgnoreCase));
                int cPct = Col("LapDistPct"), cSpeed = Col("Speed");
                if (cPct < 0 || cSpeed < 0)
                    throw new InvalidDataException("Not a Garage 61 telemetry export (needs LapDistPct and Speed columns).");
                int cLat = Col("Lat"), cLon = Col("Lon"), cThr = Col("Throttle"), cBrk = Col("Brake"),
                    cGear = Col("Gear"), cSteer = Col("SteeringWheelAngle");

                string? line;
                int rowIndex = -1;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    rowIndex++;
                    var f = line.Split(',');
                    double p = Num(f, cPct);
                    if (double.IsNaN(p)) continue;

                    // The export runs a sample or two past the line; stop at the wrap.
                    if (pct.Count > 0 && p < pct[pct.Count - 1] - 0.5) break;
                    if (pct.Count > 0 && p <= pct[pct.Count - 1]) continue;

                    pct.Add(p);
                    row.Add(rowIndex);
                    lat.Add(Num(f, cLat));
                    lon.Add(Num(f, cLon));
                    speed.Add((float)Or0(Num(f, cSpeed)));
                    throttle.Add((float)Or0(Num(f, cThr)));
                    brake.Add((float)Or0(Num(f, cBrk)));
                    gear.Add((float)Or0(Num(f, cGear)));
                    steer.Add((float)Or0(Num(f, cSteer)));
                }
            }

            int n = pct.Count;
            if (n < 100 || pct[n - 1] - pct[0] < 0.9)
                throw new InvalidDataException("The CSV doesn't contain a complete lap.");

            double trackLength = TrackLengthFromGps(pct, lat, lon);
            if (trackLength <= 0) throw new InvalidDataException("Couldn't work out the track length (no GPS columns).");

            var t = RowClock(row, pct, speed, trackLength, meta.LapTime, out double lapTime)
                ?? IntegratedClock(pct, speed, trackLength, meta.LapTime, out lapTime);

            var samples = new List<LapRecorder.Sample>(n + 2);
            LapRecorder.Sample At(int i, double p, double time) => new LapRecorder.Sample
            {
                Pct = p,
                T = time,
                Speed = speed[i],
                Throttle = throttle[i],
                Brake = brake[i],
                Gear = gear[i],
                Steer = steer[i],
            };

            samples.Add(At(0, 0.0, 0.0));
            for (int i = 0; i < n; i++) samples.Add(At(i, pct[i], t[i]));
            samples.Add(At(n - 1, 1.0, lapTime));

            var lap = LapRecorder.Resample(samples, lapTime);
            lap.IsValid = true;
            lap.TrackName = meta.Track;
            lap.CarName = meta.Car;
            lap.TrackLengthMeters = trackLength;
            lap.ComboKey = ""; // Garage 61 exports carry no iRacing track/car ids
            return new Result(lap, meta.Driver);
        }

        /// <summary>
        /// Exports are fixed-rate recordings (60 Hz from iRacing), so the row number is the most
        /// accurate clock. Only the short stretches from the line to the first row and from the
        /// last row back to the line are estimated from speed. Returns null if the file name has
        /// no lap time or the rows don't look like a steady sample rate.
        /// </summary>
        private static double[]? RowClock(List<int> row, List<double> pct, List<float> speed, double trackLength,
            double fileLapTime, out double lapTime)
        {
            lapTime = fileLapTime;
            int n = pct.Count;
            if (fileLapTime <= 0) return null;

            double head = pct[0] * trackLength / Math.Max(1.0, speed[0]);
            double tail = (1.0 - pct[n - 1]) * trackLength / Math.Max(1.0, speed[n - 1]);
            int rows = row[n - 1] - row[0];
            double hz = rows / Math.Max(1e-3, fileLapTime - head - tail);
            double rounded = Math.Round(hz);
            if (rounded < 10 || Math.Abs(hz - rounded) > 0.02 * rounded) return null;

            double span = rows / rounded;
            double rest = fileLapTime - span;
            if (rest < 0 || rest > 1.0) return null;
            double headTime = head + tail > 0 ? rest * head / (head + tail) : 0;

            var t = new double[n];
            for (int i = 0; i < n; i++) t[i] = headTime + (row[i] - row[0]) / rounded;
            return t;
        }

        /// <summary>Fallback: integrate distance / speed, scaled to the file's lap time when known.</summary>
        private static double[] IntegratedClock(List<double> pct, List<float> speed, double trackLength,
            double fileLapTime, out double lapTime)
        {
            int n = pct.Count;
            var t = new double[n];
            t[0] = pct[0] * trackLength / Math.Max(1.0, speed[0]);
            for (int i = 1; i < n; i++)
            {
                double ds = (pct[i] - pct[i - 1]) * trackLength;
                double v = Math.Max(1.0, (speed[i] + speed[i - 1]) * 0.5);
                t[i] = t[i - 1] + ds / v;
            }
            double integrated = t[n - 1] + (1.0 - pct[n - 1]) * trackLength / Math.Max(1.0, speed[n - 1]);

            lapTime = fileLapTime > 0 ? fileLapTime : integrated;
            double k = lapTime / integrated;
            for (int i = 0; i < n; i++) t[i] *= k;
            return t;
        }

        private static double Num(string[] f, int col)
        {
            if (col < 0 || col >= f.Length) return double.NaN;
            var s = f[col].Trim();
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        }

        private static double Or0(double v) => double.IsNaN(v) ? 0 : v;

        /// <summary>Sum of GPS distances, extrapolated to a full lap.</summary>
        private static double TrackLengthFromGps(List<double> pct, List<double> lat, List<double> lon)
        {
            double sum = 0;
            int n = pct.Count;
            for (int i = 1; i < n; i++)
            {
                if (double.IsNaN(lat[i]) || double.IsNaN(lon[i]) || double.IsNaN(lat[i - 1]) || double.IsNaN(lon[i - 1])) return 0;
                sum += Haversine(lat[i - 1], lon[i - 1], lat[i], lon[i]);
            }
            double covered = pct[n - 1] - pct[0];
            return covered > 0 ? sum / covered : 0;
        }

        private static double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371008.8;
            double rad = Math.PI / 180;
            double dLat = (lat2 - lat1) * rad, dLon = (lon2 - lon1) * rad;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        private sealed class FileMeta
        {
            public string Driver = "";
            public string Car = "";
            public string Track = "";
            public double LapTime;
        }

        /// <summary>"Garage 61 - {driver} - {car} - {track} - {mm.ss.fff} - {id}"</summary>
        private static FileMeta ParseFileName(string name)
        {
            var meta = new FileMeta();
            var parts = name.Split(" - ");
            if (parts.Length >= 6 && parts[0].Trim().Equals("Garage 61", StringComparison.OrdinalIgnoreCase))
            {
                meta.Driver = parts[1].Trim();
                meta.Car = parts[2].Trim();
                meta.Track = string.Join(" - ", parts, 3, parts.Length - 5).Trim();
            }

            var m = Regex.Match(name, @"(?:^| - )(\d{1,2})\.(\d{2})\.(\d{3})(?: - |$)");
            if (m.Success)
            {
                meta.LapTime = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                    + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                    + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 1000.0;
            }
            return meta;
        }
    }
}
