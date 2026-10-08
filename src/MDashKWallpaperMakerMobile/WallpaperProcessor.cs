using System;

namespace MDashKWallpaperMakerMobile
{
    internal enum ProcessStatus
    {
        Processed,
        NotProcessed,
    }

    internal sealed class ProcessResult
    {
        public ProcessStatus Status { get; init; }
        public string Reason { get; init; }
        public RgbImage Image { get; init; }

        /// <summary>Framing used (null for the "mirror on the right" layout).</summary>
        public FramingPlan Plan { get; init; }

        /// <summary>Detections, when smart framing ran for this image.</summary>
        public ImageAnalysis Analysis { get; init; }

        public static ProcessResult Skip(string reason) => new() { Status = ProcessStatus.NotProcessed, Reason = reason };
        public static ProcessResult Done(RgbImage img) => new() { Status = ProcessStatus.Processed, Image = img };
    }

    /// <summary>
    /// Phone wallpaper rules: the desktop rules turned 90 degrees. The image is fitted to the phone WIDTH; when it is
    /// shorter than the screen, blurred mirror copies fill the space above and below; when it is taller, it is cropped
    /// vertically. Limits and blur scale with the chosen phone resolution.
    /// </summary>
    internal sealed class WallpaperProcessor
    {
        /// <summary>Wallpaper resolution to produce (portrait).</summary>
        public TargetSize Target { get; set; } = TargetSize.Default;

        private int TargetW => Target.Width;
        private int TargetH => Target.Height;

        /// <summary>Gaussian blur of the mirror copies (4 px at 1080 px width).</summary>
        private double BlurRadius => Target.BlurSigma;

        /// <summary>Maximum zoom (relative to fit-to-width) allowed in the preview editor.</summary>
        public const double MaxManualZoom = 3.0;

        /// <summary>Max relative difference between width and height for an image to be considered square (0.10 = 10%).</summary>
        public double SquareTolerance { get; set; } = 0.10;

        private readonly Action<string> _log;

        public WallpaperProcessor(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        public static bool IsSquare(int w, int h, double tolerance)
        {
            if (w == h) return true;
            return Math.Abs(w - h) / (double)Math.Max(w, h) <= tolerance;
        }

        /// <summary>Uses the anime detection models to choose zoom / crop (falls back to the classic rules when null).</summary>
        public ModelHub Models { get; set; }

        public static bool IsTooSmall(int w, int h, TargetSize target) => h < target.Height && w < target.Width;

        /// <summary>
        /// Returns why an image of this size cannot be processed for <paramref name="target"/>, or null when it can.
        /// A manual plan (set in the preview) overrides the "already the target size" and "square" checks.
        /// </summary>
        public static string SkipReason(int w, int h, double squareTolerance, bool hasManualPlan, TargetSize target)
        {
            if (IsTooSmall(w, h, target))
                return $"Image is too small ({w}x{h}) for {target}; needs at least {target.Width} width or {target.Height} height." +
                       (Waifu2xUpscaler.Available ? " It can be upscaled with waifu2x in Preview / Adjust." : "");
            if (hasManualPlan) return null;
            if (w == target.Width && h == target.Height)
                return $"Image is already {target} (nothing to do).";
            if (IsSquare(w, h, squareTolerance))
                return $"Image is square ({w}x{h}) - must be processed manually (use Preview / Adjust).";
            return null;
        }

        /// <summary>True for images handled with a zoom/crop plan (everything except narrow and tall images).</summary>
        public static bool UsesPlan(int w, int h, TargetSize target) => w >= target.Width;

        /// <param name="manualPlan">Framing chosen by the user in the preview window (optional).</param>
        /// <param name="analysis">Detections, when already computed (e.g. by the preview); computed here if needed.</param>
        /// <param name="useAi">False to use the classic rules for this image even when the models are loaded.</param>
        public ProcessResult Process(RgbImage src, FramingPlan manualPlan = null, ImageAnalysis analysis = null, bool useAi = true)
        {
            int w = src.Width, h = src.Height;

            string skip = SkipReason(w, h, SquareTolerance, manualPlan != null, Target);
            if (skip != null) return ProcessResult.Skip(skip);

            // Narrow and tall: fixed "image on the left + mirror on the right" layout.
            if (!UsesPlan(w, h, Target))
            {
                _log($"    Rule: width < {TargetW} and height >= {TargetH} -> image on the left + blurred mirror on the right");
                return MirrorRight(src);
            }

            FramingPlan plan = manualPlan;
            if (plan != null && (plan.CanvasW != TargetW || plan.CanvasH != TargetH))
            {
                _log($"    Manual framing was made for {plan.Target} - adapted to {Target}");
                plan = plan.ConvertTo(Target, w, h, MaxManualZoom);
            }
            if (plan != null)
            {
                _log($"    Manual framing: zoom x{plan.Zoom(w):F2}, position ({plan.X:F0},{plan.Y:F0})");
            }
            else if (Models != null && useAi)
            {
                analysis ??= SmartFramer.Analyze(src, Models);
                plan = SmartFramer.AutoPlan(analysis, null, Target);
                _log($"    Smart framing ({analysis.AnalysisMs} ms): {plan.Notes}");
            }

            if (plan == null)
            {
                plan = ClassicPlan(w, h, Target);
                _log($"    Classic rule: fit the width ({(plan.Scale == 1.0 ? "crop only" : "bicubic resize")}), " +
                     (h * plan.Scale >= TargetH ? "crop the height (centred)" : "centred + blurred mirrors above and below"));
            }

            var rendered = RenderPlan(src, plan);
            return new ProcessResult { Status = rendered.Status, Image = rendered.Image, Reason = rendered.Reason, Plan = plan, Analysis = analysis };
        }

        /// <summary>Classic rule turned for phones: fit the width (crop when only slightly wider), centred.</summary>
        public static FramingPlan ClassicPlan(int ow, int oh, TargetSize t)
        {
            double s = ow <= t.CropInsteadOfResizeMaxW ? 1.0 : t.Width / (double)ow;
            var p = FramingPlan.Centered(ow, oh, t, s / (t.Width / (double)ow), 0.5);
            p.Notes = "classic rules (AI off)";
            p.Clamp(ow, oh, MaxManualZoom);
            return p;
        }

        public ProcessResult RenderPlan(RgbImage src, FramingPlan plan)
        {
            int tiles = PlanRenderer.TilesPerSide(src.Height, plan);
            int sw = (int)Math.Round(src.Width * plan.Scale), sh = (int)Math.Round(src.Height * plan.Scale);
            _log(sw == src.Width && sh == src.Height
                ? $"    No resize (native resolution) {sw}x{sh}"
                : $"    Resize (bicubic) {src.Width}x{src.Height} -> {sw}x{sh}");
            if (tiles > 0)
                _log($"    Placed at x={plan.X:F0}, y={plan.Y:F0}; {tiles} blurred mirror cop{(tiles == 1 ? "y" : "ies")} above / below (Gaussian blur {BlurRadius:0.#}px)");
            else
                _log($"    Crop to {plan.Target} at x={-plan.X:F0}, y={-plan.Y:F0}");
            return ProcessResult.Done(PlanRenderer.Render(src, plan));
        }

        /// <summary>Narrow, tall image: fit the height, image on the left, blurred horizontal mirror on the right.</summary>
        private ProcessResult MirrorRight(RgbImage src)
        {
            RgbImage left;
            if (src.Height == TargetH)
            {
                left = src;
            }
            else if (src.Height <= Target.CropInsteadOfResizeMaxH)
            {
                int cut = src.Height - TargetH;
                _log($"    Canvas crop height {src.Height} -> {TargetH} (top {cut / 2}px, bottom {cut - cut / 2}px)");
                left = src.CropCentered(src.Width, TargetH);
            }
            else
            {
                int newW = Math.Max(1, (int)Math.Round(src.Width * (double)TargetH / src.Height, MidpointRounding.AwayFromZero));
                _log($"    Resize (bicubic) {src.Width}x{src.Height} -> {newW}x{TargetH}");
                left = src.Resize(newW, TargetH);
            }

            int lw = left.Width;
            if (lw * 2 < TargetW)
            {
                return ProcessResult.Skip(
                    $"After bringing the height to {TargetH}, the image is {lw}x{TargetH}; image plus mirror is only {lw * 2}px wide " +
                    $"(< {TargetW}) - {TargetW - lw * 2}px of white border would remain on the right.");
            }

            _log($"    Mirror: flip horizontal + Gaussian blur {BlurRadius:0.#}px");
            RgbImage mirror = left.FlipHorizontal().GaussianBlur(BlurRadius);

            _log($"    Canvas {lw}x{TargetH} -> {TargetW}x{TargetH} (left anchor), mirror at x={lw}");
            var canvas = new RgbImage(TargetW, TargetH, 255);
            canvas.Paste(left, 0, 0);
            canvas.Paste(mirror, lw, 0);
            return ProcessResult.Done(canvas);
        }
    }
}
