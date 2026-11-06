using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VocabCollector.Desktop;

/// <summary>Move both keyboard and logical focus off an input when the user clicks a noninteractive blank area.</summary>
public static class BlankAreaFocus
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(BlankAreaFocus), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)e.NewValue) element.PreviewMouseDown += OnMouseDown;
        else element.PreviewMouseDown -= OnMouseDown;
    }
    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Keyboard.FocusedElement is not (TextBoxBase or PasswordBox)
            || sender is not FrameworkElement root) return;
        for (var item = e.OriginalSource as DependencyObject; item is not null && item != root; item = Parent(item))
            if (item is TextBoxBase or PasswordBox or ButtonBase or Hyperlink or TreeViewItem or ListBoxItem
                or MenuItem or ScrollBar or Thumb or Slider or ComboBox) return;
        root.Focus(); // Keyboard.ClearFocus alone leaves the input's logical focus/caret behind.
    }
    private static DependencyObject? Parent(DependencyObject item) => item switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(item),
        FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
        _ => LogicalTreeHelper.GetParent(item)
    };
}
