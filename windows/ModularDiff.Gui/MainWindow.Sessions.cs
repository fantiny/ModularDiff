using System.IO;
using System.Windows;
using System.Windows.Controls;
using ModularDiff.Contracts;
using ModularDiff.Core;

namespace ModularDiff.Gui;

/// <summary>Named sessions and recent comparisons, persisted through the shared SessionStore.</summary>
public partial class MainWindow
{
    private SessionStore.SessionFile _sessions = new([], []);

    private void InitializeSessions()
    {
        _sessions = SessionStore.Load();
        RefreshSessionLists();
    }

    private void RefreshSessionLists()
    {
        SessionBox.ItemsSource = _sessions.Named.Select(s => s.Name).ToArray();
        RecentBox.ItemsSource = _sessions.Recent.Select(s => Path.GetFileName(s.Left) + " ↔ " + Path.GetFileName(s.Right)).ToArray();
    }

    private void SaveSessionClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LeftPath.Text) || string.IsNullOrWhiteSpace(RightPath.Text))
        { StatusLabel.Text = "请先填写左右路径，再保存会话。"; return; }
        string suggestion = Path.GetFileName(LeftPath.Text.TrimEnd('\\', '/')) + " ↔ " + Path.GetFileName(RightPath.Text.TrimEnd('\\', '/'));
        string? name = PromptSessionName(suggestion);
        if (string.IsNullOrWhiteSpace(name)) return;
        _sessions = _sessions.UpsertNamed(new SavedSession(name!, LeftPath.Text, RightPath.Text,
            (AlgorithmBox.SelectedItem as Contracts.IDiffAlgorithm)?.Id,
            IgnoreCaseBox.IsChecked == true, IgnoreWhitespaceBox.IsChecked == true, IgnoreEolBox.IsChecked == true,
            RecursiveBox.IsChecked == true, DateTime.Now,
            UnimportantBox.Text.Length > 0 ? [.. UnimportantBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)] : null));
        SessionStore.Save(_sessions);
        RefreshSessionLists();
        SessionBox.SelectedItem = name;
        StatusLabel.Text = "会话已保存：" + name;
    }

    private void DeleteSessionClick(object sender, RoutedEventArgs e)
    {
        if (SessionBox.SelectedItem is not string name) { StatusLabel.Text = "请先在会话列表中选择要删除的会话。"; return; }
        _sessions = _sessions.RemoveNamed(name);
        SessionStore.Save(_sessions);
        RefreshSessionLists();
        StatusLabel.Text = "会话已删除：" + name;
    }

    private async void SessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SessionBox.SelectedItem is not string name) return;
        var session = _sessions.Named.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (session is null) return;
        ApplySession(session);
        await CompareAsync();
    }

    private async void RecentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || RecentBox.SelectedItem is not string label) return;
        int index = Array.IndexOf([.. _sessions.Recent.Select(s => Path.GetFileName(s.Left) + " ↔ " + Path.GetFileName(s.Right))], label);
        if (index < 0 || index >= _sessions.Recent.Count) return;
        ApplySession(_sessions.Recent[index]);
        await CompareAsync();
    }

    private void ApplySession(SavedSession session)
    {
        LeftPath.Text = session.Left; RightPath.Text = session.Right;
        IgnoreCaseBox.IsChecked = session.IgnoreCase;
        IgnoreWhitespaceBox.IsChecked = session.IgnoreWhitespace;
        IgnoreEolBox.IsChecked = session.IgnoreEol;
        RecursiveBox.IsChecked = session.Recursive;
        UnimportantBox.Text = session.UnimportantPatterns is { } patterns ? string.Join("\n", patterns) : "";
        if (session.Algorithm is { } id && AlgorithmBox.ItemsSource is System.Collections.IEnumerable algorithms)
            foreach (var plugin in algorithms.OfType<Contracts.IDiffAlgorithm>())
                if (plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) { AlgorithmBox.SelectedItem = plugin; break; }
    }

    /// <summary>Called after a successful comparison; failures never break comparing.</summary>
    private void RecordRecentComparison(ComparisonRequest request)
    {
        try
        {
            _sessions = _sessions.RecordRecent(request.LeftPath, request.RightPath, request.AlgorithmId,
                request.EffectiveOptions.IgnoreCase, request.EffectiveOptions.IgnoreWhitespace, request.EffectiveOptions.IgnoreLineEndings, request.EffectiveOptions.Recursive,
                request.EffectiveOptions.UnimportantPatterns);
            SessionStore.Save(_sessions);
            RefreshSessionLists();
        }
        catch (Exception) { /* session bookkeeping is best-effort */ }
    }

    private string? PromptSessionName(string suggestion)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => ReferenceEquals(w, this));
        var dialog = new Window
        {
            Title = "保存比较会话", Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner, ResizeMode = ResizeMode.NoResize,
        };
        var box = new TextBox { Text = suggestion, Margin = new Thickness(12, 4, 12, 8) };
        var ok = new Button { Content = "保存", Width = 92, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 66, IsCancel = true };
        bool? confirmed = null;
        ok.Click += (_, _) => { confirmed = true; dialog.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "会话名称：", Margin = new Thickness(12, 12, 12, 0) });
        panel.Children.Add(box); panel.Children.Add(buttons);
        dialog.Content = panel;
        box.SelectAll(); box.Loaded += (_, _) => box.Focus();
        dialog.ShowDialog();
        return confirmed == true && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
