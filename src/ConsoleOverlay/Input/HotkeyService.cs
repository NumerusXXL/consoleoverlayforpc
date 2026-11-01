using System;
using System.Windows.Input;
using System.Windows.Interop;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Input;

/// <summary>Registers one system-wide keyboard shortcut and raises <see cref="Pressed"/> when it fires.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4F56; // "OV"
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService()
    {
        // A message-only window: invisible, never activated, only receives WM_HOTKEY.
        _source = new HwndSource(new HwndSourceParameters("ConsoleOverlayHotkey")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a shortcut such as "Ctrl+Alt+G". Returns false if it can't be parsed or is taken.</summary>
    public bool Register(string shortcut, out string? error)
    {
        Unregister();
        if (!TryParse(shortcut, out uint modifiers, out uint vk, out error))
            return false;

        _registered = Native.RegisterHotKey(_source.Handle, HotkeyId, modifiers | Native.MOD_NOREPEAT, vk);
        if (!_registered)
            error = $"\"{shortcut}\" is already used by another app.";
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            Native.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    public static bool TryParse(string shortcut, out uint modifiers, out uint vk, out string? error)
    {
        modifiers = 0;
        vk = 0;
        error = null;
        Key? key = null;

        foreach (var raw in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win" or "windows": modifiers |= Native.MOD_WIN; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse(name, ignoreCase: true, out Key parsed) || parsed == Key.None)
                    {
                        error = $"Unknown key \"{raw}\" in shortcut \"{shortcut}\".";
                        return false;
                    }
                    key = parsed;
                    break;
            }
        }

        if (key is null)
        {
            error = $"Shortcut \"{shortcut}\" has no main key.";
            return false;
        }

        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
