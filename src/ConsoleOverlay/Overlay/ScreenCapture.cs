using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Takes a small snapshot of a monitor to use as the blurred background behind the overlay.
/// Shrinking the image already blurs it and keeps the blur effect cheap on large screens.
/// </summary>
internal static class ScreenCapture
{
    public static BitmapSource? Capture(Native.RECT area, int downscale = 8)
    {
        try
        {
            int width = Math.Max(1, area.Width);
            int height = Math.Max(1, area.Height);

            using var full = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(area.Left, area.Top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);

            int smallWidth = Math.Max(1, width / downscale);
            int smallHeight = Math.Max(1, height / downscale);
            using var small = new Bitmap(smallWidth, smallHeight, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, 0, 0, smallWidth, smallHeight);
            }

            var hBitmap = small.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                Native.DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Screen snapshot failed, using a plain background: {ex.Message}");
            return null;
        }
    }
}
