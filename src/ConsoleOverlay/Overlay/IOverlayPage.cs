using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ConsoleOverlay.Input;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// A page with its own behavior (All Tabs, Sound, Discord). The overlay shows its content, activates it while it is
/// on screen, and offers it each controller command and key first.
/// </summary>
internal interface IOverlayPage
{
    Panel Content { get; }

    /// <summary>The button hints shown under the page.</summary>
    string Hint { get; }

    /// <summary>Raised when <see cref="Hint"/> changes while the page is shown.</summary>
    event Action? HintChanged;

    /// <summary>The page came into view. <paramref name="gameWindow"/> is the window the overlay opened over.</summary>
    void Activate(IntPtr gameWindow);

    /// <summary>The page left the screen (another page, or the overlay closed). Let go of anything expensive.</summary>
    void Deactivate();

    /// <summary>The control to focus when the page opens.</summary>
    UIElement? FirstFocus();

    /// <summary>Handles a controller command. Returns false to let the overlay do its default.</summary>
    bool Handle(NavCommand command, IInputElement? focused);

    /// <summary>Handles a key. Returns false to let the overlay do its default.</summary>
    bool HandleKey(Key key, IInputElement? focused);

    /// <summary>
    /// Sees every key first, with modifiers, before it becomes a command (used to record a new shortcut).
    /// Return true to swallow it.
    /// </summary>
    bool HandleRawKey(KeyEventArgs e) => false;
}
