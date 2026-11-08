using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ModularDiff.Gui;

internal static class ExplorerIntegration
{
    private const string StateKey = @"Software\ModularDiff\Explorer";
    private const string ClassicKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";
    public static bool SupportsClassicMenu => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    public static bool IsClassicMenuConfigured()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClassicKey);
        return key?.GetValue("") is string value && value.Length == 0;
    }
    public static void SetClassicMenu(bool enabled)
    {
        if (!SupportsClassicMenu) throw new PlatformNotSupportedException("此设置仅适用于 Windows 11。");
        using var existing = Registry.CurrentUser.OpenSubKey(ClassicKey, writable: true);
        var value = existing?.GetValue("");
        if (value is not null && value is not "")
            throw new IOException("此注册表项已有其他配置，未修改。请先检查系统设置。");
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClassicKey);
            key.SetValue("", "", RegistryValueKind.String);
        }
        else
        {
            // Remove only the empty override; preserve unrelated values and subkeys.
            existing?.DeleteValue("", throwOnMissingValue: false);
            if (existing is not null && existing.ValueCount == 0 && existing.SubKeyCount == 0)
                Registry.CurrentUser.DeleteSubKey(ClassicKey, throwOnMissingSubKey: false);
        }
        Notify();
    }
    public static (bool Any, bool Current) RegistrationStatus()
    {
        bool any = false, current = true;
        foreach (string target in Targets)
        {
            using var menu = Registry.CurrentUser.OpenSubKey(target);
            any |= menu is not null;
            foreach (var (id, verb) in new[] { ("01Left", "--shell-left"), ("02Compare", "--shell-right") })
            {
                using var command = menu?.OpenSubKey(@"shell\" + id + @"\command");
                current &= string.Equals(command?.GetValue("") as string,
                    "\"" + Environment.ProcessPath + "\" " + verb + " \"%1\"", StringComparison.OrdinalIgnoreCase);
            }
        }
        return (any, current);
    }
    private static readonly string[] Targets = [@"Software\Classes\*\shell\ModularDiff", @"Software\Classes\Directory\shell\ModularDiff"];
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    public static void Install()
    {
        string exe = Environment.ProcessPath ?? throw new IOException("无法确定 EXE 路径。");
        if (!string.Equals(Path.GetFileName(exe), "ModularDiff.Gui.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("请使用发布版 ModularDiff.Gui.exe 安装右键菜单。");
        foreach (string target in Targets)
        {
            using var menu = Registry.CurrentUser.CreateSubKey(target);
            menu.SetValue("MUIVerb", "ModularDiff"); menu.SetValue("Icon", "\"" + exe + "\""); menu.SetValue("SubCommands", "");
            Add(menu, "01Left", "设为左侧 (Left)", exe, "--shell-left");
            Add(menu, "02Compare", "设为右侧并比较 (Right)", exe, "--shell-right");
        }
        Notify();
    }
    private static void Add(RegistryKey parent, string id, string title, string exe, string verb)
    {
        using var item = parent.CreateSubKey(@"shell\" + id); item.SetValue("MUIVerb", title); item.SetValue("MultiSelectModel", "Single");
        using var command = item.CreateSubKey("command"); command.SetValue("", "\"" + exe + "\" " + verb + " \"%1\"");
    }
    public static void Uninstall()
    {
        foreach (string target in Targets) Registry.CurrentUser.DeleteSubKeyTree(target, false);
        Registry.CurrentUser.DeleteSubKeyTree(StateKey, false); Notify();
    }
    public static void SelectLeft(string path)
    {
        path = ValidPath(path);
        using var state = Registry.CurrentUser.CreateSubKey(StateKey); state.SetValue("Left", path);
    }
    public static string[] CompareRight(string path)
    {
        string right = ValidPath(path);
        using var state = Registry.CurrentUser.OpenSubKey(StateKey);
        string left = state?.GetValue("Left") as string ?? throw new IOException("请先右键第一个文件或目录，选择 ModularDiff → 设为左侧 (Left)。");
        left = ValidPath(left);
        if (Directory.Exists(left) != Directory.Exists(right)) throw new IOException("左右必须同为文件或同为目录，请重新选择左侧。");
        return ["--left", left, "--right", right, "--auto-compare"];
    }
    private static string ValidPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new IOException("所选路径不存在或无法访问：" + full);
        return full;
    }
    private static void Notify() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
