using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Button prompts drawn the way Steam's Big Picture shows them: flat, light glyphs with the label in dark ink. Face
/// buttons are discs, bumpers are rounded bars, triggers are rounded on top, and keyboard keys are rounded key caps.
/// Plain shapes, so they cost nothing to draw.
/// </summary>
internal static class ButtonGlyphs
{
    private static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEB)));
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1F)));
    private static readonly Brush DarkFill = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1F)));
    private static readonly Brush LightInk = Frozen(new SolidColorBrush(Colors.White));

    /// <summary>
    /// A prompt for a controller button ("A", "LB", "RT") or a keyboard key ("Enter", "Q", "←").
    /// <paramref name="dark"/> draws it dark with a white label, for use on a white pill.
    /// </summary>
    public static FrameworkElement Create(string button, double size = 24, bool dark = false)
    {
        var fill = dark ? DarkFill : Fill;
        var ink = dark ? LightInk : Ink;
        return button switch
        {
            "A" or "B" or "X" or "Y" => Disc(button, size, fill, ink),
            "LB" or "RB" => Bar(button, size, new CornerRadius(size * 0.3), fill, ink),
            "LT" or "RT" => Bar(button, size, new CornerRadius(size * 0.5, size * 0.5, size * 0.2, size * 0.2), fill, ink),
            _ => Bar(button, size, new CornerRadius(size * 0.22), fill, ink),
        };
    }

    private static FrameworkElement Disc(string letter, double size, Brush fill, Brush ink)
    {
        var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Ellipse { Fill = fill });
        grid.Children.Add(Label(letter, size, FontWeights.Black, 0.56, ink));
        return grid;
    }

    private static FrameworkElement Bar(string text, double size, CornerRadius corners, Brush fill, Brush ink) => new Border
    {
        MinWidth = size,
        Height = size,
        CornerRadius = corners,
        Background = fill,
        Padding = new Thickness(text.Length > 1 ? size * 0.3 : 0, 0, text.Length > 1 ? size * 0.3 : 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Label(text, size, FontWeights.Bold, text.Length > 2 ? 0.44 : 0.5, ink),
    };

    private static TextBlock Label(string text, double size, FontWeight weight, double scale, Brush ink) => new()
    {
        Text = text,
        FontSize = Math.Round(size * scale),
        FontWeight = weight,
        Foreground = ink,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
