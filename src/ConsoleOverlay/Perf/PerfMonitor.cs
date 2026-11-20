using System;
using System.Collections.Generic;
using System.Windows.Threading;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Perf;

/// <summary>A second's reading: the PC's load plus the game's frame rate (null when it can't be counted).</summary>
internal sealed record PerfSnapshot(StatsSample Stats, FrameStats? Frames, string? GameName);

/// <summary>
/// Reads the stats once a second for whoever is watching (the Performance page, the stats in the corner) and keeps
/// the last 60 seconds of frame rate for the graph. Runs only while someone watches, so it costs nothing otherwise.
/// The game is the window the overlay opened over, or, while the overlay is closed, the window in front.
/// </summary>
internal sealed class PerfMonitor
{
    public static PerfMonitor Instance { get; } = new();

    private readonly HashSet<object> _watchers = [];
    private readonly DispatcherTimer _timer;
    private SystemStats? _stats;
    private FrameCounter? _frames;
    private IntPtr _gameWindow;
    private int _gamePid;
    private string? _gameName;

    public event Action<PerfSnapshot>? Updated;

    public PerfSnapshot? Last { get; private set; }

    /// <summary>The last 60 frame rates, oldest first.</summary>
    public Queue<int> History { get; } = new();

    public string? FrameProblem => _frames?.Problem;

    public bool NvidiaGpu => _stats?.NvidiaGpu ?? Nvml.Init();

    private PerfMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Watch(object watcher)
    {
        if (!_watchers.Add(watcher) || _watchers.Count > 1) return;
        _stats ??= new SystemStats();
        _frames ??= new FrameCounter();
        _frames.Start();
        _stats.Read(); // primes the CPU counters, so the first real reading is right
        _timer.Start();
    }

    public void Unwatch(object watcher)
    {
        if (!_watchers.Remove(watcher) || _watchers.Count > 0) return;
        _timer.Stop();
        _frames?.Stop();
        History.Clear();
    }

    /// <summary>Retry the frame counter (after the app restarted with more rights, or the user was added to the group).</summary>
    public void RetryFrames()
    {
        if (_frames is null || _frames.Running) return;
        _frames.Start();
    }

    /// <summary>The overlay opened over this window: count its frames. Zero goes back to following the front window.</summary>
    public void SetGameWindow(IntPtr window) => _gameWindow = window;

    private void Tick()
    {
        if (_stats is null) return;
        UpdateGame();
        var frames = _frames?.Read(_gamePid);
        var snapshot = new PerfSnapshot(_stats.Read(), frames, _gameName);
        Last = snapshot;
        History.Enqueue(frames?.Fps ?? 0);
        while (History.Count > 60) History.Dequeue();
        Updated?.Invoke(snapshot);
    }

    private void UpdateGame()
    {
        var window = _gameWindow != IntPtr.Zero ? _gameWindow : Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(window, out uint pid);
        if ((int)pid == _gamePid) return;
        _gamePid = (int)pid;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(_gamePid);
            _gameName = string.IsNullOrWhiteSpace(process.MainWindowTitle) ? process.ProcessName : process.MainWindowTitle;
        }
        catch
        {
            _gameName = null;
        }
    }
}
