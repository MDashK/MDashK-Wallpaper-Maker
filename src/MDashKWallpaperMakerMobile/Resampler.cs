using System;
using System.Threading.Tasks;

namespace MDashKWallpaperMakerMobile
{
    /// <summary>
    /// Separable bicubic (Keys cubic convolution) resampler.
    /// </summary>
    internal static class Resampler
    {
        /// <summary>
        /// Keys "a" parameter. -0.5 = Catmull-Rom; more negative = sharper.
        /// -2.0 was chosen by comparing against the Photoshop "Bicubic Automatic" (= Bicubic Sharper when reducing)
        /// reference images: it gives the lowest error (e.g. 0.56 vs 1.52 mean abs. error/255 for a=-0.5).
        /// </summary>
        public const double DefaultA = -2.0;

        private static double Cubic(double x, double a)
        {
            x = Math.Abs(x);
            if (x < 1) return ((a + 2) * x - (a + 3)) * x * x + 1;
            if (x < 2) return ((a * x - 5 * a) * x + 8 * a) * x - 4 * a;
            return 0;
        }

        private sealed class Contrib
        {
            public int[] Start;
            public int[] Count;
            public float[] Weights; // [dst * maxTaps + k]
            public int MaxTaps;
        }

        private static Contrib BuildContrib(int srcSize, int dstSize, double a)
        {
            double scale = (double)srcSize / dstSize;
            double filterScale = Math.Max(scale, 1.0);
            double support = 2.0 * filterScale;
            int maxTaps = (int)Math.Ceiling(support * 2) + 2;

            var c = new Contrib
            {
                Start = new int[dstSize],
                Count = new int[dstSize],
                Weights = new float[dstSize * maxTaps],
                MaxTaps = maxTaps,
            };
            var tmp = new double[maxTaps];
            var idx = new int[maxTaps];

            for (int i = 0; i < dstSize; i++)
            {
                double center = (i + 0.5) * scale - 0.5;
                int left = (int)Math.Ceiling(center - support);
                int right = (int)Math.Floor(center + support);
                int n = 0;
                double sum = 0;
                for (int j = left; j <= right && n < maxTaps; j++)
                {
                    double w = Cubic((j - center) / filterScale, a);
                    if (w == 0) continue;
                    tmp[n] = w;
                    idx[n] = Math.Clamp(j, 0, srcSize - 1);
                    sum += w;
                    n++;
                }
                // Replicated edge pixels are merged so we can store a contiguous [start, start+count) range.
                int start = idx[0], end = idx[n - 1];
                int count = end - start + 1;
                c.Start[i] = start;
                c.Count[i] = count;
                int baseW = i * maxTaps;
                for (int k = 0; k < n; k++)
                    c.Weights[baseW + idx[k] - start] += (float)(tmp[k] / sum);
            }
            return c;
        }

        public static RgbImage Resize(RgbImage src, int dstW, int dstH, double a)
        {
            int srcW = src.Width, srcH = src.Height;
            var cx = BuildContrib(srcW, dstW, a);
            var cy = BuildContrib(srcH, dstH, a);

            // Horizontal pass: srcH x dstW (float)
            var tmp = new float[(long)dstW * srcH * 3];
            byte[] s = src.Data;
            Parallel.For(0, srcH, y =>
            {
                long srow = (long)y * srcW * 3;
                long drow = (long)y * dstW * 3;
                for (int x = 0; x < dstW; x++)
                {
                    int start = cx.Start[x], count = cx.Count[x], wb = x * cx.MaxTaps;
                    float b = 0, g = 0, r = 0;
                    long p = srow + start * 3;
                    for (int k = 0; k < count; k++, p += 3)
                    {
                        float w = cx.Weights[wb + k];
                        b += s[p] * w; g += s[p + 1] * w; r += s[p + 2] * w;
                    }
                    long d = drow + x * 3;
                    tmp[d] = b; tmp[d + 1] = g; tmp[d + 2] = r;
                }
            });

            // Vertical pass
            var dst = new RgbImage(dstW, dstH);
            byte[] o = dst.Data;
            int rowLen = dstW * 3;
            Parallel.For(0, dstH, y =>
            {
                int start = cy.Start[y], count = cy.Count[y], wb = y * cy.MaxTaps;
                var acc = new float[rowLen];
                for (int k = 0; k < count; k++)
                {
                    float w = cy.Weights[wb + k];
                    long p = (long)(start + k) * rowLen;
                    for (int i = 0; i < rowLen; i++) acc[i] += tmp[p + i] * w;
                }
                long d = (long)y * rowLen;
                for (int i = 0; i < rowLen; i++) o[d + i] = RgbImage.ToByte(acc[i]);
            });
            return dst;
        }
    }
}
