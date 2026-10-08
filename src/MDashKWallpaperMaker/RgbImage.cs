using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MDashKWallpaperMaker
{
    /// <summary>
    /// Simple opaque 24-bit image buffer (BGR, tightly packed rows) with the operations needed for wallpaper processing.
    /// </summary>
    internal sealed class RgbImage
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Data { get; }
        public int Stride => Width * 3;

        public RgbImage(int width, int height)
        {
            Width = width;
            Height = height;
            Data = new byte[(long)width * height * 3];
        }

        public RgbImage(int width, int height, byte fill) : this(width, height)
        {
            if (fill != 0) Array.Fill(Data, fill);
        }

        // ------------------------------------------------------------------ Loading / saving

        /// <summary>
        /// Loads an image from disk (Unicode-safe), applies EXIF orientation and flattens any transparency onto white.
        /// </summary>
        public static RgbImage Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            Exception gdiError;
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
                ApplyExifOrientation(img);
                using var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
                bmp.SetResolution(96, 96);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
                }
                return FromArgbBitmap(bmp);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ExternalException || ex is OutOfMemoryException)
            {
                gdiError = ex;
            }

            // Fallback: Windows Imaging Component (supports WebP/HEIC/AVIF... when the codecs are installed).
            try
            {
                return WicLoader.Load(bytes);
            }
            catch (Exception)
            {
                throw new InvalidDataException("Not a valid image, corrupted file or unsupported format.", gdiError);
            }
        }

        private static void ApplyExifOrientation(Image img)
        {
            const int OrientationId = 0x0112;
            if (Array.IndexOf(img.PropertyIdList, OrientationId) < 0) return;
            var prop = img.GetPropertyItem(OrientationId);
            if (prop?.Value == null || prop.Value.Length < 1) return;
            int orientation = prop.Value[0];
            RotateFlipType rf = orientation switch
            {
                2 => RotateFlipType.RotateNoneFlipX,
                3 => RotateFlipType.Rotate180FlipNone,
                4 => RotateFlipType.Rotate180FlipX,
                5 => RotateFlipType.Rotate90FlipX,
                6 => RotateFlipType.Rotate90FlipNone,
                7 => RotateFlipType.Rotate270FlipX,
                8 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone,
            };
            if (rf != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(rf);
        }

        private static RgbImage FromArgbBitmap(Bitmap bmp)
        {
            var result = new RgbImage(bmp.Width, bmp.Height);
            var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                FromBgra(bd.Scan0, bd.Stride, result);
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return result;
        }

        /// <summary>Copies straight (non-premultiplied) BGRA pixels into the image, compositing alpha over white.</summary>
        internal static unsafe void FromBgra(IntPtr scan0, int stride, RgbImage dst)
        {
            int w = dst.Width;
            byte[] data = dst.Data;
            Parallel.For(0, dst.Height, y =>
            {
                byte* src = (byte*)scan0 + (long)y * stride;
                long o = (long)y * w * 3;
                for (int x = 0; x < w; x++, src += 4, o += 3)
                {
                    int a = src[3];
                    if (a == 255)
                    {
                        data[o] = src[0];
                        data[o + 1] = src[1];
                        data[o + 2] = src[2];
                    }
                    else
                    {
                        int inv = 255 - a;
                        data[o] = (byte)((src[0] * a + 255 * inv + 127) / 255);
                        data[o + 1] = (byte)((src[1] * a + 255 * inv + 127) / 255);
                        data[o + 2] = (byte)((src[2] * a + 255 * inv + 127) / 255);
                    }
                }
            });
        }

        /// <summary>Creates a 24-bit GDI+ bitmap with the same pixels (for display).</summary>
        public Bitmap ToBitmap()
        {
            var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                for (int y = 0; y < Height; y++)
                    Marshal.Copy(Data, y * Stride, bd.Scan0 + y * bd.Stride, Stride);
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return bmp;
        }

        /// <summary>Saves as an opaque 24-bit PNG (Unicode-safe path).</summary>
        public void SavePng(string path)
        {
            using var bmp = ToBitmap();
            bmp.SetResolution(72, 72);

            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                bmp.Save(fs, ImageFormat.Png);
            File.Move(tmp, path, overwrite: true);
        }

        // ------------------------------------------------------------------ Geometry

        public RgbImage Crop(int x, int y, int w, int h)
        {
            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > Width || y + h > Height)
                throw new ArgumentOutOfRangeException(nameof(w), "Crop rectangle outside of image.");
            var r = new RgbImage(w, h);
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(Data, ((y + row) * Width + x) * 3, r.Data, row * w * 3, w * 3);
            return r;
        }

        /// <summary>Centered crop ("canvas size" reduction with centre anchor).</summary>
        public RgbImage CropCentered(int w, int h) => Crop((Width - w) / 2, (Height - h) / 2, w, h);

        public RgbImage FlipHorizontal()
        {
            var r = new RgbImage(Width, Height);
            int w = Width;
            Parallel.For(0, Height, y =>
            {
                int row = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    int s = row + x * 3, d = row + (w - 1 - x) * 3;
                    r.Data[d] = Data[s];
                    r.Data[d + 1] = Data[s + 1];
                    r.Data[d + 2] = Data[s + 2];
                }
            });
            return r;
        }

        public RgbImage FlipVertical()
        {
            var r = new RgbImage(Width, Height);
            for (int y = 0; y < Height; y++)
                Buffer.BlockCopy(Data, y * Stride, r.Data, (Height - 1 - y) * Stride, Stride);
            return r;
        }

        /// <summary>Pastes <paramref name="src"/> at (dx, dy); anything outside this image is clipped.</summary>
        public void Paste(RgbImage src, int dx, int dy)
        {
            int x0 = Math.Max(0, dx), y0 = Math.Max(0, dy);
            int x1 = Math.Min(Width, dx + src.Width), y1 = Math.Min(Height, dy + src.Height);
            if (x1 <= x0 || y1 <= y0) return;
            int bytes = (x1 - x0) * 3;
            for (int y = y0; y < y1; y++)
                Buffer.BlockCopy(src.Data, ((y - dy) * src.Width + (x0 - dx)) * 3, Data, (y * Width + x0) * 3, bytes);
        }

        // ------------------------------------------------------------------ Filters

        /// <summary>
        /// Gaussian blur. Photoshop's "Radius" behaves as the standard deviation of the Gaussian
        /// (verified against the reference images: sigma 4 matches "Radius 4 px" with ~0.3/255 mean error).
        /// Edges are handled by replicating the border pixels.
        /// </summary>
        public RgbImage GaussianBlur(double sigma)
        {
            int radius = (int)Math.Ceiling(sigma * 3.0);
            var kernel = new float[radius * 2 + 1];
            double sum = 0;
            for (int i = -radius; i <= radius; i++)
            {
                double v = Math.Exp(-(i * i) / (2 * sigma * sigma));
                kernel[i + radius] = (float)v;
                sum += v;
            }
            for (int i = 0; i < kernel.Length; i++) kernel[i] = (float)(kernel[i] / sum);

            int w = Width, h = Height;
            var tmp = new float[(long)w * h * 3];
            var src = Data;

            // Horizontal pass
            Parallel.For(0, h, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float b = 0, g = 0, r = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int xx = x + k;
                        if (xx < 0) xx = 0; else if (xx >= w) xx = w - 1;
                        int s = (row + xx) * 3;
                        float kv = kernel[k + radius];
                        b += src[s] * kv; g += src[s + 1] * kv; r += src[s + 2] * kv;
                    }
                    int d = (row + x) * 3;
                    tmp[d] = b; tmp[d + 1] = g; tmp[d + 2] = r;
                }
            });

            // Vertical pass
            var result = new RgbImage(w, h);
            var dst = result.Data;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float b = 0, g = 0, r = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int yy = y + k;
                        if (yy < 0) yy = 0; else if (yy >= h) yy = h - 1;
                        int s = (yy * w + x) * 3;
                        float kv = kernel[k + radius];
                        b += tmp[s] * kv; g += tmp[s + 1] * kv; r += tmp[s + 2] * kv;
                    }
                    int d = (y * w + x) * 3;
                    dst[d] = ToByte(b); dst[d + 1] = ToByte(g); dst[d + 2] = ToByte(r);
                }
            });
            return result;
        }

        // ------------------------------------------------------------------ Resampling

        /// <summary>
        /// Bicubic resize (separable). When reducing, the kernel is widened by the scale factor so that every
        /// source pixel contributes (proper anti-aliasing, like Photoshop's bicubic modes).
        /// </summary>
        public RgbImage Resize(int newWidth, int newHeight, double cubicA = Resampler.DefaultA)
        {
            if (newWidth == Width && newHeight == Height) return Crop(0, 0, Width, Height);
            return Resampler.Resize(this, newWidth, newHeight, cubicA);
        }

        internal static byte ToByte(float v)
        {
            int i = (int)(v + 0.5f);
            return i < 0 ? (byte)0 : i > 255 ? (byte)255 : (byte)i;
        }
    }
}
