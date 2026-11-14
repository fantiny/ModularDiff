using Avalonia.Styling;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
namespace ModularDiff.Desktop;
internal static class Program
{
    [STAThread] public static void Main(string[] args) => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
public sealed class App : Application
{
    /// <summary>Shared plugin catalog: loaded once at startup, used by all windows.</summary>
    public static ModularDiff.Core.PluginCatalog Plugins { get; private set; } = null!;

    public override void Initialize() { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; Styles.Add(new FluentTheme()); var rowStyle = new Avalonia.Styling.Style(x => x.OfType<Avalonia.Controls.ListBoxItem>()); rowStyle.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.ContentControl.HorizontalContentAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Stretch)); rowStyle.Setters.Add(new Setter(Avalonia.Layout.Layoutable.MinHeightProperty, 22d)); rowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(0))); Styles.Add(rowStyle); var selectedStyle = new Avalonia.Styling.Style(x => x.OfType<Avalonia.Controls.ListBoxItem>().Class(":selected")); selectedStyle.Setters.Add(new Setter(Avalonia.Controls.ListBoxItem.BackgroundProperty, Avalonia.Media.Brush.Parse("#D9E8FF"))); Styles.Add(selectedStyle); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Shared plugin catalog: resolve root once, load once, share across all windows.
            var args = desktop.Args ?? [];
            string? pluginArg = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--plugins") { pluginArg = args[i + 1]; break; }
            Plugins = ModularDiff.Core.PluginCatalog.Load(pluginArg ?? ResolvePluginRoot());

            // 单实例多窗口：Finder/服务重复调用时把参数转发给已运行实例,由它开新窗口(macOS 的 open --args 不会送达已运行应用)。
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            if (SingleInstance.TryForward(args)) { Environment.Exit(0); return; }
            SingleInstance.Start(ipcArgs => Dispatcher.UIThread.Post(() => new MainWindow(ipcArgs).Show()));
            desktop.MainWindow = new MainWindow(args);
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static string ResolvePluginRoot()
    {
        var candidates = new[] { "../plugins", "../Resources/plugins", "../PlugIns" }
            .Select(rel => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rel)));
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates.First();
    }
}
internal static class SingleInstance
{
    private static string SocketPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularDiff", "ipc.sock");
    public static bool TryForward(string[] args)
    {
        if (!args.Contains("--left") && !args.Contains("--right")) return false;
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return false;
        try
        {
            using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            var pending = client.BeginConnect(new System.Net.Sockets.UnixDomainSocketEndPoint(SocketPath), null, null);
            if (!pending.AsyncWaitHandle.WaitOne(700)) return false;
            client.EndConnect(pending);
            client.Send(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(args)));
            return true;
        }
        catch { return false; }
    }
    public static void Start(Action<string[]> onArgs)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SocketPath)!);
            try { File.Delete(SocketPath); } catch { }
            var server = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            server.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(SocketPath));
            server.Listen(4);
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    System.Net.Sockets.Socket socket;
                    try { socket = await server.AcceptAsync(); } catch { return; }
                    _ = Handle(socket, onArgs);
                }
            });
        }
        catch { }
    }
    private static async Task Handle(System.Net.Sockets.Socket socket, Action<string[]> onArgs)
    {
        try
        {
            using (socket)
            {
                var buffer = new byte[65536]; using var stream = new MemoryStream();
                int read; while ((read = await socket.ReceiveAsync(buffer)) > 0) { stream.Write(buffer, 0, read); if (stream.Length > 1 << 20) break; }
                var args = System.Text.Json.JsonSerializer.Deserialize<string[]>(stream.ToArray()) ?? [];
                if (args.Length > 0) onArgs(args);
            }
        }
        catch { }
    }
}
