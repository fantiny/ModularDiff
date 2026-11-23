using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace ModularDiff.Gui.Controls;

/// <summary>Address-bar behaviour for path boxes: the first click into a box that does not have
/// keyboard focus selects the whole text, so one click + Ctrl+C copies the full path; a click
/// while the box is already focused places the caret normally, and keyboard focus selects all.</summary>
public static class AddressBar
{
    public static readonly DependencyProperty SelectAllOnFocusProperty = DependencyProperty.RegisterAttached(
        "SelectAllOnFocus", typeof(bool), typeof(AddressBar), new PropertyMetadata(false, Changed));

    public static bool GetSelectAllOnFocus(DependencyObject element) => (bool)element.GetValue(SelectAllOnFocusProperty);
    public static void SetSelectAllOnFocus(DependencyObject element, bool value) => element.SetValue(SelectAllOnFocusProperty, value);

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not TextBox box) return;
        if ((bool)args.NewValue) { box.PreviewMouseLeftButtonDown += MouseDown; box.GotKeyboardFocus += Focused; }
        else { box.PreviewMouseLeftButtonDown -= MouseDown; box.GotKeyboardFocus -= Focused; }
    }

    private static void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.IsKeyboardFocusWithin || !box.IsEnabled || box.IsReadOnly) return;
        box.Focus();
        box.SelectAll();
        e.Handled = true;   // the box's own mouse handling must not collapse the selection into a caret
    }

    private static void Focused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && box.SelectionLength == 0) box.SelectAll();
    }
}

/// <summary>Gives a TextBox a localized Cut / Copy / Paste / Select all context menu (WPF's stock
/// text-box menu is always English). Items carry Loc keys, so the menu follows language switches.</summary>
public static class TextBoxMenu
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(TextBoxMenu), new PropertyMetadata(false, Changed));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not TextBox box) return;
        box.ContextMenu = (bool)args.NewValue ? Build() : null;
    }

    private static ContextMenu Build()
    {
        var menu = new ContextMenu();
        Add(menu, "menu_undo", ApplicationCommands.Undo);
        Add(menu, "menu_redo", ApplicationCommands.Redo);
        menu.Items.Add(new Separator());
        Add(menu, "menu_cut", ApplicationCommands.Cut);
        Add(menu, "menu_copy", ApplicationCommands.Copy);
        Add(menu, "menu_paste", ApplicationCommands.Paste);
        Add(menu, "menu_delete", box => box.SelectedText = "");
        menu.Items.Add(new Separator());
        Add(menu, "menu_select_all", box => box.SelectAll());
        return menu;
    }

    private static void Add(ContextMenu menu, string key, ICommand command)
    {
        var item = new MenuItem { Command = command };
        Wire(item, key);
        // Routed commands (and their enabled state) act on the text box that opened the menu.
        item.SetBinding(MenuItem.CommandTargetProperty, new Binding("PlacementTarget")
        { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ContextMenu), 1) });
        menu.Items.Add(item);
    }

    private static void Add(ContextMenu menu, string key, Action<TextBox> action)
    {
        var item = new MenuItem();
        Wire(item, key);
        item.Click += (_, _) => { if (menu.PlacementTarget is TextBox box) action(box); };
        menu.Items.Add(item);
    }

    private static void Wire(MenuItem item, string key)
    {
        Loc.SetTarget(item, LocTarget.Header);
        Loc.SetKey(item, key);
    }
}
