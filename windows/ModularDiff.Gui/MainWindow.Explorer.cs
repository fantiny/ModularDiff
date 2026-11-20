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
            ExplorerMenuStatus.Text = status.Current ? ModularDiff.Gui.Controls.Lang.Get("explorer_reg_current") : status.Any ? ModularDiff.Gui.Controls.Lang.Get("explorer_reg_other") : ModularDiff.Gui.Controls.Lang.Get("explorer_reg_none");
            RegisterMenuButton.Content = status.Any ? ModularDiff.Gui.Controls.Lang.Get("explorer_reregister") : ModularDiff.Gui.Controls.Lang.Get("register_menu");
            UnregisterMenuButton.IsEnabled = status.Any;
            bool supported = ExplorerIntegration.SupportsClassicMenu;
            bool classic = supported && ExplorerIntegration.IsClassicMenuConfigured();
            ClassicMenuStatus.Text = !supported ? ModularDiff.Gui.Controls.Lang.Get("explorer_win11_only") : classic ? ModularDiff.Gui.Controls.Lang.Get("explorer_classic_on") : ModularDiff.Gui.Controls.Lang.Get("explorer_classic_off");
            ClassicMenuButton.IsEnabled = supported && !classic;
            ModernMenuButton.IsEnabled = supported && classic;
        }
        catch (Exception ex) { ExplorerSettingsFeedback.Text = ModularDiff.Gui.Controls.Lang.Get("explorer_read_failed", ex.Message); }
    }
    private void RunExplorerSetting(Action action, string message)
    {
        try { action(); ExplorerSettingsFeedback.Text = StatusLabel.Text = message; }
        catch (Exception ex) { ExplorerSettingsFeedback.Text = StatusLabel.Text = ModularDiff.Gui.Controls.Lang.Get("status_settings_failed", ex.Message); }
        RefreshExplorerSettings();
    }
    private void ClassicMenuClick(object sender, RoutedEventArgs e) => RunExplorerSetting(
        () => ExplorerIntegration.SetClassicMenu(true), ModularDiff.Gui.Controls.Lang.Get("explorer_classic_set"));
    private void ModernMenuClick(object sender, RoutedEventArgs e) => RunExplorerSetting(
        () => ExplorerIntegration.SetClassicMenu(false), ModularDiff.Gui.Controls.Lang.Get("explorer_classic_cleared"));
    private void InstallExplorerClick(object sender, RoutedEventArgs e)
    {
        RunExplorerSetting(ExplorerIntegration.Install, ModularDiff.Gui.Controls.Lang.Get("explorer_installed"));
    }
    private void UninstallExplorerClick(object sender, RoutedEventArgs e)
    {
        RunExplorerSetting(ExplorerIntegration.Uninstall, ModularDiff.Gui.Controls.Lang.Get("explorer_uninstalled"));
    }
}
