using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ConsoleOverlay.Steam;

/// <summary>
/// Downloads pictures (store capsules, avatars) and decodes them at the size they are drawn, off the UI thread.
/// A few dozen stay in memory while a page is open; <see cref="Clear"/> lets go of them.
/// </summary>
internal static class WebImages
{
    private const int MaxCached = 80;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, BitmapSource> Cache = [];
    private static readonly LinkedList<string> Order = new();

    public static async Task<BitmapSource?> LoadAsync(string url, int decodeWidth, CancellationToken cancel)
    {
        string key = url + "|" + decodeWidth;
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, cancel);
            var image = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                bitmap.Freeze();
                return (BitmapSource)bitmap;
            }, cancel);
            lock (Cache)
            {
                Cache[key] = image;
                Order.AddLast(key);
                while (Order.Count > MaxCached)
                {
                    Cache.Remove(Order.First!.Value);
                    Order.RemoveFirst();
                }
            }
            return image;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not load a picture ({new Uri(url).Host}): {ex.Message}");
            return null;
        }
    }

    public static void Clear()
    {
        lock (Cache)
        {
            Cache.Clear();
            Order.Clear();
        }
    }
}
