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
            if (args.Length > 0 && args[0] is "--shell-left" or "--shell-right")
            {
                if (args.Length != 2) throw new ArgumentException("资源管理器集成每次仅接受一个文件或目录。");
                if (args[0] == "--shell-left") { ExplorerIntegration.SelectLeft(args[1]); Shutdown(); return; }
                args = ExplorerIntegration.CompareRight(args[1]);
            }
            new MainWindow(args).Show();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "ModularDiff startup error"); Shutdown(2); }
    }
}
