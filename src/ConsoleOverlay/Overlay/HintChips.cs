using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Turns a hint like "A select · B back · LB / RB other pages" into console-style prompts: a green A, a red B,
/// a blue X, a yellow Y, grey pills for the bumpers and triggers, and key caps for the keyboard.
/// </summary>
internal static class HintChips
{
    // Longest first, so "LB / RB" wins over "B".
    private static readonly string[] Prefixes =
    [
        "LB / RB", "LT / RT", "Q / E", "Ctrl+V", "← →", "Enter", "Esc", "Tab", "Delete", "A", "B", "X", "Y",
    ];

    private static readonly Color Green = Color.FromRgb(0x4C, 0xB8, 0x4A);
    private static readonly Color Red = Color.FromRgb(0xE2, 0x4B, 0x4B);
    private static readonly Color Blue = Color.FromRgb(0x3E, 0x8E, 0xF0);
    private static readonly Color Yellow = Color.FromRgb(0xF2, 0xC1, 0x3A);

    public static void Fill(WrapPanel panel, FrameworkElement resources, string hint)
    {
        panel.Children.Clear();
        var labelBrush = (Brush)resources.FindResource("SoftTextBrush");

        foreach (var part in hint.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 22, 4) };
            string label = part;
            var prefix = Prefixes.FirstOrDefault(p => part.StartsWith(p + " ", StringComparison.Ordinal));
            if (prefix is not null)
            {
                foreach (var button in prefix.Split(prefix == "← →" ? " " : " / ", StringSplitOptions.RemoveEmptyEntries))
                    item.Children.Add(Chip(button.Trim()));
                label = part[(prefix.Length + 1)..];
            }
            item.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 14,
                Foreground = labelBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(prefix is null ? 0 : 4, 0, 0, 0),
            });
            panel.Children.Add(item);
        }
    }

    private static FrameworkElement Chip(string button)
    {
        (Color? fill, Color ink, bool round) = button switch
        {
            "A" => ((Color?)Green, Colors.White, true),
            "B" => ((Color?)Red, Colors.White, true),
            "X" => ((Color?)Blue, Colors.White, true),
            "Y" => ((Color?)Yellow, Color.FromRgb(0x1A, 0x1A, 0x1A), true),
            "LB" or "RB" or "LT" or "RT" => ((Color?)Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Colors.White, false),
            _ => ((Color?)null, Color.FromRgb(0xE6, 0xE9, 0xF0), false), // a keyboard key: outlined key cap
        };

        var text = new TextBlock
        {
            Text = button,
            FontSize = button.Length > 2 ? 11 : 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(ink),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Height = 24,
            MinWidth = 24,
            Padding = round ? new Thickness(0) : new Thickness(7, 0, 7, 0),
            CornerRadius = new CornerRadius(round ? 12 : 7),
            Background = fill is { } f ? new SolidColorBrush(f) : Brushes.Transparent,
            BorderBrush = fill is null ? new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)) : null,
            BorderThickness = fill is null ? new Thickness(1.5) : new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            Child = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
