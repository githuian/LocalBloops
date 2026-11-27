using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalBloops.Telemetry;

namespace LocalBloops.Laps
{
    /// <summary>
    /// Builds Garage 61 lap-search links. Garage 61 uses its own track/car IDs, so iRacing IDs are
    /// translated with the bundled Garage61Map.json (made by tools\make-garage61-map.ps1) plus any
    /// links the user saved. These links are only handed to the browser; nothing here goes online.
    /// </summary>
    public static class Garage61Links
    {
        public const string LapSearch = "https://garage61.net/app/laps";

        private static readonly Regex LapsUrl = new Regex(
            @"garage61\.net/app/laps/(\d+)/(\d+)(?![\d,])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Lazy<Map> Bundled = new Lazy<Map>(LoadBundled);

        public static bool InSession(SessionInfo? session) =>
            session != null && IsId(session.TrackId) && IsId(session.CarId);

        /// <summary>Garage 61 track/car IDs for the session, preferring links the user saved.</summary>
        public static bool TryResolve(SessionInfo? session, Settings settings, out int track, out int car)
        {
            track = car = 0;
            if (!InSession(session)) return false;
            return TryLookup(settings.Garage61Tracks, Bundled.Value.Tracks, session!.TrackId, out track)
                && TryLookup(settings.Garage61Cars, Bundled.Value.Cars, session.CarId, out car);
        }

        /// <summary>The filtered search for the session's combo, or the plain search when it can't be mapped.</summary>
        public static string SearchUrl(SessionInfo? session, Settings settings, out bool filtered)
        {
            filtered = TryResolve(session, settings, out int track, out int car);
            return filtered ? $"{LapSearch}/{track}/{car}" : LapSearch;
        }

        /// <summary>
        /// Reads a Garage 61 lap-search URL filtered to one track and one car (e.g. copied from the
        /// address bar) and remembers its IDs for the session's iRacing track and car.
        /// </summary>
        public static bool TryLearn(string? text, SessionInfo? session, Settings settings)
        {
            if (string.IsNullOrEmpty(text) || !InSession(session)) return false;
            var m = LapsUrl.Match(text);
            if (!m.Success
                || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int track)
                || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int car)
                || track <= 0 || car <= 0)
            {
                return false;
            }
            settings.Garage61Tracks ??= new Dictionary<string, int>();
            settings.Garage61Cars ??= new Dictionary<string, int>();
            settings.Garage61Tracks[session!.TrackId] = track;
            settings.Garage61Cars[session.CarId] = car;
            return true;
        }

        private static bool TryLookup(Dictionary<string, int>? learned, Dictionary<string, int> bundled, string key, out int id)
        {
            if (learned != null && learned.TryGetValue(key, out id) && id > 0) return true;
            return bundled.TryGetValue(key, out id) && id > 0;
        }

        private static bool IsId(string s) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0;

        private sealed class Map
        {
            /// <summary>iRacing TrackID → Garage 61 track ID.</summary>
            public Dictionary<string, int> Tracks { get; set; } = new Dictionary<string, int>();
            /// <summary>iRacing CarID → Garage 61 car ID.</summary>
            public Dictionary<string, int> Cars { get; set; } = new Dictionary<string, int>();
        }

        private static Map LoadBundled()
        {
            try
            {
                using var stream = typeof(Garage61Links).Assembly.GetManifestResourceStream("LocalBloops.Garage61Map.json");
                if (stream != null)
                {
                    var map = JsonSerializer.Deserialize<Map>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (map != null)
                    {
                        map.Tracks ??= new Dictionary<string, int>();
                        map.Cars ??= new Dictionary<string, int>();
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Garage61Map: " + ex.Message);
            }
            return new Map();
        }
    }
}
