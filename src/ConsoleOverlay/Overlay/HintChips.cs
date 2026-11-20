using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Turns a hint like "A select · B back · LB / RB other pages" into console-style prompts, drawn by
/// <see cref="ButtonGlyphs"/>: face buttons, bumper caps, and key caps for the keyboard.
/// </summary>
internal static class HintChips
{
    // Longest first, so "LB / RB" wins over "B".
    private static readonly string[] Prefixes =
    [
        "LB / RB", "LT / RT", "Q / E", "Ctrl+V", "← →", "Enter", "Esc", "Tab", "Delete", "A", "B", "X", "Y",
    ];


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
        var glyph = ButtonGlyphs.Create(button, 24);
        glyph.Margin = new Thickness(0, 0, 5, 0);
        return glyph;
    }
}
