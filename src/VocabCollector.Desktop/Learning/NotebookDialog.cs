using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace VocabCollector.Desktop.Learning;

public sealed record NotebookPickerItem(string Title, string Detail, object Value);

public sealed class NotebookPicker : Window
{
    private readonly TextBox _query = new() { Name = "NotebookPickerQuery", Margin = new(14, 12, 14, 8) };
    private readonly ListBox _list = new() { Name = "NotebookPickerResults", DisplayMemberPath = nameof(NotebookPickerItem.Title), Margin = new(8, 0, 8, 0) };
    private readonly IReadOnlyList<NotebookPickerItem> _items;
    private readonly Func<NotebookPickerItem, string, bool> _filter;
    private readonly bool _canCreate;
    public NotebookPickerItem? Selected { get; private set; }
    public bool OpenInNewTab { get; private set; }
    public bool CreateNew { get; private set; }
    public string Query => _query.Text.Trim();

    public NotebookPicker(FrameworkElement host, string title, IReadOnlyList<NotebookPickerItem> items,
        Func<NotebookPickerItem, string, bool>? filter = null, bool canCreate = false)
    {
        _items = items; _filter = filter ?? ((item, query) => item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)); _canCreate = canCreate;
        Title = title; Width = 600; Height = 410; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None;
        Owner = Window.GetWindow(host); WindowStartupLocation = Owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        NotebookDialog.ApplyResources(this, host);
        SetResourceReference(BackgroundProperty, "NotebookSurface"); SetResourceReference(ForegroundProperty, "NotebookText");
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = title, Margin = new(16, 14, 16, 0), FontWeight = FontWeights.SemiBold });
        Grid.SetRow(_query, 1); panel.Children.Add(_query); Grid.SetRow(_list, 2); panel.Children.Add(_list);
        var template = new DataTemplate(typeof(NotebookPickerItem));
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var titleText = new FrameworkElementFactory(typeof(TextBlock)); titleText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(NotebookPickerItem.Title))); titleText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); stack.AppendChild(titleText);
        var detail = new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(NotebookPickerItem.Detail))); detail.SetValue(TextBlock.FontSizeProperty, 12.0); detail.SetResourceReference(TextBlock.ForegroundProperty, "NotebookMuted"); detail.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0)); detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); stack.AppendChild(detail); template.VisualTree = stack;
        _list.DisplayMemberPath = ""; _list.ItemTemplate = template;
        var hint = new TextBlock { Text = canCreate ? "↑ ↓ 选择    Enter 打开    Ctrl+Enter 新标签    无匹配时 Enter 新建    Esc 关闭" : "↑ ↓ 选择    Enter 执行    Esc 关闭", FontSize = 12, Margin = new(16, 10, 16, 12) }; hint.SetResourceReference(TextBlock.ForegroundProperty, "NotebookMuted"); Grid.SetRow(hint, 3); panel.Children.Add(hint);
        var border = new Border { Child = panel, BorderThickness = new(1), CornerRadius = new(8) }; border.SetResourceReference(Border.BackgroundProperty, "NotebookSurface"); border.SetResourceReference(Border.BorderBrushProperty, "NotebookBorder"); Content = border;
        _query.TextChanged += (_, _) => Filter();
        _list.MouseDoubleClick += (_, _) => Accept();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { Close(); args.Handled = true; }
            else if (args.Key == Key.Enter) { Accept(); args.Handled = true; }
            else if (args.Key is Key.Down or Key.Up && _list.Items.Count > 0)
            { _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + (args.Key == Key.Down ? 1 : -1), 0, _list.Items.Count - 1); _list.ScrollIntoView(_list.SelectedItem); args.Handled = true; }
        };
        Loaded += (_, _) => { Filter(); _query.Focus(); };
    }
    private void Filter()
    { _list.ItemsSource = _items.Where(i => _filter(i, Query)).Take(100).ToArray(); _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1; }
    private void Accept()
    {
        Selected = _list.SelectedItem as NotebookPickerItem;
        CreateNew = Selected == null && _canCreate && Query.Length > 0;
        if (Selected == null && !CreateNew) return;
        OpenInNewTab = Keyboard.Modifiers.HasFlag(ModifierKeys.Control); DialogResult = true;
    }
}

public static class NotebookDialog
{
    internal static void ApplyResources(Window window, FrameworkElement host)
    {
        window.Resources.MergedDictionaries.Add(host.Resources);
        foreach (var (key, fallback) in new[] { ("NotebookSurface", "Sheet"), ("NotebookText", "Ink"),
            ("NotebookMuted", "InkSecondary"), ("NotebookBorder", "Line"), ("NotebookAction", "OutlineAction") })
            if (host.TryFindResource(key) is null && host.TryFindResource(fallback) is { } value) window.Resources[key] = value;
    }
    public static string? Prompt(FrameworkElement host, string title, string initial, string description)
    {
        var window = Create(host, title, 520, 218);
        var grid = new Grid { Margin = new(22) }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        var label = new TextBlock { Text = description, Margin = new(0, 10, 0, 10), TextWrapping = TextWrapping.Wrap }; label.SetResourceReference(TextBlock.ForegroundProperty, "NotebookMuted"); Grid.SetRow(label, 1); grid.Children.Add(label);
        var input = new TextBox { Name = "NotebookNameBox", Text = initial, MinHeight = 34 }; Grid.SetRow(input, 2); grid.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 68, Margin = new(0, 0, 8, 0) }; cancel.SetResourceReference(FrameworkElement.StyleProperty, "NotebookAction"); cancel.Click += (_, _) => window.Close();
        var confirm = new Button { Name = "NotebookConfirmButton", Content = "确定", IsDefault = true, MinWidth = 68 }; confirm.SetResourceReference(FrameworkElement.StyleProperty, "NotebookAction"); confirm.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) window.DialogResult = true; }; actions.Children.Add(cancel); actions.Children.Add(confirm); Grid.SetRow(actions, 3); grid.Children.Add(actions);
        window.Content = grid; window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
    public static bool Confirm(FrameworkElement host, string title, string description)
    {
        var window = Create(host, title, 480, 198);
        var panel = new StackPanel { Margin = new(24) }; panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 17 }); panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 18) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 10, 0), MinWidth = 68 }; cancel.SetResourceReference(FrameworkElement.StyleProperty, "NotebookAction"); cancel.Click += (_, _) => window.Close();
        var confirm = new Button { Name = "NotebookConfirmButton", Content = "移入回收站", IsDefault = true, MinWidth = 108 }; confirm.SetResourceReference(FrameworkElement.StyleProperty, "NotebookAction"); confirm.Click += (_, _) => window.DialogResult = true; buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons); window.Content = panel;
        return window.ShowDialog() == true;
    }
    private static Window Create(FrameworkElement host, string title, double width, double height)
    {
        var window = new Window { Title = title, Width = width, MinHeight = height, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Owner = Window.GetWindow(host), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ApplyResources(window, host); window.SetResourceReference(Window.BackgroundProperty, "NotebookSurface"); window.SetResourceReference(Window.ForegroundProperty, "NotebookText"); window.SourceInitialized += (_, _) => TitleBarTheme.Apply(window); return window;
    }
}
