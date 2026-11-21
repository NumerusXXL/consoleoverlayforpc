using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// One row of tiles that scrolls sideways, like a console shelf: the focused tile grows and stays near the left with
/// one tile before it. Used by the Steam store and friends; the library has its own copy tuned for covers.
/// </summary>
internal sealed class Shelf
{
    private readonly FrameworkElement _resources;
    private readonly double _width, _height, _focusedWidth, _focusedHeight;
    private const double Gap = 18; // CoverButton's right margin
    private readonly StackPanel _tiles = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly List<Button> _buttons = [];

    public Shelf(FrameworkElement resources, double width, double height, double focusedWidth, double focusedHeight)
    {
        _resources = resources;
        (_width, _height, _focusedWidth, _focusedHeight) = (width, height, focusedWidth, focusedHeight);
        // Padding so the focus ring (drawn just outside a tile) isn't cut off by the scrolling strip.
        _tiles.Margin = new Thickness(10, 10, 10, 12);
        View = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            CanContentScroll = false,
            Content = _tiles,
            Margin = new Thickness(-10, 0, -10, 0),
            Height = focusedHeight + 24,
        };
        KeyboardNavigation.SetDirectionalNavigation(_tiles, KeyboardNavigationMode.Contained);
    }

    public ScrollViewer View { get; }

    public int Count => _buttons.Count;

    public IReadOnlyList<Button> Tiles => _buttons;

    public void Clear()
    {
        _tiles.Children.Clear();
        _buttons.Clear();
        View.ScrollToHorizontalOffset(0);
    }

    /// <summary>Adds a tile. <paramref name="focused"/> runs when it gets focus, with its index.</summary>
    public Button Add(UIElement content, Action<int> focused, Action pressed)
    {
        var button = new Button
        {
            Style = (Style)_resources.FindResource("CoverButton"),
            Content = content,
            Width = _width,
            Height = _height,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        button.Click += (_, _) => pressed();
        button.GotKeyboardFocus += (_, _) =>
        {
            int index = _buttons.IndexOf(button);
            View.ScrollToHorizontalOffset(Math.Max(0, (index - 1) * (_width + Gap)));
            Grow(button, true);
            focused(index);
        };
        button.LostKeyboardFocus += (_, _) => Grow(button, false);
        _buttons.Add(button);
        _tiles.Children.Add(button);
        return button;
    }

    public void Rise() => Ui.Stagger(_buttons.Take(8).Cast<FrameworkElement>(), 30);

    public bool Contains(IInputElement? element) => element is Button b && _buttons.Contains(b);

    public int IndexOf(IInputElement? element) => element is Button b ? _buttons.IndexOf(b) : -1;

    public void Focus(int index)
    {
        if (_buttons.Count == 0) return;
        var button = _buttons[Math.Clamp(index, 0, _buttons.Count - 1)];
        button.Focus();
        Keyboard.Focus(button);
    }

    private void Grow(Button button, bool focused)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(focused ? 260 : 200));
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        button.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(focused ? _focusedWidth : _width, duration) { EasingFunction = ease });
        button.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(focused ? _focusedHeight : _height, duration) { EasingFunction = ease });
    }
}
