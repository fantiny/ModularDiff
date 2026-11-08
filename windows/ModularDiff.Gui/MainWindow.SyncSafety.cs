using System.IO;
using System.Windows;
using ModularDiff.Contracts;

namespace ModularDiff.Gui;

public partial class MainWindow
{
    private string? _historyRootOverride;
    private SyncSafetyContext SafetyContext => new(_historyRootOverride ?? Arg("--sync-history-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularDiff", "SyncHistory"),
        SyncBackupBox.SelectedItem as ISyncBackup ?? throw new InvalidOperationException("缺少同步备份插件。"),
        SyncJournalBox.SelectedItem as ISyncJournal ?? throw new InvalidOperationException("缺少同步记录插件。"));
    private void InitializeSyncSafety()
    {
        SyncBackupBox.ItemsSource = _plugins.GetAll<ISyncBackup>(); SyncBackupBox.SelectedIndex = 0;
        SyncJournalBox.ItemsSource = _plugins.GetAll<ISyncJournal>(); SyncJournalBox.SelectedIndex = 0;
        SyncRecoveryBox.ItemsSource = _plugins.GetAll<ISyncRecovery>(); SyncRecoveryBox.SelectedIndex = 0;
    }
    private async void SyncHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_busy || SyncRecoveryBox.SelectedItem is not ISyncRecovery recovery || SyncBackupBox.SelectedItem is not ISyncBackup || SyncJournalBox.SelectedItem is not ISyncJournal) return;
        if (!await CanLeaveEdits()) return;
        _detailCancellation?.Cancel(); _syncActive = _busy = true; TextView.IsEnabled = false; UpdateEditState();
        try
        {
            var window = new SyncHistoryWindow(SafetyContext, recovery) { Owner = this }; window.ShowDialog();
            if (window.RecoveryAttempted && _folderResult is not null) await RefreshAfterSync();
        }
        catch (Exception ex) { StatusLabel.Text = "恢复窗口失败：" + ex.Message; }
        finally { _syncActive = _busy = false; TextView.IsEnabled = true; UpdateEditState(); }
    }
}
