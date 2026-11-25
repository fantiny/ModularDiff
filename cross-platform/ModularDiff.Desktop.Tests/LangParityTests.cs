using System.Text.RegularExpressions;
using ModularDiff.Desktop;

/// <summary>Desktop 的 Lang 必须与 WPF 同约定:两份实现共用同一批语言包文件,
/// 任何一侧少一张表/少一条回退,用户就会在该平台看到原始键、枚举名或未翻译的插件文本。</summary>
internal static class LangParityTests
{
    public static void Run(Action<bool, string> check)
    {
        string? original = Lang.CurrentName;
        try
        {
            // 1) 插件经 Lang 键发出的文本(sync_*/file_status_*)在每一种语言里都能解析成人类可读文本。
            //    回归:同步预览曾在 macOS 上显示 "file_status_modified" / "sync_state_same" 这样的原始键。
            string[] pluginKeys =
            [
                "sync_choice_skip", "sync_choice_add_left", "sync_choice_add_right", "sync_choice_overwrite_left", "sync_choice_overwrite_right",
                "sync_state_conflict", "sync_state_neither", "sync_state_same", "sync_stamp_missing", "sync_result_summary",
                "file_status_equal", "file_status_error", "file_status_left_only", "file_status_modified", "file_status_pending", "file_status_right_only",
                "folder_scan_root", "folder_scan_progress", "folder_compare_progress", "folder_visible_summary", "status_summary", "status_pending_suffix",
            ];
            foreach (string pack in Lang.Available)
            {
                Lang.Apply(pack);
                foreach (string key in pluginKeys)
                {
                    string value = Lang.Get(key);
                    check(!string.IsNullOrWhiteSpace(value) && value != key, $"{pack}: '{key}' is missing from the pack (raw key would be shown)");
                    check(!Regex.IsMatch(value, "^(sync|file_status|folder|status)_"), $"{pack}: '{key}' still looks like a key: {value}");
                }
            }

            // 2) 带参格式化(界面用它拼 "内容比较 3/7 · a.txt" 这类动态文本)。
            Lang.Apply("中文");
            check(Lang.Get("folder_compare_progress", 3, 7, "a.txt") == "内容比较 3/7 · a.txt", $"format args ignored: {Lang.Get("folder_compare_progress", 3, 7, "a.txt")}");
            check(Lang.Get("folder_visible_summary", 12, 40) == "12 个可见文件 / 40 个文件", "format args ignored (folder_visible_summary)");
            check(Lang.Get("status_summary", 10, 4, 5, 1) == "10 个文件 · 4 相同 · 5 差异 · 1 错误", $"N0 formatting wrong: {Lang.Get("status_summary", 10, 4, 5, 1)}");
            check(Lang.Get("status_pending_suffix", 2500).Contains("2,500") || Lang.Get("status_pending_suffix", 2500).Contains("2500"), "N0 formatting wrong (status_pending_suffix)");
            check(Lang.Get("md_title").Length > 0 && Lang.Get("md_key_that_does_not_exist_xyz") == "md_key_that_does_not_exist_xyz", "unknown key must come back as the key itself");
            check(Lang.Get("folder_compare_progress") == Lang.Get("folder_compare_progress", Array.Empty<object>()), "no-args overload must equal the plain template");

            // 3) Core/插件来源文本经 messages 表翻译(异常消息、备注、显示名),未知文本原样返回。
            Lang.Apply("English");
            check(Lang.Text("内容比较中…") == "Comparing content…", $"plugin note not translated in English: {Lang.Text("内容比较中…")}");
            check(Lang.Text("子目录扫描中…") == "Scanning subfolders…", $"plugin note not translated in English: {Lang.Text("子目录扫描中…")}");
            // 引擎(Core)发的是英文;中文包用反向表把它译回中文,其它语言缺条目时按设计回退英文。
            const string engineMessage = "Choose two existing files or two existing directories.";
            check(Lang.Text(engineMessage) == engineMessage, "an English engine message must pass through an English UI unchanged");
            Lang.Apply("中文");
            check(Lang.Text(engineMessage) == "请选择两个存在的文件或两个存在的目录。", $"engine message not translated into Chinese: {Lang.Text(engineMessage)}");
            Lang.Apply("繁體中文");
            check(Lang.Text(engineMessage) == "請選擇兩個存在的檔案或兩個存在的目錄。", $"engine message not translated into Traditional Chinese: {Lang.Text(engineMessage)}");
            Lang.Apply("English");
            check(Lang.Text("a message no pack knows about") == "a message no pack knows about", "unknown source must pass through unchanged");
            check(Lang.Text(null) == "" && Lang.Text("") == "", "null/empty source");
            Lang.Apply("日本語");
            check(Lang.Text("内容比较中…") == "内容を比較中…", $"plugin note not translated in Japanese: {Lang.Text("内容比较中…")}");
            Lang.Apply("繁體中文");
            check(Lang.Text("内容比较中…") == "正在比較內容…", $"plugin note not translated in Traditional Chinese: {Lang.Text("内容比较中…")}");
            // 中文包承载反向表(英文来源 → 中文);带 {0} 的模板要保留侧别。
            Lang.Apply("中文");
            check(Lang.Text("left: symbolic link/reparse point excluded; comparison is incomplete.") == "left：符号链接/重解析点已排除；比较不完整。",
                $"templated English source not translated into Chinese: {Lang.Text("left: symbolic link/reparse point excluded; comparison is incomplete.")}");
            Lang.Apply("繁體中文");
            check(Lang.Text("right: symbolic link/reparse point excluded; comparison is incomplete.") == "right：符號連結/重新解析點已排除；比較不完整。", "templated source (zh-TW)");

            // 4) 键奇偶校验:任何语言包缺键都要进诊断(而不是让某个平台悄悄显示键名)。
            check(Lang.LoadDiagnostics.Count == 0, "language pack diagnostics:\n  " + string.Join("\n  ", Lang.LoadDiagnostics));

            // 5) 切换语言会通知订阅者;代码随语言包的 code 走。
            int notifications = 0;
            void OnChanged() => notifications++;
            Lang.Changed += OnChanged;
            try
            {
                Lang.Apply("English");
                check(notifications == 1 && Lang.CurrentCode == "en-US", "Apply must raise Changed once and publish the pack code");
                Lang.Apply("English");
                check(notifications == 1, "applying the language that is already active must not raise Changed");
                Lang.Apply("不存在的语言");
                check(notifications == 1 && Lang.CurrentName == "English", "an unknown language must be ignored");
            }
            finally { Lang.Changed -= OnChanged; }
        }
        finally { Lang.Apply(original ?? "中文"); }
    }
}
