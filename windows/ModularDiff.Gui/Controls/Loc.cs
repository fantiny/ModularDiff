using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ModularDiff.Gui.Controls;

/// <summary>Where the localized value of <see cref="Loc.Key"/> is written. <see cref="Default"/>
/// infers the property from the element type (window title, column header, text, content).</summary>
public enum LocTarget { Default, Text, Content, ToolTip, Title, Header, AutomationName }

/// <summary>Declarative localization for XAML: an element declares its language-pack key in markup
/// (<c>ui:Loc.Key="save_left_label"</c>, optional <c>ui:Loc.Target</c>, plus <c>ui:Loc.TipKey</c>
/// for tooltips) and <see cref="Apply"/> writes the translated value. XAML therefore carries no
/// human-language text at all — every visible string is a key, so nothing can be missed by a
/// hand-maintained list of assignments (the way several labels stayed Chinese in English mode).</summary>
public static class Loc
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Loc), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(LocTarget), typeof(Loc), new PropertyMetadata(LocTarget.Default, Changed));
    public static readonly DependencyProperty TipKeyProperty = DependencyProperty.RegisterAttached(
        "TipKey", typeof(string), typeof(Loc), new PropertyMetadata(null, Changed));

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);
    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);
    public static LocTarget GetTarget(DependencyObject element) => (LocTarget)element.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject element, LocTarget value) => element.SetValue(TargetProperty, value);
    public static string? GetTipKey(DependencyObject element) => (string?)element.GetValue(TipKeyProperty);
    public static void SetTipKey(DependencyObject element, string? value) => element.SetValue(TipKeyProperty, value);

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        // XAML writes Key and Target as two separate attributes: acting on the first one alone would
        // apply an incomplete declaration (a Key meant for AutomationName landed in Content, wiping
        // the real content). Defer to the next dispatcher pass, by which time the declaration — and
        // the window — is fully loaded; LocalizeUI also applies the whole tree synchronously.
        element.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => ApplyTo(element)));
    }

    /// <summary>Applies every declared key under <paramref name="root"/>: its logical tree (popup
    /// children included) plus the columns of any DataGrid. Called on load and on language switch.</summary>
    public static void Apply(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            ApplyTo(current);
            if (current is DataGrid grid)
                foreach (var column in grid.Columns) ApplyTo(column);
            if (current is Popup { Child: DependencyObject child }) pending.Push(child);
            // Context menus assigned in code (their items carry Loc keys) are their own logical root.
            if (current is FrameworkElement { ContextMenu: { } menu }) pending.Push(menu);
            if (current is FrameworkElement or FrameworkContentElement)
                foreach (object item in LogicalTreeHelper.GetChildren(current))
                    if (item is DependencyObject dependency) pending.Push(dependency);
        }
    }

    private static void ApplyTo(DependencyObject element)
    {
        if (GetTipKey(element) is string tipKey && element is FrameworkElement tipOwner)
            tipOwner.ToolTip = Lang.Get(tipKey);
        if (GetKey(element) is not string key) return;
        string value = Lang.Get(key);
        switch (GetTarget(element))
        {
            case LocTarget.ToolTip when element is FrameworkElement toolTipOwner:
                toolTipOwner.ToolTip = value; break;
            case LocTarget.Title when element is Window window:
                window.Title = value; break;
            case LocTarget.Header when element is HeaderedItemsControl headered:
                headered.Header = value; break;
            case LocTarget.Header when element is DataGridColumn column:
                column.Header = value; break;
            case LocTarget.Content when element is ContentControl content:
                content.Content = value; break;
            case LocTarget.Text when element is TextBlock text:
                text.Text = value; break;
            case LocTarget.AutomationName when element is FrameworkElement named:
                AutomationProperties.SetName(named, value); break;
            case LocTarget.Default:
                if (element is Window defaultWindow) defaultWindow.Title = value;
                else if (element is DataGridColumn defaultColumn) defaultColumn.Header = value;
                else if (element is TextBlock defaultText) defaultText.Text = value;
                else if (element is ContentControl defaultContent) defaultContent.Content = value;
                break;
        }
    }
}

/// <summary>Binding converter for plugin-provided display names: routes them through
/// <see cref="Lang.Text"/> so plugin strings follow the UI language too.</summary>
public sealed class LocTextConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        Lang.Text(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
