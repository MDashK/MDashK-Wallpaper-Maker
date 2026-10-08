using System;

namespace MDashKWallpaperMaker
{
    /// <summary>Renders a <see cref="FramingPlan"/>: scaled image + blurred mirror tiles (flipped, normal, flipped, ...).</summary>
    internal static class PlanRenderer
    {
        /// <param name="src">Source image. <paramref name="scale"/>, <paramref name="x"/>, <paramref name="y"/> are relative to it.</param>
        /// <param name="canvasW">Output width (1920 for the final image, smaller for previews).</param>
        /// <param name="blurSigma">Mirror blur in output pixels (4 for the final image).</param>
        public static RgbImage Render(RgbImage src, double scale, double x, double y, int canvasW, int canvasH, double blurSigma,
                                      double cubicA = Resampler.DefaultA)
        {
            int w = Math.Max(1, (int)Math.Round(src.Width * scale));
            int h = Math.Max(1, (int)Math.Round(src.Height * scale));
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);

            RgbImage scaled = (w == src.Width && h == src.Height) ? src : src.Resize(w, h, cubicA);

            // Only the rows that end up on the canvas matter (plus a margin so the blur near the cut is correct).
            int margin = (int)Math.Ceiling(blurSigma * 3) + 1;
            int y0 = Math.Max(0, -iy - margin);
            int y1 = Math.Min(h, canvasH - iy + margin);
            RgbImage strip = (y0 == 0 && y1 == h) ? scaled : scaled.Crop(0, y0, w, y1 - y0);

            var canvas = new RgbImage(canvasW, canvasH, 255);
            if (ix > 0 || ix + w < canvasW)
            {
                RgbImage blurred = strip.GaussianBlur(blurSigma);
                RgbImage flipped = blurred.FlipHorizontal();
                for (int k = 1; ix + k * w < canvasW; k++)
                    canvas.Paste(k % 2 == 1 ? flipped : blurred, ix + k * w, iy + y0);
                for (int k = 1; ix - k * w + w > 0; k++)
                    canvas.Paste(k % 2 == 1 ? flipped : blurred, ix - k * w, iy + y0);
            }
            canvas.Paste(strip, ix, iy + y0);
            return canvas;
        }

        public static RgbImage Render(RgbImage original, FramingPlan plan) =>
            Render(original, plan.Scale, plan.X, plan.Y, plan.CanvasW, plan.CanvasH, plan.Target.BlurSigma);

        /// <summary>Number of mirror tiles needed on each side (for the log).</summary>
        public static int TilesPerSide(int originalW, FramingPlan plan)
        {
            double w = originalW * plan.Scale;
            if (w >= plan.CanvasW) return 0;
            double gap = Math.Max(plan.X, plan.CanvasW - plan.X - w);
            return (int)Math.Ceiling(gap / w);
        }
    }
}
