using System.Windows;
using System.Windows.Controls;
using ModularDiff.Gui.Controls;

namespace ModularDiff.Gui;

public partial class MainWindow
{
    /// <summary>Applies the active language pack to chrome that lives in XAML as design-time text.</summary>
    private void LocalizeUI()
    {
        Title = Lang.Get("app_title");
        FolderTab.Content = Lang.Get("folder_tab");
        NewTabButton.ToolTip = Lang.Get("new_tab");

        CompareButton.ToolTip = Lang.Get("compare_button");
        CancelButton.ToolTip = Lang.Get("cancel_button");
        OpenButton.ToolTip = Lang.Get("open_button");
        PreviousFileButton.ToolTip = Lang.Get("prev_file");
        NextFileButton.ToolTip = Lang.Get("next_file");
        ExportButton.ToolTip = Lang.Get("export");
        SaveAllButton.ToolTip = Lang.Get("save_all");
        UndoButton.ToolTip = Lang.Get("undo");
        RedoButton.ToolTip = Lang.Get("redo");
        SaveLeftButton.ToolTip = Lang.Get("save_left");
        SaveRightButton.ToolTip = Lang.Get("save_right");
        LeftPath.ToolTip = Lang.Get("path_tip");
        RightPath.ToolTip = Lang.Get("path_tip");
        LangBox.ToolTip = Lang.Get("tip_lang");
        ThemeToggle.ToolTip = Lang.Get("tip_theme");
        SettingsToggle.ToolTip = Lang.Get("settings");

        MergeLeftButton.ToolTip = Lang.Get("merge_left_tip");
        MergeRightButton.ToolTip = Lang.Get("merge_right_tip");
        CopyAllLeftButton.ToolTip = Lang.Get("copy_all_left_tip");
        CopyAllRightButton.ToolTip = Lang.Get("copy_all_right_tip");
        FormatButton.ToolTip = Lang.Get("format");
        RealignButton.ToolTip = Lang.Get("realign");
        AutoRealignToggle.ToolTip = Lang.Get("auto_realign");
        LockHorizontal.ToolTip = Lang.Get("lock_scroll");
        ShowEol.ToolTip = Lang.Get("show_eol");
        WrapToggle.ToolTip = Lang.Get("wrap");
        FindBox.ToolTip = Lang.Get("find");
        ReplaceToggle.ToolTip = Lang.Get("replace");
        ReplaceBox.ToolTip = Lang.Get("replace_text");
        ReplaceOneButton.ToolTip = Lang.Get("replace_one");
        ReplaceAllButton.ToolTip = Lang.Get("replace_all");

        LocalizeCombo(FolderFilter, ["folder_filter_all", "folder_filter_diff", "folder_filter_same", "folder_filter_left", "folder_filter_right"]);
        LocalizeCombo(TextFilter, ["show_all", "show_diff_only", "show_context"]);
        FolderSearch.ToolTip = Lang.Get("folder_search_tip");
        FolderSearchRegex.ToolTip = Lang.Get("folder_regex_tip");

        IgnoreCaseBox.Content = Lang.Get("ignore_case");
        IgnoreWhitespaceBox.Content = Lang.Get("ignore_whitespace");
        IgnoreEolBox.Content = Lang.Get("ignore_eol");
        RecursiveBox.Content = Lang.Get("recursive");
        UnimportantBox.ToolTip = Lang.Get("unimportant_tip");
        SaveSessionButton.Content = Lang.Get("save_session");
        DeleteSessionButton.Content = Lang.Get("delete_session");
        RegisterMenuButton.Content = Lang.Get("register_menu");
        UnregisterMenuButton.Content = Lang.Get("unregister_menu");
        ClassicMenuButton.Content = Lang.Get("classic_menu");
        ModernMenuButton.Content = Lang.Get("modern_menu");

        foreach (var btn in FindLogicalChildren<Button>(RootLayout))
        {
            string? key = btn.Tag?.ToString() switch
            {
                "selected-left" => "sync_sel_left_tip",
                "selected-right" => "sync_sel_right_tip",
                "batch-left" => "sync_batch_left_tip",
                "batch-right" => "sync_batch_right_tip",
                "batch-both" => "sync_batch_both_tip",
                "i18n:swap" => "swap_button",
                "i18n:new_window" => "new_window",
                "i18n:merge" => "merge_button",
                "i18n:clear_filter" => "clear_folder_filter",
                "i18n:expand" => "expand_all",
                "i18n:collapse" => "collapse_all",
                "i18n:history" => "sync_history_tip",
                "i18n:prev_diff" => "prev_diff",
                "i18n:next_diff" => "next_diff",
                "i18n:zoom_out" => "zoom_out",
                "i18n:zoom_in" => "zoom_in",
                "i18n:layout" => "layout_toggle",
                "i18n:find_next" => "find",
                _ => null,
            };
            if (key is null && btn.Tag is "left" or "right")
            {
                // Segoe glyph E8A5 = file, E8B7 = folder (see XAML Content).
                string content = btn.Content?.ToString() ?? "";
                key = content.Contains('\uE8B7') ? "browse_folder" : "browse_file";
            }
            if (key is not null) btn.ToolTip = Lang.Get(key);
        }

        LocalizeDataGridHeaders(LeftFolders);
        LocalizeDataGridHeaders(RightFolders);

        ActivityLabel.Text = Lang.Get("activity_ready");
        if (!string.Equals(LangBox.SelectedItem as string, Lang.CurrentName, StringComparison.Ordinal))
            LangBox.SelectedItem = Lang.CurrentName;   // 语言经其它入口变化时回同步下拉框显示
        UpdateEditState();
        // Refresh folder row bindings so StatusText/Description pick up the new language.
        if (LeftFolders.ItemsSource is not null)
        {
            var src = LeftFolders.ItemsSource;
            LeftFolders.ItemsSource = RightFolders.ItemsSource = null;
            LeftFolders.ItemsSource = RightFolders.ItemsSource = src;
        }
    }

    private static void LocalizeDataGridHeaders(DataGrid grid)
    {
        foreach (var col in grid.Columns)
        {
            string header = col.Header?.ToString() ?? "";
            col.Header = header switch
            {
                "名称" or "Name" or "名稱" or "名前" or "이름" => Lang.Get("col_name"),
                "大小" or "Size" or "サイズ" or "크기" => Lang.Get("col_size"),
                "修改时间" or "Modified" or "修改時間" or "更新日時" or "수정 시간" => Lang.Get("col_modified"),
                "状态" or "Status" or "狀態" or "状態" or "상태" => Lang.Get("col_status"),
                _ => col.Header,
            };
        }
    }

    private static void LocalizeCombo(ComboBox box, string[] keys)
    {
        for (int i = 0; i < box.Items.Count && i < keys.Length; i++)
            if (box.Items[i] is ComboBoxItem item) item.Content = Lang.Get(keys[i]);
    }

    private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T t) yield return t;
            if (child is DependencyObject d)
                foreach (var nested in FindLogicalChildren<T>(d)) yield return nested;
        }
    }
}
