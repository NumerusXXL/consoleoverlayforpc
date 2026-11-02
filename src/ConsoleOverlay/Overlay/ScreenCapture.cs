using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Takes a snapshot of a monitor to use as the background behind the overlay. It is shown sharp at
/// first (so opening doesn't jump) and then blurred. Half resolution keeps it light on large screens.
/// </summary>
internal static class ScreenCapture
{
    /// <summary>
    /// A blurred copy, made once: the snapshot shrunk to a fifth, softened, and later stretched back up with
    /// smooth scaling. That looks like a strong blur but costs nothing per frame, unlike a live blur effect.
    /// </summary>
    public static BitmapSource? Blur(BitmapSource source)
    {
        try
        {
            int width = Math.Max(1, source.PixelWidth / 5);
            int height = Math.Max(1, source.PixelHeight / 5);
            var image = new System.Windows.Controls.Image
            {
                Source = source,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = 3, KernelType = KernelType.Gaussian },
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Measure(new System.Windows.Size(width, height));
            image.Arrange(new Rect(0, 0, width, height));

            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(image);
            target.Freeze();
            return target;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not blur the snapshot: {ex.Message}");
            return null;
        }
    }

    public static BitmapSource? Capture(Native.RECT area, int downscale = 2)
    {
        try
        {
            int width = Math.Max(1, area.Width);
            int height = Math.Max(1, area.Height);

            using var full = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(area.Left, area.Top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);

            int smallWidth = Math.Max(1, width / downscale);
            int smallHeight = Math.Max(1, height / downscale);
            using var small = new Bitmap(smallWidth, smallHeight, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
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
