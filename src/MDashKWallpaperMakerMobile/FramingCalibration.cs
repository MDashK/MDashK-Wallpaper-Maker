using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDashKWallpaperMakerMobile
{
    /// <summary>Portable data folder ("config" next to the executable), so the app can be moved between machines.</summary>
    internal static class AppPaths
    {
        private static string _configDir;

        public static string ConfigDir
        {
            get
            {
                if (_configDir != null) return _configDir;
                string dir = Path.Combine(AppContext.BaseDirectory, "config");
                try
                {
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".write_test");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                }
                catch
                {
                    // Read-only location (e.g. Program Files): fall back to the user profile.
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
                    Directory.CreateDirectory(dir);
                }
                return _configDir = dir;
            }
            set => _configDir = value;
        }

        public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
        public static string HistoryFile => Path.Combine(ConfigDir, "history.jsonl");
        public static string CalibrationFile => Path.Combine(ConfigDir, "calibration.json");
    }

    /// <summary>
    /// Parameters of the automatic framing for phone wallpapers. The defaults are reasonable starting values (the
    /// Mobile version has no reference set yet); "Recalibrate" refits them with the user's own adjustments.
    /// </summary>
    internal sealed class FramingCalibration
    {
        /// <summary>When the image is cropped vertically: gap between crop top and head top, as fraction of the crop height.</summary>
        public double HeadroomFraction { get; set; } = 0.03;

        /// <summary>Visible width of the original = factor x width of the character (half-body).</summary>
        public double SubjectWidthFactor { get; set; } = 1.6;

        /// <summary>Typical zoom; the computed zoom is pulled towards it (in log space) by <see cref="TypicalZoomWeight"/>.</summary>
        public double TypicalZoom { get; set; } = 1.0;
        public double TypicalZoomWeight { get; set; } = 0.5;

        /// <summary>When the image is shorter than the screen: 0 = at the top, 0.5 = centred, 1 = at the bottom.</summary>
        public double VerticalPosition { get; set; } = 0.5;

        public double MaxAutoZoom { get; set; } = 2.0;

        /// <summary>
        /// The image should cover at least this fraction of the screen height (1/3 = at most one mirror copy above and
        /// below); landscape images are zoomed in (sides cropped around the character) to reach it.
        /// </summary>
        public double MinImageHeightFraction { get; set; } = 1.0 / 3.0;

        // Information about where these values come from
        public string Source { get; set; } = "original (default values)";
        public DateTime? CreatedUtc { get; set; }
        public int UserSamples { get; set; }
        public double Score { get; set; }

        [JsonIgnore]
        public bool IsDefault => Source.StartsWith("original", StringComparison.Ordinal);

        public FramingCalibration Clone() => (FramingCalibration)MemberwiseClone();

        public static FramingCalibration Default => new();

        private static FramingCalibration _current;
        private static readonly object Gate = new();

        /// <summary>Calibration in use (config\calibration.json, or the defaults).</summary>
        public static FramingCalibration Current
        {
            get
            {
                lock (Gate)
                {
                    if (_current != null) return _current;
                    try
                    {
                        if (File.Exists(AppPaths.CalibrationFile))
                            _current = JsonSerializer.Deserialize<FramingCalibration>(File.ReadAllText(AppPaths.CalibrationFile));
                    }
                    catch
                    {
                        // corrupted -> defaults
                    }
                    return _current ??= Default;
                }
            }
        }

        public static void Save(FramingCalibration c)
        {
            lock (Gate)
            {
                File.WriteAllText(AppPaths.CalibrationFile, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
                _current = c;
            }
        }

        public static void ResetToDefault()
        {
            lock (Gate)
            {
                if (File.Exists(AppPaths.CalibrationFile)) File.Delete(AppPaths.CalibrationFile);
                _current = Default;
            }
        }

        public string Describe() =>
            $"headroom {HeadroomFraction:+0.000;-0.000}, subject width factor {SubjectWidthFactor:F2}, " +
            $"typical zoom x{TypicalZoom:F2} (weight {TypicalZoomWeight:F2}), vertical position {VerticalPosition:F2}";
    }
}
