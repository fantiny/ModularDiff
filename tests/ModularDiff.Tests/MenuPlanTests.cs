using ModularDiff.ShellExt;

internal static class MenuPlanTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test) =>
        await test("ShellExt menu plan follows selection count and remembered left side", () =>
        {
            var fileA = new SelectionPath(@"C:\t\a.cs", IsDirectory: false);
            var fileB = new SelectionPath(@"C:\t\b.txt", IsDirectory: false);
            var dirA = new SelectionPath(@"C:\t\left", IsDirectory: true);
            var dirB = new SelectionPath(@"C:\t\right", IsDirectory: true);

            Check(MenuPlan.Plan([fileA, fileB], null) is [var c] && c.Action == MenuAction.Compare && c.Text == "ModularDiff Compare",
                "Two files of different extensions compare directly.");
            Check(MenuPlan.Plan([dirA, dirB], null) is [var cd] && cd.Action == MenuAction.Compare,
                "Two directories compare directly.");
            Check(MenuPlan.Plan([fileA, fileA], null).Count == 0, "Identical paths stay out of the menu.");
            Check(MenuPlan.Plan([fileA, dirA], null).Count == 0, "Mixed file/directory selection stays out.");
            Check(MenuPlan.Plan([fileA, fileB, fileA], null).Count == 0, "Three or more items stay out.");
            Check(MenuPlan.Plan([], null).Count == 0, "Empty selection stays out.");

            Check(MenuPlan.Plan([fileA], null) is [var leftOnly] && leftOnly.Action == MenuAction.SelectLeft,
                "Single file without a remembered side offers Select Left only.");
            Check(MenuPlan.Plan([fileA], dirA) is [var leftOnlyDir] && leftOnlyDir.Action == MenuAction.SelectLeft,
                "Remembered side of a different kind is ignored.");
            Check(MenuPlan.Plan([fileA], fileB) is [var to, var again] && to.Action == MenuAction.CompareTo
                && to.Text == "ModularDiff Compare to 'b.txt'" && again.Action == MenuAction.SelectLeft,
                "Single file with a remembered side offers Compare-to first, then Select Left.");
            Check(MenuPlan.Plan([dirA], dirB) is [var dto, _] && dto.Text.Contains("right"),
                "Compare-to names the remembered directory.");
            Check(MenuPlan.Plan([fileB], fileB) is [var redo] && redo.Action == MenuAction.SelectLeft,
                "Re-selecting the remembered side goes back to Select Left.");

            Check(MenuPlan.Name(@"C:\t\b.txt") == "b.txt" && MenuPlan.Name(@"C:\t\left\") == "left" && MenuPlan.Name("solo.cs") == "solo.cs",
                "Display names come from the final path segment.");
            return Task.CompletedTask;
        });

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
