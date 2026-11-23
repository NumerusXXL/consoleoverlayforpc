using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// A frame in the overlay's page that holds another app's window (Steam's chat) as a child window. It moves and sizes
/// with the page's layout, clips the window to its rounded corners, and hands the window back before it goes away.
/// </summary>
internal sealed class ChatHost : HwndHost
{
    /// <summary>The frame's corner radius, in WPF units (scaled to pixels like the rest of the page).</summary>
    public double CornerRadius { get; init; } = 18;

    /// <summary>The frame's own window (the parent of the Steam window); zero until it is shown.</summary>
    public IntPtr Container { get; private set; }

    /// <summary>The frame moved or changed size (in pixels).</summary>
    public event Action? Resized;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        int style = unchecked((int)(Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS));
        Container = Native.CreateWindowEx(0, "Static", "", style, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, Container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Never take Steam's window down with the frame: give back anything still inside first.
        if (SteamChatWindow.Active is { } chat && Native.GetAncestor(chat.Hwnd, 1 /* GA_PARENT */) == hwnd.Handle) chat.Release();
        for (var child = Native.GetWindow(hwnd.Handle, Native.GW_CHILD); child != IntPtr.Zero; child = Native.GetWindow(hwnd.Handle, Native.GW_CHILD))
            Native.SetParent(child, IntPtr.Zero);
        Native.DestroyWindow(hwnd.Handle);
        Container = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (Container == IntPtr.Zero) return;
        // Rounded like the page's panels. The region is in the frame's own pixels; Windows owns it after this call.
        int width = (int)Math.Round(rcBoundingBox.Width), height = (int)Math.Round(rcBoundingBox.Height);
        double scale = ActualWidth > 0 ? rcBoundingBox.Width / ActualWidth : 1;
        int radius = (int)Math.Round(CornerRadius * scale);
        var region = Native.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region != IntPtr.Zero && Native.SetWindowRgn(Container, region, true) == 0) Native.DeleteObject(region);
        Resized?.Invoke();
    }
}
