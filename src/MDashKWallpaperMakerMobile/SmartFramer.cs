using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace MDashKWallpaperMakerMobile
{
    /// <summary>Detections for one image, in original image pixel coordinates.</summary>
    internal sealed class ImageAnalysis
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public List<Detection> Detections { get; init; } = new();
        public long AnalysisMs { get; init; }

        public IEnumerable<Detection> Of(string label) => Detections.Where(d => d.Label == label);
    }

    /// <summary>Loads the ONNX models once (from the "Models" folder next to the executable).</summary>
    internal sealed class ModelHub : IDisposable
    {
        private static readonly object Gate = new();
        private static ModelHub _instance;
        private static string _loadError;

        public YoloDetector Head { get; private set; }
        public YoloDetector Face { get; private set; }
        public YoloDetector HalfBody { get; private set; }
        public TextDetector Text { get; private set; }

        public static string ModelsDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "Models");

        /// <summary>Returns the loaded models, or null (see <see cref="LoadError"/>) when they are not available.</summary>
        public static ModelHub Get()
        {
            lock (Gate)
            {
                if (_instance != null || _loadError != null) return _instance;
                try
                {
                    var hub = new ModelHub
                    {
                        Head = new YoloDetector(Path.Combine(ModelsDir, "head"), "head"),
                        Face = new YoloDetector(Path.Combine(ModelsDir, "face"), "face"),
                        HalfBody = new YoloDetector(Path.Combine(ModelsDir, "halfbody"), "halfbody"),
                        Text = new TextDetector(Path.Combine(ModelsDir, "text", "model.onnx")),
                    };
                    _instance = hub;
                }
                catch (Exception ex)
                {
                    _loadError = ex.Message;
                }
                return _instance;
            }
        }

        public static string LoadError => _loadError;

        public void Dispose()
        {
            Head?.Dispose(); Face?.Dispose(); HalfBody?.Dispose(); Text?.Dispose();
        }
    }

    /// <summary>
    /// Chooses zoom and crop position of phone wallpapers automatically from anime head / half-body detections.
    /// The tunable values live in <see cref="FramingCalibration"/> and are refined with the user's own adjustments
    /// by <see cref="Calibrator"/>.
    /// </summary>
    internal static class SmartFramer
    {

        // Same analysis sizes as used for the calibration.
        private const int DetectorWorkingSize = 1600;
        private const int TextWorkingSize = 1280;

        public static ImageAnalysis Analyze(RgbImage original, ModelHub models)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int longest = Math.Max(original.Width, original.Height);
            double k = Math.Min(1.0, DetectorWorkingSize / (double)longest);
            RgbImage work = k < 1
                ? original.Resize(Math.Max(1, (int)Math.Round(original.Width * k)), Math.Max(1, (int)Math.Round(original.Height * k)))
                : original;
            double kt = Math.Min(1.0, TextWorkingSize / (double)longest);
            RgbImage workText = kt < k
                ? work.Resize(Math.Max(1, (int)Math.Round(original.Width * kt)), Math.Max(1, (int)Math.Round(original.Height * kt)))
                : work;
            if (kt >= k) kt = k;

            var found = new List<Detection>();
            found.AddRange(models.Head.Detect(work));
            found.AddRange(models.Face.Detect(work));
            found.AddRange(models.HalfBody.Detect(work));

            static Detection ToOriginal(Detection d, double f) => new()
            {
                Label = d.Label,
                Score = d.Score,
                Box = RectangleF.FromLTRB((float)(d.Box.Left / f), (float)(d.Box.Top / f), (float)(d.Box.Right / f), (float)(d.Box.Bottom / f)),
            };
            var dets = found.Select(d => ToOriginal(d, k)).ToList();
            dets.AddRange(models.Text.Detect(workText).Select(d => ToOriginal(d, kt)));

            return new ImageAnalysis { Width = original.Width, Height = original.Height, Detections = dets, AnalysisMs = sw.ElapsedMilliseconds };
        }

        private static RectangleF Union(IEnumerable<RectangleF> boxes)
        {
            RectangleF? u = null;
            foreach (var b in boxes) u = u == null ? b : RectangleF.Union(u.Value, b);
            return u ?? RectangleF.Empty;
        }

        private static float Area(RectangleF r) => r.Width * r.Height;

        /// <summary>Head box(es) of the main character(s).</summary>
        public static RectangleF MainHeads(ImageAnalysis a, out int count)
        {
            var heads = a.Of("head").Select(d => d.Box).ToList();
            if (heads.Count == 0)
            {
                // Fall back to faces, enlarged to approximate the head (hair above, sides).
                heads = a.Of("face").Select(d => RectangleF.FromLTRB(
                    d.Box.Left - 0.3f * d.Box.Width, d.Box.Top - 0.6f * d.Box.Height,
                    d.Box.Right + 0.3f * d.Box.Width, d.Box.Bottom)).ToList();
            }
            if (heads.Count > 0)
            {
                float max = heads.Max(Area);
                heads = heads.Where(h => Area(h) >= 0.25f * max).ToList();
                // Secondary heads at the very bottom (chibis, logos) are ignored when there is a head higher up.
                var upper = heads.Where(h => h.Top < 0.70f * a.Height).ToList();
                if (upper.Count > 0) heads = upper;
            }
            count = heads.Count;
            return Union(heads);
        }

        /// <summary>Clamp that tolerates max &lt; min (tiny negative values from floating point rounding): returns min.</summary>
        private static double SafeClamp(double v, double min, double max) => max <= min ? min : Math.Clamp(v, min, max);

        /// <param name="target">Phone wallpaper size (default 1080x2400).</param>
        public static FramingPlan AutoPlan(ImageAnalysis a, FramingCalibration calibration = null, TargetSize? target = null)
        {
            var c = calibration ?? FramingCalibration.Current;
            var size = target ?? TargetSize.Default;
            int canvasW = size.Width, canvasH = size.Height;
            int ow = a.Width, oh = a.Height;
            double baseScale = canvasW / (double)ow;          // fit the phone width
            double maxZoomNative = 1.0 / baseScale;           // never upscale above the original resolution
            double maxAutoZoom = c.MaxAutoZoom;
            var notes = new List<string>();

            RectangleF head = MainHeads(a, out int headCount);
            if (head.IsEmpty)
            {
                var p = FramingPlan.Centered(ow, oh, size, 1.0, c.VerticalPosition);
                p.Notes = "no character detected -> full width, centred";
                p.Clamp(ow, oh, maxAutoZoom);
                return p;
            }
            notes.Add(headCount > 1 ? $"{headCount} heads" : "1 head");

            var halfBodies = a.Of("halfbody").Where(d => d.Box.IntersectsWith(head)).Select(d => d.Box).ToList();
            RectangleF body = Union(halfBodies);
            double subjectW = body.IsEmpty ? 2.2 * head.Width : body.Width;
            if (body.IsEmpty) notes.Add("no half-body (head based)");

            // Zoom: show about SubjectWidthFactor x the character width, pulled towards the typical zoom.
            double zoom = ow / (c.SubjectWidthFactor * subjectW);
            zoom = Math.Exp((1 - c.TypicalZoomWeight) * Math.Log(Math.Max(zoom, 1e-3)) + c.TypicalZoomWeight * Math.Log(c.TypicalZoom));
            zoom = Math.Min(zoom, maxAutoZoom);

            // Landscape images: zoom in until the image covers enough of the screen height (fewer mirror copies).
            double coverageZoom = c.MinImageHeightFraction * canvasH / (oh * baseScale);
            if (coverageZoom > zoom)
            {
                zoom = Math.Min(coverageZoom, Math.Min(maxAutoZoom, maxZoomNative));
                notes.Add("zoomed to cover the screen height");
            }
            if (zoom > maxZoomNative) notes.Add("limited to native resolution");
            zoom = Math.Max(1.0, Math.Min(zoom, maxZoomNative));

            double s = baseScale * zoom;
            double ww = canvasW / s, wh = canvasH / s;        // visible window in original pixels
            double cx = body.IsEmpty ? head.Left + head.Width / 2 : body.Left + body.Width / 2;
            double left = SafeClamp(cx - ww / 2, 0, ow - ww);

            double y;
            if (oh * s >= canvasH)
            {
                // Taller than the screen: crop vertically, keeping the head near the top.
                double top = SafeClamp(head.Top + c.HeadroomFraction * wh, 0, oh - wh);
                y = -top * s;
                notes.Add("cropped vertically");
            }
            else
            {
                // Shorter than the screen: mirrors above and below.
                y = (canvasH - oh * s) * c.VerticalPosition;
                notes.Add("mirrors above / below");
            }

            var plan = new FramingPlan
            {
                CanvasW = canvasW,
                CanvasH = canvasH,
                Scale = s,
                X = -left * s,
                Y = y,
                Source = PlanSource.Auto,
            };
            plan.Clamp(ow, oh, maxAutoZoom);
            plan.Notes = $"{string.Join(", ", notes)} -> zoom x{plan.Zoom(ow):F2}";
            return plan;
        }
    }
}
