using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalBloops.Laps;

namespace LocalBloops
{
    public sealed class Settings
    {
        public double Left { get; set; } = 40;
        public double Top { get; set; } = 40;
        public double Width { get; set; } = 520;
        public double Height { get; set; } = 240;

        /// <summary>"kmh" or "mph".</summary>
        public string SpeedUnit { get; set; } = "kmh";
        [JsonIgnore]
        public bool UseMph => string.Equals(SpeedUnit, "mph", StringComparison.OrdinalIgnoreCase);

        public bool OffTrackInvalidatesLap { get; set; } = true;
        public double MetersBehind { get; set; } = 150;
        public double MetersAhead { get; set; } = 500;
        public bool OverlayHidden { get; set; }

        /// <summary>Garage 61 CSV used as the reference; reloaded on startup.</summary>
        public string? Garage61Reference { get; set; }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static Settings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Json);
                    if (s != null) return s;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Settings: " + ex.Message);
            }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Settings: " + ex.Message);
            }
        }
    }
}
