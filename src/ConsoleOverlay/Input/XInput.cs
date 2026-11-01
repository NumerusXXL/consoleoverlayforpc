using System;
using System.Runtime.InteropServices;

namespace ConsoleOverlay.Input;

/// <summary>Buttons the overlay understands. Triggers are folded in as buttons so they can be bound too.</summary>
[Flags]
public enum ControllerButtons : uint
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,         // "Menu" on Xbox controllers
    Back = 0x0020,          // "View" on Xbox controllers
    LeftStick = 0x0040,
    RightStick = 0x0080,
    LeftShoulder = 0x0100,  // LB
    RightShoulder = 0x0200, // RB
    Guide = 0x0400,         // Xbox button, only readable through XInputGetStateEx
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
    LeftTrigger = 0x10000,
    RightTrigger = 0x20000,
}

/// <summary>Thin wrapper over xinput1_4.dll (ships with Windows 8 and later).</summary>
internal static class XInput
{
    public const int MaxControllers = 4;
    private const uint ErrorSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out State state);

    // Ordinal 100 is the undocumented variant that also reports the Guide button.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint userIndex, out State state);

    private static bool _exAvailable = true;

    public static bool TryGetState(int index, out State state)
    {
        if (_exAvailable)
        {
            try
            {
                return XInputGetStateEx((uint)index, out state) == ErrorSuccess;
            }
            catch (EntryPointNotFoundException)
            {
                _exAvailable = false;
            }
        }
        return XInputGetState((uint)index, out state) == ErrorSuccess;
    }
}
