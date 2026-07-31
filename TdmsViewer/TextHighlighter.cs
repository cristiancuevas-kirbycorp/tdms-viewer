using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TdmsViewer;

/// <summary>Attached behavior that renders a TextBlock's text with the matching query substring highlighted.</summary>
public static class TextHighlighter
{
    private static readonly Brush HighlightBrush = CreateBrush();

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(TextHighlighter), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(TextHighlighter), new PropertyMetadata(string.Empty, OnChanged));

    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);
    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetQuery(DependencyObject o, string value) => o.SetValue(QueryProperty, value);
    public static string GetQuery(DependencyObject o) => (string)o.GetValue(QueryProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        var text = GetText(d) ?? string.Empty;
        var tokens = (GetQuery(d) ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        tb.Inlines.Clear();
        if (tokens.Length == 0 || text.Length == 0)
        {
            tb.Inlines.Add(new Run(text));
            return;
        }

        // Mark every character covered by any token match, then emit runs by highlight state.
        var mask = new bool[text.Length];
        foreach (var token in tokens)
        {
            var index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                for (var k = index; k < index + token.Length; k++)
                    mask[k] = true;
                index += token.Length;
            }
        }

        var start = 0;
        while (start < text.Length)
        {
            var on = mask[start];
            var end = start;
            while (end < text.Length && mask[end] == on) end++;

            var run = new Run(text[start..end]);
            if (on)
            {
                run.Background = HighlightBrush;
                run.FontWeight = FontWeights.SemiBold;
            }
            tb.Inlines.Add(run);
            start = end;
        }
    }

    private static Brush CreateBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x7A));
        brush.Freeze();
        return brush;
    }
}
