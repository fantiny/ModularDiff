using ModularDiff.Core;

internal static class SessionStoreTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test, string temp) =>
        await test("Session store: named upsert/remove, recent dedupe+cap, round trip", () =>
        {
            string file = Path.Combine(temp, "sessions.json");
            var sessions = SessionStore.Load(file);
            Check(sessions.Named.Count == 0 && sessions.Recent.Count == 0, "Fresh store must start empty.");
            var saved = new SavedSession("工作区", @"C:\l", @"C:\r", "myers", true, false, false, true, DateTime.Now);
            sessions = sessions.UpsertNamed(saved);
            sessions = sessions.UpsertNamed(saved with { Left = @"C:\l2", SavedAt = DateTime.Now.AddMinutes(1) });
            Check(sessions.Named.Count == 1 && sessions.Named[0].Left == @"C:\l2", "Upsert must replace in place.");
            sessions = sessions.RemoveNamed("工作区");
            Check(sessions.Named.Count == 0, "Remove must delete the named session.");
            for (int i = 0; i < 15; i++)
                sessions = sessions.RecordRecent($@"C:\a{i}", @"C:\r", "myers", false, false, false, true);
            sessions = sessions.RecordRecent(@"C:\a3", @"C:\r", "patience", false, false, false, true);
            Check(sessions.Recent.Count == SessionStore.RecentLimit, "Recent must cap at the limit.");
            Check(sessions.Recent[0].Left == @"C:\a3" && sessions.Recent[0].Algorithm == "patience", "Repeat must move to front with new data.");
            Check(sessions.Recent.Count(s => s.Left.Equals(@"C:\a3", StringComparison.OrdinalIgnoreCase)) == 1, "Recent must not duplicate pairs.");
            SessionStore.Save(sessions, file);
            var loaded = SessionStore.Load(file);
            Check(loaded.Recent.Count == SessionStore.RecentLimit && loaded.Recent[0].Left == @"C:\a3", "Round trip must preserve recent order.");
            return Task.CompletedTask;
        });

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
