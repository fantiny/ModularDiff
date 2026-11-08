using System.Windows;

namespace ModularDiff.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var args = e.Args;
            if (args.Length == 1 && args[0] is "--shell-install" or "--shell-uninstall")
            {
                if (args[0] == "--shell-install") ExplorerIntegration.Install(); else ExplorerIntegration.Uninstall();
                Shutdown(); return;
            }
            new MainWindow(args).Show();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "ModularDiff startup error"); Shutdown(2); }
    }
}
