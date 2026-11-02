using System;
using System.Diagnostics;
using System.Windows.Media;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Finds stutter for the log. For a few seconds after each page change or button press it watches every
/// frame WPF draws and writes down slow ones: how long the worst frame took and how many were late.
/// It only listens while it measures, because listening keeps WPF drawing every frame.
/// </summary>
internal sealed class FrameMonitor
{
    private const double WatchSeconds = 2.5;
    private readonly Stopwatch _watch = new();
    private TimeSpan _lastFrame;
    private string _label = "";
    private int _frames;
    private int _late;
    private double _worst;
    private bool _listening;

    public void Start(string label)
    {
        if (_listening && label == _label)
        {
            _watch.Restart(); // keep measuring the same thing a bit longer
            return;
        }
        Report();
        _label = label;
        _frames = 0;
        _late = 0;
        _worst = 0;
        _lastFrame = TimeSpan.Zero;
        _watch.Restart();
        if (!_listening)
        {
            CompositionTarget.Rendering += OnRendering;
            _listening = true;
        }
    }

    public void Stop()
    {
        Report();
        if (_listening)
        {
            CompositionTarget.Rendering -= OnRendering;
            _listening = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var time = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if (time == _lastFrame) return; // WPF can raise this twice for one frame
        if (_lastFrame != TimeSpan.Zero)
        {
            double gap = (time - _lastFrame).TotalMilliseconds;
            _frames++;
            if (gap > 34) _late++;
            _worst = Math.Max(_worst, gap);
        }
        _lastFrame = time;

        if (_watch.Elapsed.TotalSeconds > WatchSeconds)
            Stop();
    }

    private void Report()
    {
        if (_frames == 0) return;
        double seconds = Math.Max(_watch.Elapsed.TotalSeconds, 0.001);
        if (_worst > 40 || _late > 2)
            Log.Write($"Stutter on {_label}: {_frames} frames in {seconds:0.0} s, {_late} late, worst {_worst:0} ms.");
        _frames = 0;
    }
}
