using Avalonia.Styling;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
namespace ModularDiff.Desktop;
internal static class Program
{
    [STAThread] public static void Main(string[] args) => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
public sealed class App : Application
{
    public override void Initialize() { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; Styles.Add(new FluentTheme()); var rowStyle = new Avalonia.Styling.Style(x => x.OfType<Avalonia.Controls.ListBoxItem>()); rowStyle.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.ContentControl.HorizontalContentAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Stretch)); rowStyle.Setters.Add(new Setter(Avalonia.Layout.Layoutable.MinHeightProperty, 22d)); rowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(0))); Styles.Add(rowStyle); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args ?? []);
        base.OnFrameworkInitializationCompleted();
    }
}
