using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalBloops.Laps;

namespace LocalBloops
{
    public sealed class OverlayBounds
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public sealed class Settings
    {
        /// <summary>Overlay bounds used when no car is known, or for a car with no saved bounds yet.</summary>
        public double Left { get; set; } = 40;
        public double Top { get; set; } = 40;
        public double Width { get; set; } = 520;
        public double Height { get; set; } = 240;

        /// <summary>Overlay bounds per car, keyed by iRacing CarPath.</summary>
        public Dictionary<string, OverlayBounds> CarBounds { get; set; } = new Dictionary<string, OverlayBounds>();

        /// <summary>Saved bounds for this car, or null when the car has none (or no car is known).</summary>
        public OverlayBounds? GetCarBounds(string? carPath)
        {
            if (string.IsNullOrEmpty(carPath) || CarBounds == null) return null;
            return CarBounds.TryGetValue(carPath, out var b) ? b : null;
        }

        /// <summary>Stores bounds for the car, or as the default when no car is known.</summary>
        public void SetBounds(string? carPath, double left, double top, double width, double height)
        {
            if (string.IsNullOrEmpty(carPath))
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
                return;
            }
            CarBounds ??= new Dictionary<string, OverlayBounds>();
            CarBounds[carPath] = new OverlayBounds { Left = left, Top = top, Width = width, Height = height };
        }

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
