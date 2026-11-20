using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

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

    /// <summary>An LB / RB / LT / RT cap, like the ones on the dock.</summary>
    public static FrameworkElement Bumper(string text, Thickness margin)
    {
        var glyph = ButtonGlyphs.Create(text, 24);
        glyph.Margin = margin;
        return glyph;
    }

    /// <summary>
    /// A big pill with a controller button in it ("A Play", "X Game page"), the way consoles show what each button
    /// does. The primary one is white. Not focusable: the controller button presses it, focus stays on the page.
    /// </summary>
    public static Button ActionPill(FrameworkElement resources, string key, string label, bool primary, Action onClick)
    {
        var ink = primary ? (Brush)resources.FindResource("SelectedTextBrush") : Brushes.White;
        var glyph = ButtonGlyphs.Create(key, 28, dark: primary);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 16,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = ink,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new Button
        {
            Style = (Style)resources.FindResource("ActionPill"),
            Content = content,
            Tag = primary ? "primary" : null,
            Margin = new Thickness(0, 0, 12, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static void Fade(UIElement element, double to, int milliseconds) =>
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    /// <summary>
    /// Rows rise in one after another, a few milliseconds apart. Each element gets its own opacity back
    /// afterwards, and its transform is removed, so nothing stays animated.
    /// </summary>
    public static void Stagger(IEnumerable<FrameworkElement> items, int stepMs, int maxSteps = 10)
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(320));
        int index = 0;
        foreach (var item in items)
        {
            var begin = TimeSpan.FromMilliseconds(Math.Min(index++, maxSteps) * stepMs);
            double opacity = item.Opacity;
            var previousTransform = item.RenderTransform;
            var offset = new TranslateTransform(0, 10);
            item.RenderTransform = offset;
            item.Opacity = 0;

            var fade = new DoubleAnimation(0, opacity, duration) { BeginTime = begin, EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                item.BeginAnimation(UIElement.OpacityProperty, null);
                item.Opacity = opacity;
                if (item.RenderTransform == offset)
                    item.RenderTransform = previousTransform;
            };
            item.BeginAnimation(UIElement.OpacityProperty, fade);
            offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { BeginTime = begin, EasingFunction = ease });
        }
    }

    public static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node == container)
                return true;
        return false;
    }

    /// <summary>
    /// A tile's two colors: the icon's strongest color, and a darker shade of it, for the gradient behind the icon.
    /// Slate grey when the icon has none (or there is no icon).
    /// </summary>
    public static (Color Light, Color Dark) TileColors(ImageSource? icon)
    {
        var fallback = (Color.FromRgb(0x4A, 0x54, 0x68), Color.FromRgb(0x23, 0x29, 0x3A));
        if (icon is not BitmapSource bitmap) return fallback;
        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);

            // Average the colorful, mostly opaque pixels, weighting each by how colorful it is.
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 160) continue;
                double pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double saturation = max <= 0 ? 0 : (max - min) / max;
                if (saturation < 0.25 || max < 40) continue;
                double w = saturation * saturation;
                r += pr * w; g += pg * w; b += pb * w; weight += w;
            }
            if (weight <= 0) return fallback;
            var light = Color.FromRgb((byte)(r / weight), (byte)(g / weight), (byte)(b / weight));
            light = Scale(light, 230.0 / Math.Max(1.0, Math.Max(light.R, Math.Max(light.G, (double)light.B)))); // bright, not neon
            return (light, Scale(light, 0.38));
        }
        catch
        {
            return fallback;
        }
    }

    private static Color Scale(Color color, double factor) => Color.FromRgb(
        (byte)Math.Clamp(color.R * factor, 0, 255), (byte)Math.Clamp(color.G * factor, 0, 255), (byte)Math.Clamp(color.B * factor, 0, 255));

}
