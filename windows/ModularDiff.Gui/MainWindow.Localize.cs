using System.Windows;
using System.Windows.Controls;
using ModularDiff.Contracts;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

public partial class MainWindow
{
    /// <summary>Applies the active language to the whole window: everything declared in XAML
    /// (Loc keys) plus every text that is computed from state. Runs on construction and on every
    /// language change, so no chrome can be left behind in the previous language.</summary>
    private void LocalizeUI()
    {
        Loc.Apply(this);
        if (!string.Equals(LangBox.SelectedItem as string, Lang.CurrentName, StringComparison.Ordinal))
            LangBox.SelectedItem = Lang.CurrentName;   // the language changed through another entry point
        if (_ready)
        {
            RefreshPluginLists();
            RefreshDynamicTexts();
        }
        UpdateEditState();
        // Folder row tooltips read the language at binding time; re-point the rows so they follow a switch.
        LeftFolders.Refresh(); RightFolders.Refresh();
    }

    /// <summary>Plugin-backed combo boxes show plugin display names through Lang.Text (see
    /// PluginNameTemplate); re-binding the items re-evaluates those names in the new language.</summary>
    private void RefreshPluginLists()
    {
        foreach (var box in new[] { AlgorithmBox, PresentationBox, FormatBox, BufferBox, StorageBox, BufferCompareBox, MergeBox,
                     SyncPlannerBox, SyncPreviewBox, SyncExecutorBox, SyncBackupBox, SyncJournalBox, SyncRecoveryBox })
        {
            if (box.ItemsSource is null) continue;
            var items = box.Items.OfType<object>().ToArray();
            var selected = box.SelectedItem;
            box.ItemsSource = null;
            box.ItemsSource = items;
            box.SelectedItem = selected;
        }
        int module = ModuleBox.SelectedIndex;
        ModuleBox.ItemsSource = new[] { new ModuleChoice(null, Lang.Get("module_auto")) }
            .Concat(_plugins.GetAll<IComparisonModule>().Select(p => new ModuleChoice(p.Id, p.DisplayName))).ToArray();
        ModuleBox.SelectedIndex = module < 0 ? 0 : module;
    }

    /// <summary>Recomputes every label derived from the current state, in the active language.</summary>
    private void RefreshDynamicTexts()
    {
        PluginSummary.Text = Lang.Get("plugins_loaded", _plugins.GetAll<IPlugin>().Count);
        UpdateFolderSummary();
        RefreshSessionTabTexts();
        UpdateTextHeaders(_textFile);
        RefreshFileMessages();
        RefreshHunkLabel();
        UpdatePathBoxTips();
        UpdateSummary();
        UpdateFileStats();
        if (_activeSession is { IsBlank: true })
        {
            TextEmptyMessage.Text = Lang.Get("blank_tab_hint");
            StatusLabel.Text = Lang.Get("status_blank_tab");
        }
        else if (_fileResult is null && _folderResult is null && !_busy) StatusLabel.Text = Lang.Get("status_initial");
        RefreshExplorerSettings();
    }

    /// <summary>"N visible files / M files" at the right of the folder toolbar.</summary>
    private void UpdateFolderSummary()
    {
        // Both counts come from the tree. The result the window was handed is the root wave alone while a
        // scan runs, and it counts the folder entries too: the total would sit below the visible count.
        FolderSummary.Text = _tree is null ? ""
            : Lang.Get("folder_visible_summary", _folderRows.Count(n => !n.IsDirectory), _tree.All.Count(n => !n.IsDirectory));
    }
}
