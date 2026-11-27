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

        /// <summary>Keep the overlay on screen while iRacing isn't in a session (off: it hides).</summary>
        public bool ShowOutsideSession { get; set; }

        /// <summary>Make the overlay a normal app window so VR window-pinning tools can list it.</summary>
        public bool PinnableWindow { get; set; }
        /// <summary>Speed chart visibility when no car is known, or for a car that hasn't set its own.</summary>
        public bool ShowSpeedChart { get; set; } = true;

        /// <summary>Speed chart visibility per car, keyed by iRacing CarPath.</summary>
        public Dictionary<string, bool> CarShowSpeedChart { get; set; } = new Dictionary<string, bool>();

        public bool GetShowSpeedChart(string? carPath)
        {
            if (!string.IsNullOrEmpty(carPath) && CarShowSpeedChart != null && CarShowSpeedChart.TryGetValue(carPath, out var show))
                return show;
            return ShowSpeedChart;
        }

        public void SetShowSpeedChart(string? carPath, bool show)
        {
            if (string.IsNullOrEmpty(carPath))
            {
                ShowSpeedChart = show;
                return;
            }
            CarShowSpeedChart ??= new Dictionary<string, bool>();
            CarShowSpeedChart[carPath] = show;
        }

        /// <summary>Garage 61 CSV used as the reference; reloaded on startup.</summary>
        public string? Garage61Reference { get; set; }

        /// <summary>Garage 61 track IDs saved from search links, keyed by iRacing TrackID.</summary>
        public Dictionary<string, int> Garage61Tracks { get; set; } = new Dictionary<string, int>();

        /// <summary>Garage 61 car IDs saved from search links, keyed by iRacing CarID.</summary>
        public Dictionary<string, int> Garage61Cars { get; set; } = new Dictionary<string, int>();

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static Settings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Json);
                    if (s != null)
                    {
                        Log.Info($"Settings loaded from {AppPaths.SettingsFile} ({s.CarBounds?.Count ?? 0} car layouts)");
                        return s;
                    }
                }
                Log.Info($"No settings at {AppPaths.SettingsFile}; using defaults");
            }
            catch (Exception ex)
            {
                Log.Error("Loading settings failed; using defaults", ex);
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
                Log.Error("Saving settings failed", ex);
            }
        }
    }
}
