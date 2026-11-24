using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ModularDiff.Gui.Controls;

/// <summary>Captions for file-pair tabs, made to stay readable on a narrow tab. A plain TextBlock
/// trims at the end, so names that share a beginning ("ModularDiff.Algorithms.Myers.dll",
/// "…Myers.pdb", "…Myers.deps.json") all collapse into the same leading characters. Here each name
/// is trimmed in the middle instead: its end — the extension, usually the part that differs — stays
/// visible. A caption that fits is left untouched.</summary>
public static class TabCaption
{
    public const string PairSeparator = " ↔ ";
    private const string UnsavedMarker = "● ", Ellipsis = "…";

    /// <summary>Caption of a pair: both file names when they differ, the name once when both sides
    /// carry the same file name (the tab tooltip holds the full paths).</summary>
    public static string Of(string leftPath, string rightPath)
    {
        string left = Path.GetFileName(leftPath), right = Path.GetFileName(rightPath);
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ? left : left + PairSeparator + right;
    }

    /// <summary><paramref name="caption"/> fitted into <paramref name="width"/> device-independent
    /// pixels, measured in the font of <paramref name="text"/>. The unsaved marker and the pair
    /// separator are kept as they are; the width left over is shared between the names in proportion
    /// to their natural width.</summary>
    public static string Fit(FrameworkElement text, string caption, double width)
    {
        width -= 1.5;   // margin against sub-pixel differences between measuring and layout (TextTrimming is only the safety net)
        if (width <= 0 || caption.Length == 0) return caption;
        var typeface = new Typeface(TextElement.GetFontFamily(text), TextElement.GetFontStyle(text), FontWeights.SemiBold, TextElement.GetFontStretch(text));
        double pixelsPerDip = VisualTreeHelper.GetDpi(text).PixelsPerDip;
        // Measured in the bold weight of the active tab: what fits bold fits the regular weight too,
        // so the caption need not be fitted again when the tab becomes active.
        double Measure(string value) => value.Length == 0 ? 0
            : new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, TextElement.GetFontSize(text), Brushes.Black, pixelsPerDip).Width;
        if (Measure(caption) <= width) return caption;

        string marker = caption.StartsWith(UnsavedMarker, StringComparison.Ordinal) ? UnsavedMarker : "";
        string[] names = caption[marker.Length..].Split(PairSeparator);
        double separatorWidth = Measure(PairSeparator);
        double room = width - Measure(marker) - separatorWidth * (names.Length - 1);
        if (room <= 0) return marker + Ellipsis;
        double total = names.Sum(name => Measure(name));
        var fitted = new string[names.Length];
        for (int i = 0; i < names.Length; i++)
            fitted[i] = TrimMiddle(names[i], room * Measure(names[i]) / total, Measure);
        return marker + string.Join(PairSeparator, fitted);
    }

    private static string TrimMiddle(string name, double width, Func<string, double> measure)
    {
        if (name.Length == 0 || measure(name) <= width) return name;
        int dot = name.LastIndexOf('.');
        // Keep the extension plus a few characters before it — never more than the back half.
        int tail = dot > 0 ? Math.Clamp(name.Length - dot + 4, 4, Math.Max(4, name.Length / 2)) : Math.Min(4, Math.Max(1, name.Length - 1));
        if (name.Length > tail && char.IsLowSurrogate(name[name.Length - tail])) tail++;   // the tail must not start inside a surrogate pair
        while (tail > 1 && measure(Ellipsis + Tail(name, tail)) > width) tail--;             // extremely narrow: give up tail characters too
        // The longest head that still fits with the ellipsis and the tail.
        int low = 0, high = Math.Max(0, name.Length - tail - 1), best = 0;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            while (middle <= high && char.IsLowSurrogate(name[middle])) middle++;            // the head must not end inside a surrogate pair
            if (middle > high) break;
            if (measure(name[..middle] + Ellipsis + Tail(name, tail)) <= width) { best = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return (best > 0 ? name[..best] : "") + Ellipsis + Tail(name, tail);
    }

    private static string Tail(string name, int tail) => name[^(Math.Min(tail, name.Length))..];
}
