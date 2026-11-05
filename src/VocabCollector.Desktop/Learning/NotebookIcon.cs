using System.Windows;
using System.Windows.Media;

namespace VocabCollector.Desktop.Learning;

public sealed class NotebookIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(NotebookIcon), new FrameworkPropertyMetadata("file", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(NotebookIcon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public NotebookIcon() { Width = 18; Height = 18; SnapsToDevicePixels = true; SetResourceReference(ForegroundProperty, "NotebookMuted"); }
    private static readonly IReadOnlyDictionary<string, Geometry> Shapes = new Dictionary<string, string>
    {
        ["file"]="M5,3 L14,3 20,9 20,21 5,21 Z M14,3 L14,9 20,9",
        ["folder"]="M3,7 L3,20 21,20 21,7 12,7 10,4 3,4 Z",
        ["search"]="M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M14.5,14.5 L21,21",
        ["plus"]="M12,5 L12,19 M5,12 L19,12",
        ["close"]="M6,6 L18,18 M18,6 L6,18",
        ["chevron-right"]="M9,5 L16,12 9,19",
        ["chevron-down"]="M5,9 L12,16 19,9",
        ["back"]="M15,5 L8,12 15,19",
        ["forward"]="M9,5 L16,12 9,19",
        ["panel-left"]="M3,4 L21,4 21,20 3,20 Z M9,4 L9,20",
        ["panel-right"]="M3,4 L21,4 21,20 3,20 Z M15,4 L15,20",
        ["edit"]="M4,16 L15,5 19,9 8,20 4,20 Z M14,6 L18,10",
        ["book"]="M12,5 C8,3 5,3 2,5 L2,20 C5,18 8,18 12,20 C16,18 19,18 22,20 L22,5 C19,3 16,3 12,5 Z M12,5 L12,20",
        ["link"]="M10,14 L14,10 M8,16 L6,18 C2,22 -2,16 2,12 L7,7 C10,4 14,6 14,9 M16,8 L18,6 C22,2 26,8 22,12 L17,17 C14,20 10,18 10,15",
        ["list"]="M8,6 L21,6 M8,12 L21,12 M8,18 L21,18 M3,6 L4,6 M3,12 L4,12 M3,18 L4,18",
        ["more"]="M5,12 L5.1,12 M12,12 L12.1,12 M19,12 L19.1,12",
        ["check"]="M4,12 L9,17 20,6",
        ["moon"]="M20,15 A9,9 0 1 1 9,4 A7,7 0 0 0 20,15 Z",
        ["sun"]="M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12 M12,1 L12,4 M12,20 L12,23 M1,12 L4,12 M20,12 L23,12 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4",
        ["settings"]="M9,3 L15,3 16,7 20,8 22,13 18,16 18,21 12,23 8,19 4,18 2,13 6,9 6,5 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
        ["refresh"]="M20,9 A8,8 0 1 0 20,16 M20,3 L20,9 14,9",
        ["trash"]="M4,6 L20,6 M9,6 L9,3 15,3 15,6 M6,6 L7,21 17,21 18,6 M10,10 L10,17 M14,10 L14,17",
        ["undo"]="M9,4 L3,10 9,16 M3,10 L15,10 C23,10 23,21 15,21",
        ["sort"]="M4,6 L20,6 M4,12 L15,12 M4,18 L10,18",
        ["move"]="M3,5 L10,5 12,8 21,8 21,20 3,20 Z M8,14 L16,14 M13,11 L16,14 13,17",
        ["split"]="M3,4 L21,4 21,20 3,20 Z M12,4 L12,20",
        ["bookmark"]="M6,3 L18,3 18,22 12,17 6,22 Z",
        ["graph"]="M7,6 L17,10 10,19 7,6 M9,6 A2,2 0 1 1 5,6 A2,2 0 1 1 9,6 M19,10 A2,2 0 1 1 15,10 A2,2 0 1 1 19,10 M12,19 A2,2 0 1 1 8,19 A2,2 0 1 1 12,19"
    }.ToDictionary(p => p.Key, p => Geometry.Parse(p.Value));
    protected override void OnRender(DrawingContext context)
    {
        if (!Shapes.TryGetValue(Kind, out var geometry)) geometry = Shapes["file"];
        context.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        context.DrawGeometry(null, new Pen(Foreground, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
        context.Pop();
    }
}
