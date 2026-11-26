using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalBloops.Laps
{
    public static class AppPaths
    {
        public static string Root { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalBloops");

        public static string LapsDir { get; } = Path.Combine(Root, "laps");
        public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

        public static string TelemetryDir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "telemetry");
    }

    /// <summary>Personal-best laps on disk, one JSON file per track/car combo.</summary>
    public static class LapStore
    {
        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static string FileFor(string comboKey) => Path.Combine(AppPaths.LapsDir, comboKey + ".json");

        public static LapData? Load(string comboKey)
        {
            if (string.IsNullOrEmpty(comboKey)) return null;
            string path = FileFor(comboKey);
            try
            {
                if (!File.Exists(path)) return null;
                var lap = JsonSerializer.Deserialize<LapData>(File.ReadAllText(path), Json);
                return lap != null && lap.IsWellFormed ? lap : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LapStore: failed to load {path}: {ex.Message}");
                return null;
            }
        }

        public static void Save(LapData lap)
        {
            if (string.IsNullOrEmpty(lap.ComboKey)) return;
            Directory.CreateDirectory(AppPaths.LapsDir);
            string path = FileFor(lap.ComboKey);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(lap, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
