using System.Windows;
namespace ModularDiff.Gui;
public partial class MainWindow
{
    private void ExplorerSettingsOpened(object sender, EventArgs e) => RefreshExplorerSettings();
    private void RefreshExplorerSettings()
    {
        try
        {
            var status = ExplorerIntegration.RegistrationStatus();
            ExplorerMenuStatus.Text = status.Current ? "已注册当前程序" : status.Any ? "已有注册，可更新为当前程序" : "尚未注册";
            RegisterMenuButton.Content = status.Any ? "重新注册菜单" : "注册右键菜单";
            UnregisterMenuButton.IsEnabled = status.Any;
            bool supported = ExplorerIntegration.SupportsClassicMenu;
            bool classic = supported && ExplorerIntegration.IsClassicMenuConfigured();
            ClassicMenuStatus.Text = !supported ? "此选项仅适用于 Windows 11" : classic ? "已设置经典模式（实际效果取决于系统支持）" : "未设置经典模式";
            ClassicMenuButton.IsEnabled = supported && !classic;
            ModernMenuButton.IsEnabled = supported && classic;
        }
        catch (Exception ex) { ExplorerSettingsFeedback.Text = "读取设置失败：" + ex.Message; }
    }
    private void RunExplorerSetting(Action action, string message)
    {
        try { action(); ExplorerSettingsFeedback.Text = StatusLabel.Text = message; }
        catch (Exception ex) { ExplorerSettingsFeedback.Text = StatusLabel.Text = "设置失败：" + ex.Message; }
        RefreshExplorerSettings();
    }
    private void ClassicMenuClick(object sender, RoutedEventArgs e) => RunExplorerSetting(
        () => ExplorerIntegration.SetClassicMenu(true), "已写入经典菜单设置。请在任务管理器中重启 Windows 资源管理器，或注销后重新登录；部分系统版本可能不支持。");
    private void ModernMenuClick(object sender, RoutedEventArgs e) => RunExplorerSetting(
        () => ExplorerIntegration.SetClassicMenu(false), "已移除经典菜单设置。重启资源管理器或注销后恢复默认菜单。");
    private void InstallExplorerClick(object sender, RoutedEventArgs e)
    {
        RunExplorerSetting(ExplorerIntegration.Install, "已注册当前用户右键菜单；Windows 11 默认在显示更多选项中显示。");
    }
    private void UninstallExplorerClick(object sender, RoutedEventArgs e)
    {
        RunExplorerSetting(ExplorerIntegration.Uninstall, "已取消注册 ModularDiff 右键菜单。");
    }
}
