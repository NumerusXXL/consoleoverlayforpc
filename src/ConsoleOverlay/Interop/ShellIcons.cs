using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConsoleOverlay.Interop;

/// <summary>
/// Big, sharp program icons (up to 256 px), the ones Explorer shows in its "Extra large icons" view, instead of the
/// 32 px icon a window hands out. Cached per program, so each one is read from disk once.
/// </summary>
internal static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon of a program or file, <paramref name="size"/> pixels square. Null if Windows has none.</summary>
    public static ImageSource? ForFile(string path, int size = 256)
    {
        string key = $"{size}|{path}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var image = Load(path, size);
        Cache[key] = image;
        return image;
    }

    /// <summary>The program file of a running process, or null (a process running as administrator may not say).</summary>
    public static string? ExePath(uint processId)
    {
        if (processId == 0) return null;
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            int size = name.Capacity;
            return Native.QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static ImageSource? Load(string path, int size)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
                return null;
            try
            {
                // Icon only: never a thumbnail of the file's contents. Scale up small icons rather than fail.
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out bitmap) != 0 || bitmap == IntPtr.Zero)
                    return null;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }

            var image = ToPbgra(bitmap);
            image ??= Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Write($"No big icon for {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
        }
    }

    /// <summary>
    /// CreateBitmapSourceFromHBitmap drops the alpha channel of a 32-bit DIB, which would give icons a black box.
    /// Read the pixels directly instead, as premultiplied BGRA.
    /// </summary>
    private static BitmapSource? ToPbgra(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmBitsPixel != 32 || info.bmBits == IntPtr.Zero)
            return null;
        int stride = info.bmWidthBytes;
        int height = Math.Abs(info.bmHeight);
        var pixels = new byte[stride * height];
        Marshal.Copy(info.bmBits, pixels, 0, pixels.Length);
        var result = BitmapSource.Create(info.bmWidth, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        // DIB sections are stored bottom-up when the height is positive.
        if (info.bmHeight > 0)
        {
            return new TransformedBitmap(result, new ScaleTransform(1, -1));
        }
        return result;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);
}
