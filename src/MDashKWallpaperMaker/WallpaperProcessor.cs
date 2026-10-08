using System;
using System.Collections.Generic;

namespace MDashKWallpaperMaker
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

        /// <summary>Framing used (null for the classic rules / "mirror below" layout).</summary>
        public FramingPlan Plan { get; init; }

        /// <summary>Detections, when smart framing ran for this image.</summary>
        public ImageAnalysis Analysis { get; init; }

        public static ProcessResult Skip(string reason) => new() { Status = ProcessStatus.NotProcessed, Reason = reason };
        public static ProcessResult Done(RgbImage img) => new() { Status = ProcessStatus.Processed, Image = img };
    }

    /// <summary>
    /// Implements the wallpaper rules. They were defined for 1920x1080; for other resolutions the limits
    /// (e.g. "too small", "crop instead of resize") and the mirror blur scale with the chosen size.
    /// </summary>
    internal sealed class WallpaperProcessor
    {
        /// <summary>Wallpaper resolution to produce.</summary>
        public TargetSize Target { get; set; } = TargetSize.FullHd;

        private int TargetW => Target.Width;
        private int TargetH => Target.Height;

        /// <summary>Heights up to this value are centre-cropped to the target height instead of resized.</summary>
        private int CropInsteadOfResizeMaxH => Target.CropInsteadOfResizeMaxH;

        /// <summary>Widths up to this value are centre-cropped to the target width instead of resized (wide, short images).</summary>
        private int CropInsteadOfResizeMaxW => Target.CropInsteadOfResizeMaxW;

        /// <summary>Gaussian blur of the mirror copies (4 px at 1080 lines).</summary>
        private double BlurRadius => Target.BlurSigma;

        /// <summary>Maximum zoom (relative to fit-to-height) allowed in the preview editor.</summary>
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

        /// <summary>
        /// Returns why an image of this size cannot be processed for <paramref name="target"/>, or null when it can.
        /// A manual plan (set in the preview) overrides the "already the target size" and "square" checks.
        /// </summary>
        public static string SkipReason(int w, int h, double squareTolerance, bool hasManualPlan, TargetSize target)
        {
            if (IsTooSmall(w, h, target))
                return $"Image is too small ({w}x{h}) for {target}; needs at least {target.Height} height or {target.Width} width." +
                       (Waifu2xUpscaler.Available ? " It can be upscaled with waifu2x in Preview / Adjust." : "");
            if (hasManualPlan) return null;
            if (w == target.Width && h == target.Height)
                return $"Image is already {target} (nothing to do).";
            if (IsSquare(w, h, squareTolerance))
                return $"Image is square ({w}x{h}) - must be processed manually (use Preview / Adjust).";
            return null;
        }

        public static bool IsTooSmall(int w, int h, TargetSize target) => h < target.Height && w < target.Width;

        /// <summary>True for images handled with a zoom/crop plan (everything except short and wide images).</summary>
        public static bool UsesPlan(int w, int h, TargetSize target) => h >= target.Height;

        /// <param name="manualPlan">Framing chosen by the user in the preview window (optional).</param>
        /// <param name="analysis">Detections, when already computed (e.g. by the preview); computed here if needed.</param>
        /// <param name="useAi">False to use the classic rules for this image even when the models are loaded.</param>
        public ProcessResult Process(RgbImage src, FramingPlan manualPlan = null, ImageAnalysis analysis = null, bool useAi = true)
        {
            int w = src.Width, h = src.Height;

            string skip = SkipReason(w, h, SquareTolerance, manualPlan != null, Target);
            if (skip != null) return ProcessResult.Skip(skip);

            // Short and wide: always the classic "mirror below" layout.
            if (!UsesPlan(w, h, Target))
            {
                _log($"    Rule: height < {TargetH} and width >= {TargetW} -> top + blurred mirror below");
                return MirrorBelow(src);
            }

            FramingPlan plan = manualPlan;
            if (plan != null && (plan.CanvasW != TargetW || plan.CanvasH != TargetH))
            {
                _log($"    Manual framing was made for {plan.Target} - adapted to {Target}");
                plan = plan.ConvertTo(Target, w, h, MaxManualZoom);
            }
            if (plan != null)
            {
                _log($"    Manual framing: zoom x{plan.Zoom(h):F2}, position ({plan.X:F0},{plan.Y:F0})");
            }
            else if (Models != null && useAi)
            {
                analysis ??= SmartFramer.Analyze(src, Models);
                plan = SmartFramer.AutoPlan(analysis, null, Target);
                _log($"    Smart framing ({analysis.AnalysisMs} ms): {plan.Notes}");
            }

            if (plan == null) return ProcessClassic(src);

            var rendered = RenderPlan(src, plan);
            return new ProcessResult { Status = rendered.Status, Image = rendered.Image, Reason = rendered.Reason, Plan = plan, Analysis = analysis };
        }

        public ProcessResult RenderPlan(RgbImage src, FramingPlan plan)
        {
            int tiles = PlanRenderer.TilesPerSide(src.Width, plan);
            int sw = (int)Math.Round(src.Width * plan.Scale), sh = (int)Math.Round(src.Height * plan.Scale);
            _log(sw == src.Width && sh == src.Height
                ? $"    No resize (native resolution) {sw}x{sh}"
                : $"    Resize (bicubic) {src.Width}x{src.Height} -> {sw}x{sh}");
            if (tiles > 0)
                _log($"    Placed at x={plan.X:F0}, y={plan.Y:F0}; {tiles} blurred mirror cop{(tiles == 1 ? "y" : "ies")} per side (Gaussian blur {BlurRadius:0.#}px)");
            else
                _log($"    Crop to {plan.Target} at x={-plan.X:F0}, y={-plan.Y:F0}");
            return ProcessResult.Done(PlanRenderer.Render(src, plan));
        }

        /// <summary>Original (v1) rules, used when smart framing is off or the models are missing.</summary>
        private ProcessResult ProcessClassic(RgbImage src)
        {
            int w = src.Width, h = src.Height;

            // Portrait (height >= target height and taller than wide)
            if (h >= TargetH && h > w)
            {
                _log($"    Rule: portrait (height >= {TargetH}, height > width) -> centre + blurred mirrors left/right");
                return MirrorSides(src);
            }

            // Landscape, narrower than the target width
            if (h >= TargetH && w < TargetW && w > h)
            {
                _log($"    Rule: landscape narrower than {TargetW} -> centre + blurred mirrors left/right");
                return MirrorSides(src);
            }

            // Large landscape
            if (w >= TargetW && w > h)
            {
                _log($"    Rule: landscape width >= {TargetW} -> fit and crop to {Target}");
                return FitAndCrop(src);
            }

            return ProcessResult.Skip($"No processing rule matches this size ({w}x{h}).");
        }

        // ------------------------------------------------------------------ Rules

        /// <summary>Brings the image to exactly the target height (centre crop when only slightly taller, otherwise bicubic resize).</summary>
        private RgbImage ToTargetHeight(RgbImage src)
        {
            if (src.Height == TargetH) return src;
            if (src.Height <= CropInsteadOfResizeMaxH)
            {
                int cut = src.Height - TargetH;
                _log($"    Canvas crop height {src.Height} -> {TargetH} (top {cut / 2}px, bottom {cut - cut / 2}px)");
                return src.CropCentered(src.Width, TargetH);
            }
            int newW = Math.Max(1, (int)Math.Round(src.Width * (double)TargetH / src.Height, MidpointRounding.AwayFromZero));
            _log($"    Resize (bicubic) {src.Width}x{src.Height} -> {newW}x{TargetH}");
            return src.Resize(newW, TargetH);
        }

        private ProcessResult MirrorSides(RgbImage src)
        {
            RgbImage center = ToTargetHeight(src);
            int cw = center.Width;

            // Canvas of the target width, centre anchor. Mirrors snap to the centre image edges.
            int offset = (TargetW - cw) / 2;
            int leftStart = offset - cw;         // left mirror covers [offset - cw, offset)
            int rightEnd = offset + cw + cw;     // right mirror covers [offset + cw, offset + 2cw)
            if (leftStart > 0 || rightEnd < TargetW)
            {
                int missing = Math.Max(0, leftStart) + Math.Max(0, TargetW - rightEnd);
                return ProcessResult.Skip(
                    $"After resizing to {cw}x{TargetH}, the image plus its two mirrors is only {cw * 3}px wide " +
                    $"(< {TargetW}) - {missing}px of white border would remain.");
            }

            _log($"    Mirror: flip horizontal + Gaussian blur {BlurRadius:0.#}px");
            RgbImage mirror = center.FlipHorizontal().GaussianBlur(BlurRadius);

            _log($"    Canvas {cw}x{TargetH} -> {TargetW}x{TargetH} (centre), image at x={offset}, mirrors at x={leftStart} and x={offset + cw}");
            var canvas = new RgbImage(TargetW, TargetH, 255);
            canvas.Paste(mirror, leftStart, 0);
            canvas.Paste(mirror, offset + cw, 0);
            canvas.Paste(center, offset, 0);
            return ProcessResult.Done(canvas);
        }

        private ProcessResult MirrorBelow(RgbImage src)
        {
            RgbImage top;
            if (src.Width == TargetW)
            {
                top = src;
            }
            else if (src.Width <= CropInsteadOfResizeMaxW)
            {
                int cut = src.Width - TargetW;
                _log($"    Canvas crop width {src.Width} -> {TargetW} (left {cut / 2}px, right {cut - cut / 2}px)");
                top = src.CropCentered(TargetW, src.Height);
            }
            else
            {
                int newH = Math.Max(1, (int)Math.Round(src.Height * (double)TargetW / src.Width, MidpointRounding.AwayFromZero));
                _log($"    Resize (bicubic) {src.Width}x{src.Height} -> {TargetW}x{newH}");
                top = src.Resize(TargetW, newH);
            }

            int th = top.Height;
            if (th * 2 < TargetH)
            {
                return ProcessResult.Skip(
                    $"After bringing the width to {TargetW}, the image is {TargetW}x{th}; image plus mirror is only {th * 2}px high " +
                    $"(< {TargetH}) - {TargetH - th * 2}px of white border would remain at the bottom.");
            }

            _log($"    Mirror: flip vertical + Gaussian blur {BlurRadius:0.#}px");
            RgbImage mirror = top.FlipVertical().GaussianBlur(BlurRadius);

            _log($"    Canvas {TargetW}x{th} -> {TargetW}x{TargetH} (top anchor), mirror at y={th}");
            var canvas = new RgbImage(TargetW, TargetH, 255);
            canvas.Paste(top, 0, 0);
            canvas.Paste(mirror, 0, th);
            return ProcessResult.Done(canvas);
        }

        private ProcessResult FitAndCrop(RgbImage src)
        {
            RgbImage img;
            if (src.Height <= CropInsteadOfResizeMaxH)
            {
                img = ToTargetHeight(src);
            }
            else
            {
                int newW = (int)Math.Round(src.Width * (double)TargetH / src.Height, MidpointRounding.AwayFromZero);
                if (newW >= TargetW)
                {
                    _log($"    Resize (bicubic) {src.Width}x{src.Height} -> {newW}x{TargetH}");
                    img = src.Resize(newW, TargetH);
                }
                else
                {
                    int newH = (int)Math.Round(src.Height * (double)TargetW / src.Width, MidpointRounding.AwayFromZero);
                    _log($"    Height {TargetH} would give width {newW} (< {TargetW}) -> resize (bicubic) to width {TargetW} instead: {TargetW}x{newH}");
                    img = src.Resize(TargetW, newH);
                    if (img.Height > TargetH)
                    {
                        int cut = img.Height - TargetH;
                        _log($"    Canvas crop height {img.Height} -> {TargetH} (top {cut / 2}px, bottom {cut - cut / 2}px)");
                        img = img.CropCentered(TargetW, TargetH);
                    }
                }
            }

            if (img.Width > TargetW)
            {
                int cut = img.Width - TargetW;
                _log($"    Canvas crop width {img.Width} -> {TargetW} (left {cut / 2}px, right {cut - cut / 2}px)");
                img = img.CropCentered(TargetW, TargetH);
            }

            if (img.Width != TargetW || img.Height != TargetH)
                return ProcessResult.Skip($"Unexpected result size {img.Width}x{img.Height}.");

            return ProcessResult.Done(img);
        }
    }
}
