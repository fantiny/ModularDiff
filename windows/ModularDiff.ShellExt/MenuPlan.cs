namespace ModularDiff.ShellExt;

public enum MenuAction { Compare, SelectLeft, CompareTo }

public sealed record MenuEntry(MenuAction Action, string Text);

public sealed record SelectionPath(string Path, bool IsDirectory);

/// <summary>Decides which "ModularDiff …" commands the context menu shows for a selection.
/// Pure logic, independent of COM, so it can be unit tested on any platform.</summary>
public static class MenuPlan
{
    /// <param name="selection">Items the user selected in Explorer.</param>
    /// <param name="left">Remembered left side (null when none or no longer valid).</param>
    public static IReadOnlyList<MenuEntry> Plan(IReadOnlyList<SelectionPath> selection, SelectionPath? left)
    {
        if (selection.Count == 2)
        {
            var (a, b) = (selection[0], selection[1]);
            if (!Same(a, b) && a.IsDirectory == b.IsDirectory)
                return [new(MenuAction.Compare, "ModularDiff Compare")];
            return [];
        }
        if (selection.Count == 1)
        {
            var item = selection[0];
            if (left is not null && !Same(left, item) && left.IsDirectory == item.IsDirectory)
                return
                [
                    new(MenuAction.CompareTo, "ModularDiff Compare to '" + Name(left.Path) + "'"),
                    new(MenuAction.SelectLeft, "ModularDiff Select Left"),
                ];
            return [new(MenuAction.SelectLeft, "ModularDiff Select Left")];
        }
        return []; // nothing selected, more than two items, mixed kinds or duplicates: stay out of the menu
    }

    public static string Name(string path)
    {
        string trimmed = path.TrimEnd('/', '\\');
        return trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
    }
    private static bool Same(SelectionPath a, SelectionPath b) =>
        string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
}
