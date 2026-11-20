using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ConsoleOverlay.Interop;
using ConsoleOverlay.Perf;

namespace ConsoleOverlay.Capture;

/// <summary>A screenshot or clip on disk, newest first in the Recent row.</summary>
internal sealed record CaptureFile(string Path, bool IsVideo, DateTime Taken)
{
    public BitmapSource? Thumbnail { get; set; }

    public TimeSpan? Duration { get; set; }
}

/// <summary>
/// Screenshots, clips and recording. Screenshots are taken by Console Overlay itself (a PNG of the screen the game is
/// on, in Videos\Captures). Clips and recording use a recorder that is already on the PC: the NVIDIA app (its
/// Instant Replay and Record, through its shortcuts) or Xbox Game Bar (Record that, Record). Both keep the last
/// seconds of gameplay in the background far more cheaply than an app like this could.
/// </summary>
internal static class CaptureService
{
    public const string Nvidia = "NVIDIA";
    public const string GameBar = "GameBar";

    private static readonly string[] VideoTypes = [".mp4", ".mkv", ".mov", ".webm"];
    private static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".bmp"];

    public static string CapturesFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");

    public static bool NvidiaGpu => Nvml.Init();

    /// <summary>The NVIDIA app's in-game overlay is running, so its shortcuts will do something.</summary>
    public static bool NvidiaOverlayRunning
    {
        get
        {
            try
            {
                var processes = Process.GetProcessesByName("NVIDIA Overlay");
                bool running = processes.Length > 0;
                foreach (var p in processes) p.Dispose();
                return running;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>The recorder to use: NVIDIA when chosen and there is an NVIDIA GPU, otherwise Game Bar.</summary>
    public static string Effective(string chosen) => chosen == Nvidia && NvidiaGpu ? Nvidia : GameBar;

    public static string Name(string recorder) => recorder == Nvidia ? "NVIDIA app" : "Xbox Game Bar";

    // ---- Doing --------------------------------------------------------------------------------

    /// <summary>Saves a PNG of the monitor the game is on. Returns its path, or null if it failed.</summary>
    public static string? Screenshot(IntPtr gameWindow, string? gameName)
    {
        try
        {
            var monitor = Native.MonitorFromWindow(gameWindow, Native.MONITOR_DEFAULTTONEAREST);
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info)) return null;
            var area = info.rcMonitor;

            using var bitmap = new Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bitmap))
                g.CopyFromScreen(area.Left, area.Top, 0, 0, new Size(area.Width, area.Height), CopyPixelOperation.SourceCopy);

            Directory.CreateDirectory(CapturesFolder);
            string name = $"{SafeName(gameName) ?? "Screenshot"} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";
            string path = System.IO.Path.Combine(CapturesFolder, name);
            bitmap.Save(path, ImageFormat.Png);
            Log.Write($"Screenshot saved: {path}");
            return path;
        }
        catch (Exception ex)
        {
            Log.Write($"Screenshot failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Asks the recorder to save the last seconds it has kept.</summary>
    public static void SaveReplay(string recorder)
    {
        Log.Write($"Asking {Name(recorder)} to save a clip.");
        if (recorder == Nvidia) KeySender.Press(KeySender.Alt, KeySender.F10);
        else KeySender.Press(KeySender.Win, KeySender.Alt, KeySender.G);
    }

    /// <summary>Starts or stops a recording.</summary>
    public static void ToggleRecording(string recorder)
    {
        Log.Write($"Asking {Name(recorder)} to start or stop recording.");
        if (recorder == Nvidia) KeySender.Press(KeySender.Alt, KeySender.F9);
        else KeySender.Press(KeySender.Win, KeySender.Alt, KeySender.R);
    }

    // ---- Recent -------------------------------------------------------------------------------

    /// <summary>
    /// The newest captures from Videos\Captures (ours and Game Bar's), the NVIDIA app's game folders in Videos, and
    /// Pictures\Screenshots. Runs off the UI thread; only looks one folder deep.
    /// </summary>
    public static Task<List<CaptureFile>> RecentAsync(int count) => Task.Run(() =>
    {
        var found = new List<CaptureFile>();
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var folders = new List<string> { CapturesFolder, System.IO.Path.Combine(pictures, "Screenshots"), videos };
        try
        {
            if (Directory.Exists(videos))
                folders.AddRange(Directory.EnumerateDirectories(videos).Take(60));
        }
        catch { }

        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
                {
                    string ext = file.Extension.ToLowerInvariant();
                    bool video = VideoTypes.Contains(ext);
                    if (!video && !ImageTypes.Contains(ext)) continue;
                    found.Add(new CaptureFile(file.FullName, video, file.LastWriteTime));
                }
            }
            catch { }
        }
        return found.OrderByDescending(f => f.Taken).Take(count).ToList();
    });

    /// <summary>Fills in each capture's thumbnail (and a clip's length) from Windows' thumbnail cache.</summary>
    public static async Task LoadThumbnailsAsync(IEnumerable<CaptureFile> files)
    {
        foreach (var file in files)
        {
            try
            {
                var item = await Windows.Storage.StorageFile.GetFileFromPathAsync(file.Path);
                using var thumbnail = await item.GetThumbnailAsync(
                    file.IsVideo ? Windows.Storage.FileProperties.ThumbnailMode.VideosView : Windows.Storage.FileProperties.ThumbnailMode.PicturesView,
                    320, Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
                if (thumbnail is not null)
                {
                    using var source = thumbnail.AsStreamForRead();
                    var memory = new MemoryStream();
                    await source.CopyToAsync(memory);
                    memory.Position = 0;
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = memory;
                    image.EndInit();
                    image.Freeze();
                    file.Thumbnail = image;
                }
                if (file.IsVideo)
                {
                    var properties = await item.Properties.GetVideoPropertiesAsync();
                    if (properties.Duration > TimeSpan.Zero) file.Duration = properties.Duration;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"No thumbnail for {file.Path}: {ex.Message}");
            }
        }
    }

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open {path}: {ex.Message}");
        }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(CapturesFolder);
            Process.Start(new ProcessStartInfo(CapturesFolder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open the captures folder: {ex.Message}");
        }
    }

    private static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (clean.Length > 60) clean = clean[..60].Trim();
        return clean.Length > 0 ? clean : null;
    }
}
