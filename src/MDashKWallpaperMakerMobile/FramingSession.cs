using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace MDashKWallpaperMakerMobile
{
    /// <summary>A waifu2x upscaled version of a (too small) image, kept as a temporary PNG.</summary>
    internal sealed class UpscaleInfo
    {
        public string TempFile { get; init; }
        public int Factor { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string Options { get; init; }

        /// <summary>Result line of the upscale (time, device used, GPU warnings).</summary>
        public string Note { get; set; }
    }

    /// <summary>Per-file choices made in the preview window, shared with the main window / processing.</summary>
    internal sealed class FramingSession
    {
        private readonly Dictionary<string, UpscaleInfo> _upscaled = new(StringComparer.OrdinalIgnoreCase);

        private static string TempRoot => Path.Combine(Path.GetTempPath(), AppInfo.Name, "waifu2x");

        /// <summary>Folder for the waifu2x results of this app instance (deleted when the app closes).</summary>
        public static string TempDir => Path.Combine(TempRoot, Environment.ProcessId.ToString());

        public UpscaleInfo GetUpscale(string path)
        {
            lock (_upscaled) return _upscaled.TryGetValue(path, out var u) ? u : null;
        }

        public void SetUpscale(string path, UpscaleInfo info)
        {
            UpscaleInfo old;
            lock (_upscaled)
            {
                _upscaled.TryGetValue(path, out old);
                _upscaled[path] = info;
            }
            if (old != null && old.TempFile != info.TempFile) TryDelete(old.TempFile);
            Changed?.Invoke(path);
        }

        /// <summary>Goes back to the original image (the waifu2x version and its manual framing are discarded).</summary>
        public void ClearUpscale(string path)
        {
            UpscaleInfo old;
            lock (_upscaled)
            {
                if (!_upscaled.Remove(path, out old)) return;
            }
            lock (_manual) _manual.Remove(path);
            TryDelete(old.TempFile);
            Changed?.Invoke(path);
        }

        /// <summary>Image to work with: the waifu2x version when there is one, otherwise the original file.</summary>
        public RgbImage LoadSource(string path)
        {
            var u = GetUpscale(path);
            return u != null && File.Exists(u.TempFile) ? RgbImage.Load(u.TempFile) : RgbImage.Load(path);
        }

        public static void CleanupTemp()
        {
            try
            {
                if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true);
            }
            catch
            {
                // files still in use: they are removed next time
            }
        }

        /// <summary>Removes waifu2x files left by instances that did not close normally.</summary>
        public static void CleanupStaleTemp()
        {
            try
            {
                if (!Directory.Exists(TempRoot)) return;
                foreach (var dir in Directory.GetDirectories(TempRoot))
                {
                    bool running = int.TryParse(Path.GetFileName(dir), out int pid) && IsRunning(pid);
                    if (!running) Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // not critical
            }
        }

        private static bool IsRunning(int pid)
        {
            try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        private static void TryDelete(string file)
        {
            try { if (file != null && File.Exists(file)) File.Delete(file); } catch { }
        }

        private readonly Dictionary<string, FramingPlan> _manual = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _aiDisabled = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _viewed = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ImageAnalysis> _analyses = new(StringComparer.OrdinalIgnoreCase);

        public event Action<string> Changed;

        public FramingPlan GetManual(string path)
        {
            lock (_manual) return _manual.TryGetValue(path, out var p) ? p.Clone() : null;
        }

        public void SetManual(string path, FramingPlan plan)
        {
            var copy = plan.Clone();
            copy.Source = PlanSource.Manual;
            lock (_manual) _manual[path] = copy;
            Changed?.Invoke(path);
        }

        public void ClearManual(string path)
        {
            bool removed;
            lock (_manual) removed = _manual.Remove(path);
            if (removed) Changed?.Invoke(path);
        }

        public bool IsExcluded(string path)
        {
            lock (_excluded) return _excluded.Contains(path);
        }

        public void SetExcluded(string path, bool excluded)
        {
            lock (_excluded)
            {
                if (excluded) _excluded.Add(path);
                else _excluded.Remove(path);
            }
            Changed?.Invoke(path);
        }

        /// <summary>Keeps the user's choices for a file that was moved (e.g. to the "not_processed" folder).</summary>
        /// <param name="analysis">Cached detections of the file, read before it was moved (the cache key includes the path).</param>
        public void RenamePath(string oldPath, string newPath, ImageAnalysis analysis)
        {
            lock (_manual)
            {
                if (_manual.Remove(oldPath, out var plan)) _manual[newPath] = plan;
            }
            lock (_upscaled)
            {
                if (_upscaled.Remove(oldPath, out var up)) _upscaled[newPath] = up;
            }
            foreach (var set in new[] { _excluded, _aiDisabled, _viewed })
            {
                lock (set)
                {
                    if (set.Remove(oldPath)) set.Add(newPath);
                }
            }
            if (analysis != null) StoreAnalysis(newPath, analysis);
        }

        /// <summary>Marks that the user saw this image's framing in the preview (keeping the automatic one = approval).</summary>
        public void MarkViewed(string path)
        {
            lock (_viewed) _viewed.Add(path);
        }

        public bool WasViewed(string path)
        {
            lock (_viewed) return _viewed.Contains(path);
        }

        /// <summary>True when the user switched smart framing (AI) off for this image only.</summary>
        public bool IsAiDisabled(string path)
        {
            lock (_aiDisabled) return _aiDisabled.Contains(path);
        }

        public void SetAiDisabled(string path, bool disabled)
        {
            bool changed;
            lock (_aiDisabled) changed = disabled ? _aiDisabled.Add(path) : _aiDisabled.Remove(path);
            if (changed) Changed?.Invoke(path);
        }

        /// <param name="smartGlobal">State of the main "Smart framing (AI)" option.</param>
        public bool UsesAi(string path, bool smartGlobal) => smartGlobal && !IsAiDisabled(path);

        public string StatusText(string path, bool smartGlobal)
        {
            if (IsExcluded(path)) return "Skip";
            var up = GetUpscale(path);
            if (up != null) return $"Manual (waifu2x {up.Factor}x)";
            lock (_manual)
                if (_manual.ContainsKey(path)) return "Manual";
            return UsesAi(path, smartGlobal) ? "Auto (AI)" : "Auto (classic)";
        }

        // Detections are cached per file version, so re-opening the preview / processing does not run the models twice.
        private string Key(string path)
        {
            // The waifu2x version is a different image, so it gets its own cache entry.
            string upscaled = GetUpscale(path)?.TempFile;
            try { return path + "|" + File.GetLastWriteTimeUtc(path).Ticks + (upscaled != null ? "|" + upscaled : ""); }
            catch { return path; }
        }

        public ImageAnalysis GetAnalysis(string path) => _analyses.TryGetValue(Key(path), out var a) ? a : null;

        public void StoreAnalysis(string path, ImageAnalysis analysis) => _analyses[Key(path)] = analysis;
    }
}
