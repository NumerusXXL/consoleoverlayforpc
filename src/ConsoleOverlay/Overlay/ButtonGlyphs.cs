using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Button prompts drawn like the real thing, the way Steam and Xbox show them: face buttons as glossy dark domes with
/// a colored letter, bumpers and triggers as dark rounded caps, and keyboard keys as light key caps with a visible
/// side. All plain shapes and gradients (no effects), so they cost nothing to draw.
/// </summary>
internal static class ButtonGlyphs
{
    private static readonly Color Green = Color.FromRgb(0x5D, 0xD3, 0x5A);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x5F, 0x5F);
    private static readonly Color Blue = Color.FromRgb(0x4F, 0xA3, 0xFF);
    private static readonly Color Yellow = Color.FromRgb(0xFF, 0xCF, 0x40);

    private static readonly Brush DomeFill = Frozen(new LinearGradientBrush(
        [new GradientStop(Color.FromRgb(0x4A, 0x50, 0x5C), 0), new GradientStop(Color.FromRgb(0x23, 0x26, 0x2D), 0.55), new GradientStop(Color.FromRgb(0x11, 0x13, 0x17), 1)],
        new Point(0.5, 0), new Point(0.5, 1)));
    private static readonly Brush DomeEdge = Frozen(new LinearGradientBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 90));
    private static readonly Brush Gloss = Frozen(new LinearGradientBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90));
    private static readonly Brush Shadow = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00)));

    private static readonly Brush KeyFace = Frozen(new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xD9, 0xDD, 0xE4), 90));
    private static readonly Brush KeySide = Frozen(new SolidColorBrush(Color.FromRgb(0x8C, 0x93, 0xA1)));
    private static readonly Brush KeyEdge = Frozen(new SolidColorBrush(Color.FromRgb(0xB9, 0xBF, 0xCA)));
    private static readonly Brush KeyInk = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x25)));

    /// <summary>A prompt for a controller button ("A", "LB", "RT") or a keyboard key ("Enter", "Q", "←").</summary>
    public static FrameworkElement Create(string button, double size = 24) => button switch
    {
        "A" => Face("A", Green, size),
        "B" => Face("B", Red, size),
        "X" => Face("X", Blue, size),
        "Y" => Face("Y", Yellow, size),
        "LB" or "RB" or "LT" or "RT" => Cap(button, size),
        _ => Key(button, size),
    };

    /// <summary>A round face button: a dark dome with a lit top edge, a gloss highlight and a colored letter.</summary>
    private static FrameworkElement Face(string letter, Color color, double size)
    {
        var grid = new Grid { Width = size, Height = size + 2, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Ellipse { Width = size, Height = size, Fill = Shadow, VerticalAlignment = VerticalAlignment.Bottom });
        grid.Children.Add(new Ellipse { Width = size, Height = size, Fill = DomeFill, Stroke = DomeEdge, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Top });
        grid.Children.Add(new Ellipse
        {
            Width = size * 0.68,
            Height = size * 0.38,
            Fill = Gloss,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, size * 0.08, 0, 0),
        });
        grid.Children.Add(new TextBlock
        {
            Text = letter,
            FontSize = Math.Round(size * 0.55),
            FontWeight = FontWeights.Black,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Height = size,
            Padding = new Thickness(0, size * 0.17, 0, 0),
        });
        return grid;
    }

    /// <summary>A bumper or trigger: a dark cap with a lit top edge and a darker side under it.</summary>
    private static FrameworkElement Cap(string name, double size)
    {
        bool trigger = name.EndsWith('T');
        double radius = size * 0.32;
        var corners = trigger
            ? new CornerRadius(radius * 1.6, radius * 1.6, radius, radius) // triggers are rounder on top
            : new CornerRadius(radius);
        return Stacked(name, size, corners,
            face: DomeFill, edge: DomeEdge, side: Frozen(new SolidColorBrush(Color.FromRgb(0x06, 0x07, 0x09))), ink: Brushes.White);
    }

    /// <summary>A keyboard key: a light key cap with its side showing underneath, like a real key.</summary>
    private static FrameworkElement Key(string name, double size) =>
        Stacked(name, size, new CornerRadius(size * 0.25), face: KeyFace, edge: KeyEdge, side: KeySide, ink: KeyInk);

    private static FrameworkElement Stacked(string text, double size, CornerRadius corners, Brush face, Brush edge, Brush side, Brush ink)
    {
        double depth = Math.Max(2, Math.Round(size * 0.12));
        var grid = new Grid { MinWidth = size, Height = size + depth, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Border { CornerRadius = corners, Background = side, Margin = new Thickness(0, depth, 0, 0) });
        grid.Children.Add(new Border
        {
            CornerRadius = corners,
            Background = face,
            BorderBrush = edge,
            BorderThickness = new Thickness(1),
            Height = size,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(text.Length > 1 ? size * 0.3 : 0, 0, text.Length > 1 ? size * 0.3 : 0, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = Math.Round(size * (text.Length > 2 ? 0.44 : 0.5)),
                FontWeight = FontWeights.Bold,
                Foreground = ink,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        return grid;
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
