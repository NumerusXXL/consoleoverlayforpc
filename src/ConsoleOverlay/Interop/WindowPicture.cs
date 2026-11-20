using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ConsoleOverlay.Interop;

/// <summary>
/// A still picture of one window, even while it is behind others (Windows draws it for us with PrintWindow).
/// Used as the All Tabs background for the focused window. Safe to call off the UI thread.
/// </summary>
internal static class WindowPicture
{
    private const uint PW_CLIENTONLY = 0x1;
    private const uint PW_RENDERFULLCONTENT = 0x2; // also captures windows drawn with DirectX, like browsers

    /// <summary>
    /// The window's contents, at most <paramref name="maxWidth"/> pixels wide. Null for a minimized window, or one
    /// that comes out black (some games can't be captured this way).
    /// </summary>
    public static BitmapSource? Capture(IntPtr window, int maxWidth)
    {
        try
        {
            if (!Native.IsWindow(window) || Native.IsIconic(window)) return null;
            if (!Native.GetClientRect(window, out var client) || client.Width < 16 || client.Height < 16) return null;

            using var full = new Bitmap(client.Width, client.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                bool ok;
                try
                {
                    ok = PrintWindow(window, hdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
                if (!ok) return null;
            }

            double scale = Math.Min(1.0, (double)maxWidth / client.Width);
            int width = Math.Max(1, (int)(client.Width * scale)), height = Math.Max(1, (int)(client.Height * scale));
            using var small = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, 0, 0, width, height);
            }
            if (MostlyBlack(small)) return null;

            var hBitmap = small.GetHbitmap();
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
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
            Log.Write($"Could not take a picture of a window: {ex.Message}");
            return null;
        }
    }

    /// <summary>A capture that failed often comes back black; a sample of pixels tells.</summary>
    private static bool MostlyBlack(Bitmap bitmap)
    {
        int dark = 0, total = 0;
        for (int y = bitmap.Height / 8; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 8))
        {
            for (int x = bitmap.Width / 8; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 8))
            {
                var c = bitmap.GetPixel(x, y);
                total++;
                if (c.R + c.G + c.B < 24) dark++;
            }
        }
        return total > 0 && dark >= total * 0.95;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);
}
