using System.Text;
using ModularDiff.Contracts;
using ModularDiff.Core;

internal static class EditingTests
{
    public static async Task Run(PluginCatalog plugins, string temp, Func<string, Func<Task>, Task> test)
    {
        var factory = plugins.Get<IEditBufferFactory>("edit-buffer");
        var comparer = plugins.Get<IBufferComparison>("buffer-comparison");
        var merge = plugins.Get<IBlockMerge>("block-merge");
        var storage = plugins.Get<ITextStorage>("text-storage");
        IEditBuffer Buffer(string text) => factory.Create(new(Path.Combine(temp, Guid.NewGuid().ToString()), text, new(), new(false, "")));
        BufferComparison Compare(IEditBuffer a, IEditBuffer b, bool ignoreEol = false) => comparer.Compare(a.Snapshot(), b.Snapshot(), "myers", new(IgnoreLineEndings: ignoreEol), plugins);
        await test("Editing: independent buffers, grouped typing, undo/redo and saved checkpoints", () =>
        {
            var a = Buffer("abc"); var b = Buffer("abc");
            a.Apply(new(3, 0, "d", "Typing")); a.Apply(new(4, 0, "e", "Typing"));
            Check(a.IsDirty && b.Text == "abc" && !b.IsDirty, "Independent buffers");
            a.Undo(); Check(a.Text == "abc" && !a.IsDirty, "Typing must undo together");
            a.Redo(); var saved = a.Snapshot(); a.AcceptSave(saved, new(true, "saved"));
            a.Undo(); Check(a.IsDirty, "Undo across saved checkpoint");
            a.Apply(new(0, 1, "A")); Check(!a.CanRedo, "New edit clears redo");
            a.AcceptSave(saved, new(true, "saved")); Check(a.IsDirty, "Late save must not mark newer edits clean");
            return Task.CompletedTask;
        });
        await test("Editing: 400 randomized bidirectional block merges converge and undo exactly", () =>
        {
            var random = new Random(817);
            for (int trial = 0; trial < 200; trial++)
            {
                string Text() => string.Join("\n", Enumerable.Range(0, random.Next(12)).Select(_ => random.Next(5).ToString())) + (random.Next(2) == 0 ? "\n" : "");
                string left = Text(), right = Text();
                foreach (var direction in Enum.GetValues<MergeDirection>())
                {
                    var a = Buffer(left); var b = Buffer(right); var target = direction == MergeDirection.LeftToRight ? b : a;
                    string expected = direction == MergeDirection.LeftToRight ? left : right;
                    int steps = 0;
                    while (target.Text != expected)
                    {
                        Check(steps++ < 30, "Merge did not converge"); string before = target.Text;
                        var edit = merge.Plan(Compare(a, b), a.Snapshot(), b.Snapshot(), 0, direction);
                        target.Apply(edit); string after = target.Text;
                        Check(before != after, "Merge made no progress"); target.Undo(); Check(target.Text == before, "Block undo"); target.Redo(); Check(target.Text == after, "Block redo");
                    }
                }
            }
            return Task.CompletedTask;
        });
        await test("Editing: stale merge rejected and ignored EOF boundary stays separate", () =>
        {
            var a = Buffer("a\nb\n"); var b = Buffer("a"); var diff = Compare(a, b, true);
            b.Apply(merge.Plan(diff, a.Snapshot(), b.Snapshot(), 0, MergeDirection.LeftToRight));
            Check(b.Text == "a\nb\n", "EOF insertion joined two logical lines");
            Expect<InvalidOperationException>(() => merge.Plan(diff, a.Snapshot(), b.Snapshot(), 0, MergeDirection.LeftToRight));
            return Task.CompletedTask;
        });
        await test("Storage: UTF8/16/32 BOM and mixed line endings round-trip", async () =>
        {
            Encoding[] encodings = [new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF32Encoding(false, true), new UTF32Encoding(true, true)];
            foreach (var encoding in encodings)
            {
                string path = Path.Combine(temp, Guid.NewGuid().ToString());
                byte[] Bytes(string text) => encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
                await File.WriteAllBytesAsync(path, Bytes("中文\r\none\ntwo\rend"));
                var buffer = factory.Create(await storage.LoadAsync(path)); buffer.Apply(new(0, 2, "编辑"));
                var saved = buffer.Snapshot(); buffer.AcceptSave(saved, await storage.SaveAsync(saved));
                Check(!buffer.IsDirty && (await File.ReadAllBytesAsync(path)).SequenceEqual(Bytes("编辑\r\none\ntwo\rend")), "Encoding or endings changed");
            }
        });
        await test("Storage: external write, new-file collision and cancellation preserve disk", async () =>
        {
            string path = Path.Combine(temp, "save-conflict"); await File.WriteAllTextAsync(path, "original");
            var buffer = factory.Create(await storage.LoadAsync(path)); buffer.Apply(new(0, 8, "edited"));
            await File.WriteAllTextAsync(path, "external");
            await ExpectAsync<DiskConflictException>(() => storage.SaveAsync(buffer.Snapshot()));
            Check(buffer.IsDirty && await File.ReadAllTextAsync(path) == "external", "Conflict lost data");
            string missing = Path.Combine(temp, "new-file"); var fresh = factory.Create(await storage.LoadAsync(missing, true)); fresh.Apply(new(0, 0, "new"));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => storage.SaveAsync(fresh.Snapshot(), cancelled.Token)); Check(!File.Exists(missing), "Cancelled save wrote file");
            var snapshot = fresh.Snapshot(); fresh.AcceptSave(snapshot, await storage.SaveAsync(snapshot)); Check(await File.ReadAllTextAsync(missing) == "new", "New file save");
            await ExpectAsync<DiskConflictException>(() => storage.SaveAsync(snapshot));
        });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
