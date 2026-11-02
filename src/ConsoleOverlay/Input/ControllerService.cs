using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace ConsoleOverlay.Input;

public enum NavCommand
{
    Up,
    Down,
    Left,
    Right,
    Accept,
    Back,
    PreviousTab,
    NextTab,
    Mute,        // Y
    Secondary,   // X: close a window on All Tabs, mute your mic on Discord
    ColumnLeft,  // LT or left stick click
    ColumnRight, // RT or right stick click
}

/// <summary>
/// Polls XInput controllers on a background thread. It always watches for the "open overlay"
/// combo, and only produces navigation commands while the overlay is visible.
/// Polling is slow while the overlay is hidden so it costs next to nothing during games.
/// </summary>
public sealed class ControllerService : IDisposable
{
    private const int HiddenPollMs = 33;          // ~30 Hz is plenty to catch a button combo
    private const int VisiblePollMs = 8;          // ~120 Hz for snappy navigation
    private const int IdlePollMs = 500;           // nobody plugged in
    private const long DisconnectedProbeMs = 2000; // probing an empty slot is slow, so do it rarely
    private const int RepeatDelayMs = 400;
    private const int RepeatIntervalMs = 110;
    private const short StickThreshold = 16000;
    private const byte TriggerThreshold = 128;

    private const ControllerButtons Directions =
        ControllerButtons.DPadUp | ControllerButtons.DPadDown | ControllerButtons.DPadLeft | ControllerButtons.DPadRight;

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly bool[] _connected = new bool[XInput.MaxControllers];
    private readonly long[] _lastProbe = new long[XInput.MaxControllers];
    private readonly ControllerButtons[] _previous = new ControllerButtons[XInput.MaxControllers];
    private volatile bool _running = true;
    private volatile bool _navigationEnabled;
    private volatile uint _combo;

    private ControllerButtons _heldDirection;
    private long _nextRepeatAt;

    private const long CaptureHoldMs = 1000;
    private const long CaptureTimeoutMs = 10000;

    /// <summary>A combo being recorded on the Settings page. Only touched on the poll thread after it is set.</summary>
    private sealed class Capture
    {
        public required Action<ControllerButtons> Progress { get; init; }
        public required Action<ControllerButtons?> Done { get; init; }
        public bool Armed;           // everything was let go once, so the A press that started this doesn't count
        public ControllerButtons Held;
        public long HeldSince = -1;
        public long LastChange = -1;
        public volatile bool Cancelled;
    }

    private volatile Capture? _capture;

    public event Action? ComboPressed;
    public event Action<NavCommand>? Navigate;

    public ControllerService(Dispatcher dispatcher, ControllerButtons combo)
    {
        _dispatcher = dispatcher;
        Combo = combo;
        Array.Fill(_lastProbe, -DisconnectedProbeMs); // probe every slot on the first pass
        _thread = new Thread(Run) { IsBackground = true, Name = "Controller poll", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public ControllerButtons Combo
    {
        get => (ControllerButtons)_combo;
        set => _combo = (uint)value;
    }

    /// <summary>True while the overlay is open; turns on navigation and fast polling.</summary>
    public bool NavigationEnabled
    {
        get => _navigationEnabled;
        set
        {
            _navigationEnabled = value;
            _heldDirection = ControllerButtons.None;
        }
    }

    /// <summary>
    /// Records a new combo: waits until every button is let go, then for buttons held together for a second.
    /// Navigation and the open combo are off meanwhile. Gives null after 10 seconds without one, or when cancelled.
    /// Both callbacks run on the UI thread.
    /// </summary>
    public void BeginCapture(Action<ControllerButtons> progress, Action<ControllerButtons?> done)
    {
        CancelCapture();
        _capture = new Capture { Progress = progress, Done = done };
    }

    public void CancelCapture()
    {
        if (_capture is { } capture)
            capture.Cancelled = true;
    }

    private void HandleCapture(Capture capture, ControllerButtons held, long now)
    {
        if (capture.LastChange < 0)
            capture.LastChange = now;

        void Finish(ControllerButtons? result)
        {
            if (_capture == capture)
                _capture = null;
            Raise(() => capture.Done(result));
        }

        if (capture.Cancelled)
        {
            Finish(null);
            return;
        }

        if (!capture.Armed)
        {
            if (held == ControllerButtons.None)
            {
                capture.Armed = true;
                capture.LastChange = now;
            }
            else if (now - capture.LastChange > CaptureTimeoutMs)
                Finish(null);
            return;
        }

        if (held != capture.Held)
        {
            capture.Held = held;
            capture.HeldSince = now;
            capture.LastChange = now;
            Raise(() => capture.Progress(held));
            return;
        }

        if (held != ControllerButtons.None && now - capture.HeldSince >= CaptureHoldMs)
            Finish(held);
        else if (held == ControllerButtons.None && now - capture.LastChange > CaptureTimeoutMs)
            Finish(null);
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        while (_running)
        {
            long now = clock.ElapsedMilliseconds;
            var combined = ControllerButtons.None;
            var pressedNow = ControllerButtons.None;
            bool anyConnected = false;

            for (int i = 0; i < XInput.MaxControllers; i++)
            {
                if (!_connected[i] && now - _lastProbe[i] < DisconnectedProbeMs)
                    continue;

                _lastProbe[i] = now;
                if (!XInput.TryGetState(i, out var state))
                {
                    _connected[i] = false;
                    _previous[i] = ControllerButtons.None;
                    continue;
                }

                _connected[i] = true;
                anyConnected = true;
                var buttons = ToButtons(state.Gamepad);
                var previous = _previous[i];
                _previous[i] = buttons;

                var combo = Combo;
                if (_capture is null && combo != ControllerButtons.None && (buttons & combo) == combo && (previous & combo) != combo)
                    Raise(() => ComboPressed?.Invoke());

                combined |= buttons;
                pressedNow |= buttons & ~previous;
            }

            var capturing = _capture;
            if (capturing is not null)
            {
                HandleCapture(capturing, combined, now);
                _heldDirection = ControllerButtons.None;
            }
            else if (_navigationEnabled)
                HandleNavigation(combined, pressedNow, now);

            // While recording a combo, keep polling even with no controller so the timeout and Esc still work.
            int sleep = capturing is not null ? VisiblePollMs
                : !anyConnected ? IdlePollMs
                : _navigationEnabled ? VisiblePollMs
                : HiddenPollMs;
            Thread.Sleep(sleep);
        }
    }

    private void HandleNavigation(ControllerButtons held, ControllerButtons pressed, long now)
    {
        // Ignore buttons that are part of the open combo so opening the overlay doesn't also click something.
        var combo = Combo;
        if ((held & combo) == combo && combo != ControllerButtons.None)
            pressed &= ~combo;

        if (pressed.HasFlag(ControllerButtons.A)) Raise(NavCommand.Accept);
        if (pressed.HasFlag(ControllerButtons.B)) Raise(NavCommand.Back);
        if (pressed.HasFlag(ControllerButtons.LeftShoulder)) Raise(NavCommand.PreviousTab);
        if (pressed.HasFlag(ControllerButtons.RightShoulder)) Raise(NavCommand.NextTab);
        if (pressed.HasFlag(ControllerButtons.Y)) Raise(NavCommand.Mute);
        if (pressed.HasFlag(ControllerButtons.X)) Raise(NavCommand.Secondary);
        if ((pressed & (ControllerButtons.LeftTrigger | ControllerButtons.LeftStick)) != 0) Raise(NavCommand.ColumnLeft);
        if ((pressed & (ControllerButtons.RightTrigger | ControllerButtons.RightStick)) != 0) Raise(NavCommand.ColumnRight);

        // Directions repeat while held, like a console menu.
        var direction = PickDirection(held & Directions);
        if (direction == ControllerButtons.None)
        {
            _heldDirection = ControllerButtons.None;
            return;
        }

        if (direction != _heldDirection)
        {
            _heldDirection = direction;
            _nextRepeatAt = now + RepeatDelayMs;
            Raise(ToCommand(direction));
        }
        else if (now >= _nextRepeatAt)
        {
            _nextRepeatAt = now + RepeatIntervalMs;
            Raise(ToCommand(direction));
        }
    }

    private static ControllerButtons PickDirection(ControllerButtons d)
    {
        // Diagonals are ambiguous in a menu; prefer vertical movement.
        if (d.HasFlag(ControllerButtons.DPadUp)) return ControllerButtons.DPadUp;
        if (d.HasFlag(ControllerButtons.DPadDown)) return ControllerButtons.DPadDown;
        if (d.HasFlag(ControllerButtons.DPadLeft)) return ControllerButtons.DPadLeft;
        if (d.HasFlag(ControllerButtons.DPadRight)) return ControllerButtons.DPadRight;
        return ControllerButtons.None;
    }

    private static NavCommand ToCommand(ControllerButtons direction) => direction switch
    {
        ControllerButtons.DPadUp => NavCommand.Up,
        ControllerButtons.DPadDown => NavCommand.Down,
        ControllerButtons.DPadLeft => NavCommand.Left,
        _ => NavCommand.Right,
    };

    private static ControllerButtons ToButtons(XInput.Gamepad pad)
    {
        var b = (ControllerButtons)pad.Buttons;
        if (pad.LeftTrigger > TriggerThreshold) b |= ControllerButtons.LeftTrigger;
        if (pad.RightTrigger > TriggerThreshold) b |= ControllerButtons.RightTrigger;

        // The left stick acts as a D-pad.
        if (pad.ThumbLY > StickThreshold) b |= ControllerButtons.DPadUp;
        if (pad.ThumbLY < -StickThreshold) b |= ControllerButtons.DPadDown;
        if (pad.ThumbLX < -StickThreshold) b |= ControllerButtons.DPadLeft;
        if (pad.ThumbLX > StickThreshold) b |= ControllerButtons.DPadRight;
        return b;
    }

    private void Raise(NavCommand command) => Raise(() => Navigate?.Invoke(command));

    private void Raise(Action action) => _dispatcher.BeginInvoke(action);

    public void Dispose() => _running = false;
}
