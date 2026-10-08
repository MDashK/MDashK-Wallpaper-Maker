using System;

namespace MDashKWallpaperMakerMobile
{
    /// <summary>
    /// Renders a <see cref="FramingPlan"/>: scaled image + blurred mirror tiles above and below it
    /// (vertically flipped, normal, flipped, ...).
    /// </summary>
    internal static class PlanRenderer
    {
        /// <param name="src">Source image. <paramref name="scale"/>, <paramref name="x"/>, <paramref name="y"/> are relative to it.</param>
        /// <param name="canvasW">Output width (the phone width for the final image, smaller for previews).</param>
        /// <param name="blurSigma">Mirror blur in output pixels (4 at 1080 px width).</param>
        public static RgbImage Render(RgbImage src, double scale, double x, double y, int canvasW, int canvasH, double blurSigma,
                                      double cubicA = Resampler.DefaultA)
        {
            int w = Math.Max(1, (int)Math.Round(src.Width * scale));
            int h = Math.Max(1, (int)Math.Round(src.Height * scale));
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);

            RgbImage scaled = (w == src.Width && h == src.Height) ? src : src.Resize(w, h, cubicA);

            // Only the columns that end up on the canvas matter (plus a margin so the blur near the cut is correct).
            int margin = (int)Math.Ceiling(blurSigma * 3) + 1;
            int x0 = Math.Max(0, -ix - margin);
            int x1 = Math.Min(w, canvasW - ix + margin);
            RgbImage strip = (x0 == 0 && x1 == w) ? scaled : scaled.Crop(x0, 0, x1 - x0, h);

            var canvas = new RgbImage(canvasW, canvasH, 255);
            if (iy > 0 || iy + h < canvasH)
            {
                RgbImage blurred = strip.GaussianBlur(blurSigma);
                RgbImage flipped = blurred.FlipVertical();
                for (int k = 1; iy + k * h < canvasH; k++)
                    canvas.Paste(k % 2 == 1 ? flipped : blurred, ix + x0, iy + k * h);
                for (int k = 1; iy - k * h + h > 0; k++)
                    canvas.Paste(k % 2 == 1 ? flipped : blurred, ix + x0, iy - k * h);
            }
            canvas.Paste(strip, ix + x0, iy);
            return canvas;
        }

        public static RgbImage Render(RgbImage original, FramingPlan plan) =>
            Render(original, plan.Scale, plan.X, plan.Y, plan.CanvasW, plan.CanvasH, plan.Target.BlurSigma);

        /// <summary>Number of mirror tiles needed above / below (for the log).</summary>
        public static int TilesPerSide(int originalH, FramingPlan plan)
        {
            double h = originalH * plan.Scale;
            if (h >= plan.CanvasH) return 0;
            double gap = Math.Max(plan.Y, plan.CanvasH - plan.Y - h);
            return (int)Math.Ceiling(gap / h);
        }
    }
}
