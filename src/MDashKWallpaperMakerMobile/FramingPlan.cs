using System;
using System.Text.RegularExpressions;

namespace MDashKWallpaperMakerMobile
{
    internal enum PlanSource
    {
        Auto,
        Manual,
    }

    /// <summary>
    /// Phone wallpaper resolution (portrait). The desktop rules were made for 1080 lines; here the reference is the
    /// 1080 px width of a typical phone, and limits and blur scale with the chosen width.
    /// </summary>
    internal readonly struct TargetSize : IEquatable<TargetSize>
    {
        public static readonly TargetSize Default = new(1080, 2400);

        public const int MinSide = 320;
        public const int MaxSide = 16384;

        public int Width { get; }
        public int Height { get; }

        public TargetSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        /// <summary>Mirror blur: Gaussian radius 4 px at 1080 px width, proportional for other widths.</summary>
        public double BlurSigma => 4.0 * Width / 1080.0;

        /// <summary>Classic rule: widths up to this value are centre-cropped instead of resized (1200 at 1080).</summary>
        public int CropInsteadOfResizeMaxW => (int)Math.Round(Width * 1200.0 / 1080.0);

        /// <summary>Classic rule: heights up to this value are centre-cropped instead of resized (narrow, tall images).</summary>
        public int CropInsteadOfResizeMaxH => (int)Math.Round(Height * 2100.0 / 1920.0);

        /// <summary>Accepts "1080x2400", "1080 x 2400", "1080*2400", "1170×2532 (iPhone)"...</summary>
        public static bool TryParse(string text, out TargetSize size)
        {
            size = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Regex.Match(text, @"(\d{2,5})\s*[xX×*]\s*(\d{2,5})");
            if (!m.Success) return false;
            int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
            if (w < MinSide || h < MinSide || w > MaxSide || h > MaxSide) return false;
            size = new TargetSize(w, h);
            return true;
        }

        public bool Equals(TargetSize other) => Width == other.Width && Height == other.Height;
        public override bool Equals(object obj) => obj is TargetSize t && Equals(t);
        public override int GetHashCode() => HashCode.Combine(Width, Height);
        public static bool operator ==(TargetSize a, TargetSize b) => a.Equals(b);
        public static bool operator !=(TargetSize a, TargetSize b) => !a.Equals(b);
        public override string ToString() => $"{Width}x{Height}";
    }

    /// <summary>
    /// Where the (scaled) original goes on the phone wallpaper canvas. The image always covers the full width; when it is
    /// shorter than the canvas, the space above and below is filled with blurred mirror copies (alternating vertically
    /// flipped / normal) placed directly above and below the image.
    /// </summary>
    internal sealed class FramingPlan
    {
        /// <summary>Wallpaper size this plan was made for.</summary>
        public int CanvasW { get; set; } = TargetSize.Default.Width;
        public int CanvasH { get; set; } = TargetSize.Default.Height;

        public TargetSize Target => new(CanvasW, CanvasH);

        /// <summary>Output pixels per original pixel.</summary>
        public double Scale { get; set; }

        /// <summary>Position of the scaled image's top-left corner on the canvas (usually &lt;= 0 when cropped).</summary>
        public double X { get; set; }
        public double Y { get; set; }

        public PlanSource Source { get; set; } = PlanSource.Auto;

        /// <summary>Human readable explanation of how the auto plan was chosen (for the log / preview).</summary>
        public string Notes { get; set; }

        public FramingPlan Clone() => (FramingPlan)MemberwiseClone();

        public double ScaledW(int originalW) => originalW * Scale;
        public double ScaledH(int originalH) => originalH * Scale;

        /// <summary>Zoom relative to "fit to the canvas width".</summary>
        public double Zoom(int originalW) => Scale / (CanvasW / (double)originalW);

        /// <summary>Forces the plan into a valid state: image covers the full width, never leaves the canvas.</summary>
        public void Clamp(int ow, int oh, double maxZoom)
        {
            double minScale = CanvasW / (double)ow;
            Scale = SafeClamp(Scale, minScale, minScale * maxZoom);
            double w = ow * Scale, h = oh * Scale;
            X = SafeClamp(X, CanvasW - w, 0);
            if (h >= CanvasH) Y = SafeClamp(Y, CanvasH - h, 0);
            else Y = SafeClamp(Y, 0, CanvasH - h);
        }

        /// <summary>
        /// Same framing for another wallpaper size: the zoom relative to "fit width" is kept and the same original row
        /// stays at the vertical centre of the canvas.
        /// </summary>
        public FramingPlan ConvertTo(TargetSize target, int ow, int oh, double maxZoom)
        {
            if (target.Width == CanvasW && target.Height == CanvasH) return Clone();
            double k = target.Width / (double)CanvasW;
            var p = Clone();
            p.CanvasW = target.Width;
            p.CanvasH = target.Height;
            p.Scale = Scale * k;
            p.X = X * k;
            double centreOriginalY = (CanvasH / 2.0 - Y) / Scale;
            p.Y = target.Height / 2.0 - centreOriginalY * p.Scale;
            p.Clamp(ow, oh, maxZoom);
            return p;
        }

        // Tolerates max < min caused by floating point rounding (returns the bound closest to "covering the canvas").
        private static double SafeClamp(double v, double min, double max) => max < min ? max : Math.Clamp(v, min, max);

        public static FramingPlan Centered(int ow, int oh, TargetSize target, double zoom = 1.0, double verticalPos = 0.5)
        {
            double s = target.Width / (double)ow * zoom;
            double w = ow * s, h = oh * s;
            return new FramingPlan
            {
                CanvasW = target.Width,
                CanvasH = target.Height,
                Scale = s,
                X = (target.Width - w) / 2,
                Y = h >= target.Height ? -(h - target.Height) * verticalPos : (target.Height - h) * verticalPos,
            };
        }

        public override string ToString() => $"zoom x{Scale:F4} at ({X:F0},{Y:F0}) on {CanvasW}x{CanvasH}";
    }
}
