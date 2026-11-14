using System.IO;
using System.Windows;
using ModularDiff.Core;

namespace ModularDiff.Gui;

public partial class App : Application
{
    /// <summary>Shared plugin catalog: loaded once at startup, used by all windows
    /// (MainWindow instances, MergeWindow) — no per-window duplicate loads.</summary>
    public static PluginCatalog Plugins { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var args = e.Args;
            // Resolve the plugin root once: --plugins argument or the default beside the host.
            string? pluginArg = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--plugins") { pluginArg = args[i + 1]; break; }
            Plugins = PluginCatalog.Load(pluginArg ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "plugins")));

            if (args.Length == 1 && args[0] is "--shell-install" or "--shell-uninstall")
            {
                if (args[0] == "--shell-install") ExplorerIntegration.Install(); else ExplorerIntegration.Uninstall();
                Shutdown(); return;
            }
            if (args.Length == 4 && args[0] == "--merge")
            {
                new MergeWindow(args[1], args[2], args[3]).Show();
                return;
            }
            new MainWindow(args).Show();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "ModularDiff startup error"); Shutdown(2); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Plugins?.Dispose();
        base.OnExit(e);
    }
}
