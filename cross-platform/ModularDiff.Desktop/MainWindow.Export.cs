using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ModularDiff.Contracts;
using ModularDiff.Core;

namespace ModularDiff.Desktop;

/// <summary>报告导出:WPF 的 ExportButton + FormatBox 在 macOS 上的对等物。
/// 格式全部来自 IResultFormatter 插件(文本 / JSON / HTML),宿主不认识任何一种具体格式。</summary>
public sealed partial class MainWindow
{
    /// <summary>十六进制视图正在显示一对二进制文件。</summary>
    public bool HexViewActive => _hexLeft is not null;

    /// <summary>格式下拉:显示的是插件显示名的译文,所以每次换语言都重建;所选的格式按 Id 保留。
    /// 默认 HTML —— 人们互相传的那一种。</summary>
    private void RefreshFormatBox()
    {
        string? chosen = SelectedReportFormatter?.Id;   // 取在重建之前:ItemsSource 一换,选中序号就没了
        _formatters = _plugins.GetAll<IResultFormatter>().ToArray();
        _formatBox.ItemsSource = _formatters.Select(f => Lang.Text(f.DisplayName)).ToArray();
        int index = Array.FindIndex(_formatters, f => f.Id == chosen);
        if (index < 0) index = Array.FindIndex(_formatters, f => f.Id == "html");
        if (index < 0 && _formatters.Length > 0) index = 0;
        _formatBox.SelectedIndex = index;
    }

    public IResultFormatter? SelectedReportFormatter =>
        (uint)_formatBox.SelectedIndex < (uint)_formatters.Length ? _formatters[_formatBox.SelectedIndex] : null;

    /// <summary>导出"当前比较"的报告,成功写盘才返回 true。
    /// <list type="bullet">
    /// <item>目录页:整个目录比较 —— 扫描结束之后(扫描中的结果只有根目录,看起来却像完整的)。</item>
    /// <item>差异 / 编辑页:当前这一对文件。文本对按导出时磁盘上的内容重新比较,所以未保存的修改不在报告里
    /// (状态栏会说明);十六进制 / 图片对用视图依据的那一次比较。</item>
    /// </list>
    /// 报告原子写盘:格式化器中途失败不会留下半截文件。报告绝不写进被比较的文件或目录 —— 那会在下次比较里
    /// 多出一个单侧文件,盖在被比较的文件上则是直接毁掉它。<paramref name="outputPath"/> 为 null 时弹出保存对话框。</summary>
    public async Task<bool> ExportReportAsync(string? outputPath = null, IResultFormatter? formatter = null)
    {
        formatter ??= SelectedReportFormatter;
        if (formatter is null) { _status.Localized(() => Lang.Get("md_export_no_formatter")); return false; }

        bool unsaved = false;
        ComparisonResult? result = null;
        if (_tabs.SelectedIndex != 2)
        {
            if (_session.Left is { } sessionLeft && _session.Right is { } sessionRight)
            {
                unsaved = _session.Dirty;
                var request = new ComparisonRequest(sessionLeft.Path, sessionRight.Path, _session.Algorithm, _session.Options);
                result = await Task.Run(() => new ComparisonEngine(_plugins).CompareAsync(request));
            }
            else result = _pair;
        }
        result ??= FolderCompareComplete ? _directory : null;
        if (result is null)
        {
            bool scanning = _folderUiTimer.IsEnabled;
            _status.Localized(() => Lang.Get(scanning ? "md_export_scanning" : "md_export_nothing"));
            return false;
        }

        if (outputPath is null)
        {
            var options = new FilePickerSaveOptions
            {
                Title = Lang.Get("export"), SuggestedFileName = "comparison" + formatter.FileExtension,
                DefaultExtension = formatter.FileExtension.TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType(Lang.Text(formatter.DisplayName)) { Patterns = ["*" + formatter.FileExtension] }],
            };
            // 用户取消:什么都不说,什么都不写。
            var picked = await StorageProvider.SaveFilePickerAsync(options);
            if (picked is null) return false;
            outputPath = picked.TryGetLocalPath() ?? throw new IOException(Lang.Get("md_export_no_path"));
        }
        string output = Path.GetFullPath(outputPath);
        try
        {
            string?[] inputs = [result.LeftPath, result.RightPath, _directory?.LeftPath, _directory?.RightPath];
            if (inputs.Where(path => path is not null).Any(path => SameOrInside(output, path!)))
            {
                string outside = Lang.Get("err_export_outside");
                _status.Localized(() => Lang.Get("status_export_failed", outside));
                return false;
            }
            using var buffer = new StringWriter();
            await Task.Run(() => formatter.WriteAsync(result, buffer));   // 插件的代码:它写完之前磁盘上不会有任何东西
            AtomicFile.WriteAllText(output, buffer.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string reason = Lang.Text(ex.Message);
            _status.Localized(() => Lang.Get("status_export_failed", reason));
            return false;
        }
        string saved = output;
        _status.Localized(() => Lang.Get("status_export_saved", saved) + (unsaved ? " · " + Lang.Get("md_export_unsaved") : ""));
        return true;
    }

    /// <summary><paramref name="output"/> 就是 <paramref name="input"/>,或落在它(是目录时)里面。</summary>
    private static bool SameOrInside(string output, string input)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return output.Equals(input, comparison)
            || (Directory.Exists(input) && output.StartsWith(Path.TrimEndingDirectorySeparator(input) + Path.DirectorySeparatorChar, comparison));
    }
}
