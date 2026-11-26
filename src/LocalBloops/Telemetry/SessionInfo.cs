using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalBloops.Telemetry
{
    /// <summary>The few session-YAML fields we need, pulled out with regexes instead of a YAML parser.</summary>
    public sealed class SessionInfo
    {
        public string TrackId { get; private set; } = "";
        public string TrackDisplayName { get; private set; } = "";
        public double TrackLengthMeters { get; private set; }
        public int DriverCarIdx { get; private set; } = -1;
        public string CarPath { get; private set; } = "";
        public string CarScreenName { get; private set; } = "";

        public bool HasCombo => TrackId.Length > 0 && CarPath.Length > 0;

        /// <summary>TrackID_CarPath, made safe for use as a file name.</summary>
        public string ComboKey => HasCombo ? Sanitize(TrackId + "_" + CarPath) : "";

        public string Describe() =>
            HasCombo ? $"{TrackDisplayName} · {CarScreenName}" : "Unknown track/car";

        public static SessionInfo Parse(string yaml)
        {
            var info = new SessionInfo
            {
                TrackId = Value(yaml, "TrackID"),
                TrackDisplayName = Value(yaml, "TrackDisplayName"),
            };

            var len = Regex.Match(yaml, @"^\s*TrackLength:\s*([\d.]+)\s*(km|mi)", RegexOptions.Multiline);
            if (len.Success && double.TryParse(len.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var l))
            {
                info.TrackLengthMeters = len.Groups[2].Value == "mi" ? l * 1609.344 : l * 1000.0;
            }

            if (int.TryParse(Value(yaml, "DriverCarIdx"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            {
                info.DriverCarIdx = idx;
                string block = DriverBlock(yaml, idx);
                info.CarPath = Value(block, "CarPath");
                info.CarScreenName = Value(block, "CarScreenName");
            }
            return info;
        }

        private static string Value(string text, string key)
        {
            var m = Regex.Match(text, @"^\s*" + Regex.Escape(key) + @":[ \t]*(.*?)\s*$", RegexOptions.Multiline);
            return m.Success ? Unquote(m.Groups[1].Value) : "";
        }

        private static string Unquote(string s) =>
            s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0] ? s.Substring(1, s.Length - 2) : s;

        /// <summary>Text of the "- CarIdx: n" entry under DriverInfo.Drivers, up to the next entry.</summary>
        private static string DriverBlock(string yaml, int carIdx)
        {
            int drivers = yaml.IndexOf("Drivers:", StringComparison.Ordinal);
            if (drivers < 0) return "";
            var start = new Regex(@"^\s*-\s*CarIdx:\s*" + carIdx + @"\s*$", RegexOptions.Multiline).Match(yaml, drivers);
            if (!start.Success) return "";
            int from = start.Index + start.Length;
            var next = new Regex(@"^\s*-\s*CarIdx:", RegexOptions.Multiline).Match(yaml, from);
            int to = next.Success ? next.Index : yaml.Length;
            return yaml.Substring(from, to - from);
        }

        private static string Sanitize(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(char.IsWhiteSpace(c) || Array.IndexOf(invalid, c) >= 0 ? '-' : c);
            }
            return sb.ToString();
        }
    }
}
