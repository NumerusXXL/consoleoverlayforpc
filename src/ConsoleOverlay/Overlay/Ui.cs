using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConsoleOverlay.Overlay;

/// <summary>Small building blocks shared by the pages. Styles come from Themes/Theme.xaml.</summary>
internal static class Ui
{
    public static Button Row(FrameworkElement resources, string title, string? hint, string? value, Action? onClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = (Style)resources.FindResource("RowTitle") });
        if (hint is not null)
            text.Children.Add(new TextBlock { Text = hint, Style = (Style)resources.FindResource("RowHint") });
        grid.Children.Add(text);

        if (value is not null)
        {
            var valueText = new TextBlock { Text = value + "  ›", Style = (Style)resources.FindResource("RowValue"), Margin = new Thickness(16, 0, 0, 0) };
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
        }

        var button = new Button { Style = (Style)resources.FindResource("RowButton"), Content = grid };
        if (onClick is not null)
            button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A row that only informs. It can still take focus, so the page always has something to land on.</summary>
    public static Button Placeholder(FrameworkElement resources, string title, string hint)
    {
        var row = Row(resources, title, hint, null, null);
        row.Opacity = 0.75;
        return row;
    }

    public static TextBlock Section(FrameworkElement resources, string text, bool first) => new()
    {
        Text = text.ToUpperInvariant(),
        Style = (Style)resources.FindResource("SectionLabel"),
        Margin = new Thickness(2, first ? 0 : 18, 0, 8),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Note(FrameworkElement resources, string text) => new()
    {
        Text = text,
        Style = (Style)resources.FindResource("RowHint"),
        Margin = new Thickness(16, 4, 0, 10),
    };

    public static TextBlock Icon(FrameworkElement resources, string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)resources.FindResource("IconFont"),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node == container)
                return true;
        return false;
    }
}
