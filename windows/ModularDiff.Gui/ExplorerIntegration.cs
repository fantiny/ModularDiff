using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ModularDiff.Gui;

internal static class ExplorerIntegration
{
    private const string StateKey = @"Software\ModularDiff\Explorer";
    private const string Clsid = "{144097CE-5032-445B-9864-8F38CB314AF5}"; // keep in sync with ShellExt Com.ClsidMenu
    private const string HandlerName = "ModularDiff";
    private const string ClassicKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";
    private static readonly string[] HandlerTargets =
        [@"Software\Classes\*\shellex\ContextMenuHandlers", @"Software\Classes\Directory\shellex\ContextMenuHandlers"];
    // Static verbs from earlier builds; still removed so upgrades come clean.
    private static readonly string[] LegacyVerbKeys =
    [
        @"Software\Classes\*\shell\ModularDiffCompare", @"Software\Classes\*\shell\ModularDiffSelectLeft",
        @"Software\Classes\*\shell\ModularDiffCompareTo", @"Software\Classes\*\shell\ModularDiff",
        @"Software\Classes\Directory\shell\ModularDiffCompare", @"Software\Classes\Directory\shell\ModularDiffSelectLeft",
        @"Software\Classes\Directory\shell\ModularDiffCompareTo", @"Software\Classes\Directory\shell\ModularDiff",
    ];

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
        string? server = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\" + Clsid + @"\InprocServer32")?.GetValue("") as string;
        any |= server is not null;
        current &= string.Equals(server, HandlerDllPath(), StringComparison.OrdinalIgnoreCase);
        foreach (string target in HandlerTargets)
        {
            string? registered = Registry.CurrentUser.OpenSubKey(target + @"\" + HandlerName)?.GetValue("") as string;
            any |= registered is not null;
            current &= string.Equals(registered, Clsid, StringComparison.OrdinalIgnoreCase);
        }
        string? exe = Registry.CurrentUser.OpenSubKey(StateKey)?.GetValue("GuiExePath") as string;
        any |= exe is not null;
        current &= string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        foreach (string legacy in LegacyVerbKeys) any |= Registry.CurrentUser.OpenSubKey(legacy) is not null;
        return (any, current);
    }

    public static void Install()
    {
        string exe = Environment.ProcessPath ?? throw new IOException("无法确定 EXE 路径。");
        if (!string.Equals(Path.GetFileName(exe), "ModularDiff.Gui.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("请使用发布版 ModularDiff.Gui.exe 安装右键菜单。");
        string dll = HandlerDllPath();
        if (!File.Exists(dll)) throw new IOException("未找到右键菜单组件 ModularDiff.ShellExt.dll，请重新构建（build.ps1）。");
        using (Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + Clsid + @"\InprocServer32"))
        {
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Classes\CLSID\" + Clsid + @"\InprocServer32", "", dll);
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Classes\CLSID\" + Clsid + @"\InprocServer32", "ThreadingModel", "Apartment");
        }
        foreach (string target in HandlerTargets)
            Registry.SetValue(@"HKEY_CURRENT_USER\" + target + @"\" + HandlerName, "", Clsid);
        Registry.SetValue(@"HKEY_CURRENT_USER\" + StateKey, "GuiExePath", exe);
        foreach (string legacy in LegacyVerbKeys) Registry.CurrentUser.DeleteSubKeyTree(legacy, false);
        Notify();
    }

    public static void Uninstall()
    {
        foreach (string target in HandlerTargets)
            Registry.CurrentUser.DeleteSubKeyTree(target + @"\" + HandlerName, false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + Clsid, false);
        foreach (string legacy in LegacyVerbKeys) Registry.CurrentUser.DeleteSubKeyTree(legacy, false);
        Registry.CurrentUser.DeleteSubKeyTree(StateKey, false);
        Notify();
    }

    private static string HandlerDllPath() => Path.Combine(AppContext.BaseDirectory, "ModularDiff.ShellExt.dll");

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    private static void Notify() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
