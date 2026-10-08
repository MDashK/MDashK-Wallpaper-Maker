using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace MDashKWallpaperMaker
{
    internal static class AppInfo
    {
        public const string Name = "MDashK Wallpaper Maker";
        public const string OutputFolderName = "processed_output";

        /// <summary>
        /// Output folder for a wallpaper size, so runs at different sizes never overwrite each other:
        /// 16:9 sizes use the height ("processed_output_1080"), other ratios the full size ("processed_output_2560x1080").
        /// </summary>
        public static string OutputFolderFor(TargetSize t)
        {
            bool is16By9 = Math.Abs(t.Width * 9.0 / (t.Height * 16.0) - 1.0) < 0.01; // 1366x768 counts as 16:9
            return is16By9 ? $"{OutputFolderName}_{t.Height}" : $"{OutputFolderName}_{t.Width}x{t.Height}";
        }

        /// <summary>True for any output folder ("processed_output", "processed_output_1080", ...).</summary>
        public static bool IsOutputFolder(string folderName) =>
            folderName.StartsWith(OutputFolderName, StringComparison.OrdinalIgnoreCase);
        public const string NotProcessedFolderName = "not_processed";

        public static string Version
        {
            get
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(info))
                {
                    int plus = info.IndexOf('+');
                    return plus >= 0 ? info.Substring(0, plus) : info;
                }
                var v = asm.GetName().Version;
                return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        public static Icon LoadIcon()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MDashKWallpaperMaker.icon.ico");
            return s == null ? null : new Icon(s);
        }

        public static Image LoadIconImage()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MDashKWallpaperMaker.icon.png");
            if (s == null) return null;
            using var img = Image.FromStream(s);
            return new Bitmap(img);
        }
    }

    /// <summary>Small persisted user preferences (config\settings.json next to the executable).</summary>
    internal sealed class AppSettings
    {
        public bool IncludeSubfolders { get; set; } = true;
        public bool SmartFraming { get; set; } = true;
        public decimal SquareTolerancePercent { get; set; } = 10;

        /// <summary>Wallpaper resolution, e.g. "1920x1080".</summary>
        public string TargetResolution { get; set; } = "1920x1080";

        /// <summary>waifu2x factor used in the preview for images that are too small (2, 4, 6 or 8).</summary>
        public int UpscaleFactor { get; set; } = 2;

        public Waifu2xOptions Waifu2x { get; set; } = new();
        public string LastFolder { get; set; }

        /// <summary>Recalibrate the automatic framing after processing when enough new examples were recorded.</summary>
        public bool LearnAutomatically { get; set; } = true;

        private static string FilePath => AppPaths.SettingsFile;

        // Settings of versions before 2.0.15 were kept in %AppData%; they are moved to the portable config folder once.
        private static string LegacyFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                if (File.Exists(LegacyFilePath) && !string.Equals(LegacyFilePath, FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    var migrated = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(LegacyFilePath)) ?? new AppSettings();
                    migrated.Save();
                    return migrated;
                }
            }
            catch
            {
                // Corrupted settings: fall back to defaults.
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Not critical.
            }
        }
    }
}
