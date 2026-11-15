using System.Windows.Media;

namespace ModularDiff.Gui.Controls;

/// <summary>Centralized color palette: all rendering colors come from here so dark mode is a
/// single switch. Controls reference Theme.Current instead of hardcoding brushes.</summary>
public static class Theme
{
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    // ─── shared palette ───
    public static Brush WindowBackground { get; private set; } = Brush("#F1F3F7");
    public static Brush PanelBackground { get; private set; } = Brushes.White;
    public static Brush PanelHeader { get; private set; } = Brush("#E8ECF2");
    public static Brush Border { get; private set; } = Brush("#DDE3EC");
    public static Brush Ink { get; private set; } = Brush("#253041");
    public static Brush Muted { get; private set; } = Brush("#8793A4");
    public static Brush Accent { get; private set; } = Brush("#3569AE");
    public static Brush AccentText { get; private set; } = Brushes.White;

    // ─── diff colors ───
    public static Brush DiffModified { get; private set; } = Brush("#FFF6D6");
    public static Brush DiffDeleted { get; private set; } = Brush("#FFF0F0");
    public static Brush DiffInserted { get; private set; } = Brush("#ECF7EF");
    public static Brush DiffGhost { get; private set; } = Brush("#F2F4F7");
    public static Brush DiffSelected { get; private set; } = Brush("#D9E8FF");
    public static Brush ChangedText { get; private set; } = Brush("#A52D22");
    public static Brush ChangedBackground { get; private set; } = Brush("#FFD4BC");

    // ─── markers ───
    public static Brush ModifiedBar { get; private set; } = Brush("#EAA05B");
    public static Brush DeletedBar { get; private set; } = Brush("#D76565");
    public static Brush InsertedBar { get; private set; } = Brush("#65A67F");

    // ─── syntax ───
    public static Brush SyntaxKeyword { get; private set; } = Brush("#2E60AA");
    public static Brush SyntaxString { get; private set; } = Brush("#98632C");
    public static Brush SyntaxComment { get; private set; } = Brush("#6A8473");
    public static Brush SyntaxNumber { get; private set; } = Brush("#7A3E9D");
    public static Brush SyntaxType { get; private set; } = Brush("#0B7285");

    // ─── gutter ───
    public static Brush Gutter { get; private set; } = Brush("#F7F8FA");
    public static Brush Caret { get; private set; } = Brush("#254E86");

    // ─── hex ───
    public static Brush HexMuted { get; private set; } = Brush("#8793A4");
    public static Brush HexDiff { get; private set; } = Brush("#A52D22");

    private static readonly Brush Light = Brush("#F1F3F7");

    public static void SetDark(bool dark)
    {
        if (IsDark == dark) return;
        IsDark = dark;
        if (dark)
        {
            WindowBackground = Brush("#1E1E2E");
            PanelBackground = Brush("#2A2A3C");
            PanelHeader = Brush("#333348");
            Border = Brush("#3D3D52");
            Ink = Brush("#CDD6F4");
            Muted = Brush("#6C7086");
            Accent = Brush("#89B4FA");
            AccentText = Brush("#1E1E2E");
            DiffModified = Brush("#4A4A38");
            DiffDeleted = Brush("#453038");
            DiffInserted = Brush("#2F4A3A");
            DiffGhost = Brush("#333344");
            DiffSelected = Brush("#3B4A6B");
            ChangedText = Brush("#F38BA8");
            ChangedBackground = Brush("#5B3A48");
            ModifiedBar = Brush("#FAB387");
            DeletedBar = Brush("#EBA0AC");
            InsertedBar = Brush("#A6E3A1");
            SyntaxKeyword = Brush("#89B4FA");
            SyntaxString = Brush("#F9E2AF");
            SyntaxComment = Brush("#6C7086");
            SyntaxNumber = Brush("#F5C2E7");
            SyntaxType = Brush("#94E2D5");
            Gutter = Brush("#252535");
            Caret = Brush("#89B4FA");
            HexMuted = Brush("#6C7086");
            HexDiff = Brush("#F38BA8");
        }
        else
        {
            WindowBackground = Brush("#F1F3F7");
            PanelBackground = Brushes.White;
            PanelHeader = Brush("#E8ECF2");
            Border = Brush("#DDE3EC");
            Ink = Brush("#253041");
            Muted = Brush("#8793A4");
            Accent = Brush("#3569AE");
            AccentText = Brushes.White;
            DiffModified = Brush("#FFF6D6");
            DiffDeleted = Brush("#FFF0F0");
            DiffInserted = Brush("#ECF7EF");
            DiffGhost = Brush("#F2F4F7");
            DiffSelected = Brush("#D9E8FF");
            ChangedText = Brush("#A52D22");
            ChangedBackground = Brush("#FFD4BC");
            ModifiedBar = Brush("#EAA05B");
            DeletedBar = Brush("#D76565");
            InsertedBar = Brush("#65A67F");
            SyntaxKeyword = Brush("#2E60AA");
            SyntaxString = Brush("#98632C");
            SyntaxComment = Brush("#6A8473");
            SyntaxNumber = Brush("#7A3E9D");
            SyntaxType = Brush("#0B7285");
            Gutter = Brush("#F7F8FA");
            Caret = Brush("#254E86");
            HexMuted = Brush("#8793A4");
            HexDiff = Brush("#A52D22");
        }
        Changed?.Invoke();
    }

    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}
