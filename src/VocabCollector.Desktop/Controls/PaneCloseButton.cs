using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VocabCollector.Desktop.Controls;

/// <summary>A compact, theme-aware pane close action with native button input and automation.</summary>
public sealed class PaneCloseButton : Button
{
    public PaneCloseButton()
    {
        Width = Height = 30;
        MinWidth = MinHeight = 0;
        ToolTip = "关闭";
        AutomationProperties.SetName(this, "关闭");
        var geometry = Geometry.Parse("M 1,1 L 11,11 M 11,1 L 1,11");
        geometry.Freeze();
        var glyph = new Path
        {
            Width = 12, Height = 12, Data = geometry, Stretch = Stretch.Uniform,
            StrokeThickness = 1.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false, Focusable = false
        };
        glyph.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        Content = glyph;
        Style = CreateStyle();
    }

    private static Style CreateStyle()
    {
        var style = new Style(typeof(PaneCloseButton));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("InkSecondary")));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        var surface = new FrameworkElementFactory(typeof(Border), "CloseSurface");
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        surface.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentProperty));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        style.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(PaneCloseButton)) { VisualTree = surface }));
        foreach (var property in new[] { IsMouseOverProperty, IsKeyboardFocusedProperty })
        {
            var feedback = new Trigger { Property = property, Value = true };
            feedback.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Hover")));
            style.Triggers.Add(feedback);
        }
        var pressed = new Trigger { Property = IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("AccentSoft")));
        style.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .4));
        style.Triggers.Add(disabled);
        return style;
    }
}
