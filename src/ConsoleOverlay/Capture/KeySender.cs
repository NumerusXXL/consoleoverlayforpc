using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace ConsoleOverlay.Capture;

/// <summary>Presses a keyboard shortcut as if typed (to ask the NVIDIA app or Game Bar to save a clip).</summary>
internal static class KeySender
{
    public const ushort Alt = 0x12, Win = 0x5B, F9 = 0x78, F10 = 0x79, G = 0x47, R = 0x52;

    /// <summary>Holds the keys down in order, then lets go in reverse.</summary>
    public static void Press(params ushort[] keys)
    {
        var inputs = keys.Select(k => Key(k, up: false))
            .Concat(Enumerable.Reverse(keys).Select(k => Key(k, up: true)))
            .ToArray();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            Log.Write($"Sending the shortcut failed (error {Marshal.GetLastWin32Error()}).");
    }

    private static INPUT Key(ushort key, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = key,
                wScan = (ushort)MapVirtualKey(key, 0),
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (key == Win ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // The union must be as big as its largest member (MOUSEINPUT), or SendInput rejects the size.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
