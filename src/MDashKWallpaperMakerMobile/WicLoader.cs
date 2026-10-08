using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MDashKWallpaperMakerMobile
{
    /// <summary>
    /// Fallback decoder using the Windows Imaging Component (WPF). Handles formats GDI+ does not know
    /// (e.g. WebP, HEIC, AVIF) when the corresponding Windows codecs are installed.
    /// </summary>
    internal static class WicLoader
    {
        public static RgbImage Load(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            frame = ApplyOrientation(frame);

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            int stride = w * 4;
            var result = new RgbImage(w, h);
            IntPtr buffer = Marshal.AllocHGlobal((IntPtr)((long)stride * h));
            try
            {
                converted.CopyPixels(System.Windows.Int32Rect.Empty, buffer, stride * h, stride);
                RgbImage.FromBgra(buffer, stride, result);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        private static BitmapSource ApplyOrientation(BitmapSource frame)
        {
            int orientation = 1;
            try
            {
                if (frame.Metadata is BitmapMetadata md)
                {
                    object v = md.GetQuery("System.Photo.Orientation");
                    if (v != null) orientation = Convert.ToInt32(v);
                }
            }
            catch
            {
                // No/unsupported metadata: keep as-is.
            }

            Transform t = orientation switch
            {
                2 => new ScaleTransform(-1, 1),
                3 => new RotateTransform(180),
                4 => new ScaleTransform(1, -1),
                5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
                6 => new RotateTransform(90),
                7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
                8 => new RotateTransform(270),
                _ => null,
            };
            return t == null ? frame : new TransformedBitmap(frame, t);
        }
    }
}
