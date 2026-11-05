using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Core.Library;

namespace VocabCollector.Desktop.Learning;

public sealed class NotebookGraphView : UserControl, IDisposable
{
    private const int NodeLimit = 300, EdgeLimit = 1500, LocalScanLimit = 3000;
    private readonly TextBox _search = new() { Name = "GraphSearchBox", MinWidth = 100, ToolTip = "筛选图中笔记，支持全文搜索语法" };
    private readonly CheckBox _local = new() { Name = "GraphLocalMode", Content = "局部关系", VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 8, 0), IsEnabled = false };
    private readonly Button _open = new() { Name = "GraphOpenNoteButton", Content = "打开笔记", IsEnabled = false, Margin = new(6, 0, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(12, 8, 12, 10), Text = "选择关系图中的节点查看笔记。" };
    private readonly GraphSurface _surface = new() { Name = "GraphCanvas", ClipToBounds = true, Focusable = true };
    private readonly DispatcherTimer _filterTimer;
    private LibraryCatalog? _catalog;
    private IReadOnlyList<LibraryNote> _notes = [];
    private LibraryNote? _focus;
    private CancellationTokenSource? _work;
    private int _generation;
    private bool _disposed, _busy, _updatingLocal;

    public NotebookGraphView()
    {
        SetResourceReference(BackgroundProperty, "NotebookBackground");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "NotebookMuted");
        _local.SetResourceReference(Control.ForegroundProperty, "NotebookText");
        _open.SetResourceReference(StyleProperty, "NotebookAction");
        AutomationProperties.SetName(_search, "筛选关系图");
        AutomationProperties.SetName(_surface, "笔记关系图，拖动节点，滚轮缩放，拖动背景平移，双击或按 Enter 打开选中笔记");
        var reset = new Button { Content = new NotebookIcon { Kind = "refresh" }, ToolTip = "重置视图 (F)", Margin = new(6, 0, 0, 0) };
        reset.SetResourceReference(StyleProperty, "NotebookIconButton");
        reset.Click += (_, _) => _surface.Fit();
        var toolbar = new DockPanel { Margin = new(12) };
        DockPanel.SetDock(_open, Dock.Right); toolbar.Children.Add(_open);
        DockPanel.SetDock(reset, Dock.Right); toolbar.Children.Add(reset);
        DockPanel.SetDock(_local, Dock.Right); toolbar.Children.Add(_local);
        toolbar.Children.Add(_search);
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(toolbar);
        Grid.SetRow(_surface, 1); grid.Children.Add(_surface);
        Grid.SetRow(_status, 2); grid.Children.Add(_status);
        Content = grid;
        _filterTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(220) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); StartLayout(fit: true); };
        _search.TextChanged += (_, _) => { if (!_disposed) { _filterTimer.Stop(); _filterTimer.Start(); } };
        _local.Checked += LocalChanged; _local.Unchecked += LocalChanged;
        _open.Click += (_, _) => OpenFocused();
        _surface.Selected += note => SetFocus(note, rebuild: false);
        _surface.OpenRequested += note => { SetFocus(note, rebuild: false); OpenFocused(); };
        _surface.SelectionSettled += () => { if (LocalMode) StartLayout(fit: true); };
    }

    public event EventHandler<LibraryNote>? NoteRequested;
    public LibraryNote? FocusNote { get => _focus; set => SetFocus(value, rebuild: true); }
    public bool LocalMode
    {
        get => _local.IsChecked == true;
        set
        {
            if (_disposed || LocalMode == value) return;
            _updatingLocal = true;
            _local.IsChecked = value && _focus != null;
            _updatingLocal = false;
            StartLayout(fit: true);
        }
    }
    public string Filter
    {
        get => _search.Text;
        set
        {
            if (_disposed || _search.Text == value) return;
            _search.Text = value ?? "";
            _filterTimer.Stop();
            StartLayout(fit: true);
        }
    }
    public bool IsBusy => _busy || _filterTimer.IsEnabled;
    public string Status => _status.Text;
    public int DisplayedNodeCount => _surface.Model.Nodes.Count;
    public int DisplayedEdgeCount => _surface.Model.Edges.Count;

    public void Configure(LibraryCatalog catalog, IReadOnlyList<LibraryNote> notes)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(notes);
        _catalog = catalog; _notes = notes.ToArray();
        SetFocus(_focus is null ? null : notes.FirstOrDefault(note => note.Id == _focus.Id), rebuild: false);
        StartLayout(fit: DisplayedNodeCount == 0);
    }
    private void SetFocus(LibraryNote? note, bool rebuild)
    {
        if (_disposed) return;
        var changed = _focus?.Id != note?.Id;
        _focus = note; _open.IsEnabled = note != null; _local.IsEnabled = note != null;
        _surface.SelectedId = note?.Id; _surface.InvalidateVisual();
        if (note is null && LocalMode) LocalMode = false;
        else if (changed && rebuild && LocalMode) StartLayout(fit: true);
    }
    private void LocalChanged(object sender, RoutedEventArgs args) { if (!_updatingLocal) StartLayout(fit: true); }
    private void OpenFocused() { if (!_disposed && _focus is { } note) NoteRequested?.Invoke(this, note); }

    private void StartLayout(bool fit)
    {
        if (_disposed || _catalog is null) return;
        _filterTimer.Stop();
        _work?.Cancel(); _work?.Dispose();
        var cancellation = _work = new();
        var token = cancellation.Token;
        var generation = ++_generation;
        var catalog = _catalog; var notes = _notes; var query = Filter; var focus = _focus; var local = LocalMode;
        var positions = _surface.Model.Nodes.ToDictionary(node => node.Note.Id, node => node.Position);
        _busy = true; _status.Text = "正在计算关系图…";
        _ = BuildAndDisplayAsync();

        async Task BuildAndDisplayAsync()
        {
            try
            {
                var model = await Task.Run(() => Build(catalog, notes, query, focus, local, positions, token), token).ConfigureAwait(false);
                if (token.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || generation != _generation) return;
                    _surface.SetModel(model, fit);
                    _status.Text = model.Message + "  ·  拖动节点 / 滚轮缩放 / 拖动背景平移 / 双击打开";
                    _busy = false;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (Dispatcher.HasShutdownStarted) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || generation != _generation) return;
                    _status.Text = "关系图暂时无法显示：" + error.Message; _busy = false;
                });
            }
        }
    }

    private static GraphModel Build(LibraryCatalog catalog, IReadOnlyList<LibraryNote> allNotes, string query,
        LibraryNote? focus, bool local, IReadOnlyDictionary<string, Point> positions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var candidates = allNotes.DistinctBy(note => note.Id).ToArray();
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!NotebookSearch.TrySearch(allNotes, query, out var hits, out var error)) return new([], [], error);
            candidates = hits.Select(hit => hit.Note).DistinctBy(note => note.Id).ToArray();
        }
        var graph = new LibraryGraph(catalog, allNotes);
        var outgoing = new Dictionary<string, IReadOnlyList<LibraryNote>>();
        IReadOnlyList<LibraryNote> Out(LibraryNote note)
        {
            token.ThrowIfCancellationRequested();
            if (!outgoing.TryGetValue(note.Id, out var links)) outgoing[note.Id] = links = graph.Outgoing(note);
            token.ThrowIfCancellationRequested(); return links;
        }
        var partialLocalScan = false;
        if (local && focus != null)
        {
            var adjacent = Out(focus).Select(note => note.Id).ToHashSet();
            foreach (var source in candidates.Take(LocalScanLimit))
                if (Out(source).Any(target => target.Id == focus.Id)) adjacent.Add(source.Id);
            partialLocalScan = candidates.Length > LocalScanLimit;
            candidates = new[] { focus }.Concat(candidates.Where(note => adjacent.Contains(note.Id))).DistinctBy(note => note.Id).ToArray();
        }
        if (focus != null) candidates = candidates.OrderByDescending(note => note.Id == focus.Id).ToArray();
        var total = candidates.Length;
        var nodes = candidates.Take(NodeLimit).Select((note, index) => new GraphNode(note,
            positions.GetValueOrDefault(note.Id, new(Math.Cos(index * 2.39996) * Math.Sqrt(index) * 47, Math.Sin(index * 2.39996) * Math.Sqrt(index) * 47)))).ToList();
        var lookup = nodes.ToDictionary(node => node.Note.Id);
        var edges = new List<GraphEdge>();
        var seen = new HashSet<(string, string)>(); var moreEdges = false;
        foreach (var source in nodes)
            foreach (var target in Out(source.Note))
            {
                if (target.Id == source.Note.Id || !lookup.TryGetValue(target.Id, out var destination) || !seen.Add((source.Note.Id, target.Id))) continue;
                if (edges.Count == EdgeLimit) { moreEdges = true; break; }
                edges.Add(new(source, destination)); source.Degree++; destination.Degree++;
            }
        Layout(nodes, edges, token);
        var message = $"{(local ? "局部关系" : "关系图")} · {nodes.Count} 个节点 · {edges.Count} 条出链";
        if (total > NodeLimit) message += $" · 当前显示 {NodeLimit}/{total} 篇，请使用筛选缩小范围";
        if (moreEdges) message += $" · 连线最多显示 {EdgeLimit} 条，请使用筛选";
        if (partialLocalScan) message += $" · 局部反链仅检查前 {LocalScanLimit} 篇，请先筛选";
        if (nodes.Count == 0) message = "没有匹配的笔记，请调整筛选条件。";
        return new(nodes, edges, message);
    }

    private static void Layout(List<GraphNode> nodes, List<GraphEdge> edges, CancellationToken token)
    {
        if (nodes.Count <= 1 || edges.Count == 0) return;
        var index = nodes.Select((node, i) => (node, i)).ToDictionary(pair => pair.node.Note.Id, pair => pair.i);
        var ideal = Math.Sqrt(360_000.0 / nodes.Count);
        for (var step = 0; step < 36; step++)
        {
            token.ThrowIfCancellationRequested();
            var forces = new Vector[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
                for (var j = i + 1; j < nodes.Count; j++)
                {
                    var delta = nodes[i].Position - nodes[j].Position;
                    var distance = Math.Max(1, delta.Length);
                    var force = delta / distance * (ideal * ideal / distance);
                    forces[i] += force; forces[j] -= force;
                }
            foreach (var edge in edges)
            {
                var delta = edge.From.Position - edge.To.Position;
                var distance = Math.Max(1, delta.Length);
                var force = delta / distance * (distance * distance / ideal * 0.12);
                forces[index[edge.From.Note.Id]] -= force; forces[index[edge.To.Note.Id]] += force;
            }
            var temperature = 20.0 * (1 - step / 36.0);
            for (var i = 0; i < nodes.Count; i++)
            {
                var force = forces[i];
                if (force.Length > 0) nodes[i].Position += force / force.Length * Math.Min(force.Length, temperature);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; _filterTimer.Stop();
        _work?.Cancel(); _work?.Dispose(); _work = null;
        _busy = false; _surface.StopInteraction();
    }
    private sealed record GraphModel(List<GraphNode> Nodes, List<GraphEdge> Edges, string Message);
    private sealed class GraphNode(LibraryNote note, Point position)
    {
        public LibraryNote Note { get; } = note;
        public Point Position = position;
        public int Degree;
        public double Radius => 5 + Math.Min(6, Math.Sqrt(Degree) * 1.4);
    }
    private sealed record GraphEdge(GraphNode From, GraphNode To);

    private sealed class GraphSurface : FrameworkElement
    {
        private static DependencyProperty BrushProperty(string name, Brush fallback) => DependencyProperty.Register(name, typeof(Brush), typeof(GraphSurface), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly DependencyProperty BackgroundBrushProperty = BrushProperty("BackgroundBrush", Brushes.Black);
        private static readonly DependencyProperty NodeBrushProperty = BrushProperty("NodeBrush", Brushes.Silver);
        private static readonly DependencyProperty TextBrushProperty = BrushProperty("TextBrush", Brushes.LightGray);
        private static readonly DependencyProperty EdgeBrushProperty = BrushProperty("EdgeBrush", Brushes.DimGray);
        private static readonly DependencyProperty AccentBrushProperty = BrushProperty("AccentBrush", Brushes.MediumPurple);
        private static readonly DependencyProperty SelectionBrushProperty = BrushProperty("SelectionBrush", Brushes.DarkSlateBlue);
        public GraphModel Model { get; private set; } = new([], [], "");
        public string? SelectedId { get; set; }
        public event Action<LibraryNote>? Selected;
        public event Action<LibraryNote>? OpenRequested;
        public event Action? SelectionSettled;
        private double _scale = 1;
        private Vector _pan, _nodeOffset;
        private Point _lastPointer;
        private GraphNode? _dragged, _hovered;
        private bool _panning, _fitPending;

        public GraphSurface()
        {
            SetResourceReference(BackgroundBrushProperty, "NotebookBackground");
            SetResourceReference(NodeBrushProperty, "NotebookMuted");
            SetResourceReference(TextBrushProperty, "NotebookText");
            SetResourceReference(EdgeBrushProperty, "NotebookBorder");
            SetResourceReference(AccentBrushProperty, "NotebookAccent");
            SetResourceReference(SelectionBrushProperty, "NotebookSelection");
            SizeChanged += (_, _) => { if (_fitPending) Fit(); else InvalidateVisual(); };
        }
        public void SetModel(GraphModel model, bool fit)
        {
            StopInteraction(); Model = model;
            if (fit) Fit();
            InvalidateVisual();
        }
        public void Fit()
        {
            if (ActualWidth < 20 || ActualHeight < 20) { _fitPending = true; return; }
            _fitPending = false;
            if (Model.Nodes.Count == 0) { _scale = 1; _pan = new(); InvalidateVisual(); return; }
            var left = Model.Nodes.Min(node => node.Position.X) - 80;
            var top = Model.Nodes.Min(node => node.Position.Y) - 50;
            var right = Model.Nodes.Max(node => node.Position.X) + 80;
            var bottom = Model.Nodes.Max(node => node.Position.Y) + 60;
            _scale = Math.Clamp(Math.Min(ActualWidth / (right - left), ActualHeight / (bottom - top)) * 0.9, 0.12, 2);
            _pan = new(-(left + right) / 2 * _scale, -(top + bottom) / 2 * _scale);
            InvalidateVisual();
        }
        protected override void OnRender(DrawingContext context)
        {
            context.DrawRectangle((Brush)GetValue(BackgroundBrushProperty), null, new Rect(RenderSize));
            context.PushTransform(new MatrixTransform(new Matrix(_scale, 0, 0, _scale, ActualWidth / 2 + _pan.X, ActualHeight / 2 + _pan.Y)));
            var accent = (Brush)GetValue(AccentBrushProperty);
            foreach (var edge in Model.Edges)
            {
                var highlighted = edge.From.Note.Id == SelectedId || edge.To.Note.Id == SelectedId || edge.From == _hovered || edge.To == _hovered;
                var brush = highlighted ? accent : (Brush)GetValue(EdgeBrushProperty);
                context.DrawLine(new Pen(brush, (highlighted ? 1.5 : 0.85) / _scale), edge.From.Position, edge.To.Position);
            }
            foreach (var node in Model.Nodes)
            {
                var selected = node.Note.Id == SelectedId; var highlighted = selected || node == _hovered;
                if (selected) context.DrawEllipse((Brush)GetValue(SelectionBrushProperty), null, node.Position, node.Radius + 6 / _scale, node.Radius + 6 / _scale);
                context.DrawEllipse(highlighted ? accent : (Brush)GetValue(NodeBrushProperty), null, node.Position, node.Radius, node.Radius);
                if (_scale < 0.65 && Model.Nodes.Count > 80 && !highlighted) continue;
                var label = new FormattedText(node.Note.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI, Microsoft YaHei UI"), 12 / _scale, highlighted ? accent : (Brush)GetValue(TextBrushProperty), VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = 150 / _scale, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                context.DrawText(label, new Point(node.Position.X - label.Width / 2, node.Position.Y + node.Radius + 4 / _scale));
            }
            context.Pop();
        }
        private Point World(Point point) => new((point.X - ActualWidth / 2 - _pan.X) / _scale, (point.Y - ActualHeight / 2 - _pan.Y) / _scale);
        private GraphNode? Hit(Point point)
        {
            var world = World(point);
            return Model.Nodes.LastOrDefault(node => (node.Position - world).Length <= Math.Max(node.Radius + 3, 10 / _scale));
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
        {
            base.OnMouseLeftButtonDown(args); Focus();
            _lastPointer = args.GetPosition(this);
            var node = Hit(_lastPointer);
            if (node != null)
            {
                SelectedId = node.Note.Id; Selected?.Invoke(node.Note);
                if (args.ClickCount == 2) { OpenRequested?.Invoke(node.Note); args.Handled = true; return; }
                _dragged = node; _nodeOffset = node.Position - World(_lastPointer); Cursor = Cursors.SizeAll;
            }
            else { _panning = true; Cursor = Cursors.ScrollAll; }
            CaptureMouse(); InvalidateVisual(); args.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            var pointer = args.GetPosition(this);
            if (_dragged != null && args.LeftButton == MouseButtonState.Pressed) _dragged.Position = World(pointer) + _nodeOffset;
            else if (_panning && args.LeftButton == MouseButtonState.Pressed) _pan += pointer - _lastPointer;
            else { _hovered = Hit(pointer); Cursor = _hovered is null ? Cursors.Arrow : Cursors.Hand; ToolTip = _hovered?.Note.RelativePath; }
            _lastPointer = pointer; InvalidateVisual();
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
        {
            var selected = _dragged != null;
            StopInteraction();
            if (selected) SelectionSettled?.Invoke();
            args.Handled = true; base.OnMouseLeftButtonUp(args);
        }
        protected override void OnLostMouseCapture(MouseEventArgs args) { _dragged = null; _panning = false; base.OnLostMouseCapture(args); }
        protected override void OnMouseLeave(MouseEventArgs args) { if (!IsMouseCaptured) { _hovered = null; InvalidateVisual(); } base.OnMouseLeave(args); }
        protected override void OnMouseWheel(MouseWheelEventArgs args)
        {
            var pointer = args.GetPosition(this); var world = World(pointer);
            _scale = Math.Clamp(_scale * Math.Pow(1.15, args.Delta / 120.0), 0.12, 6);
            _pan = new(pointer.X - ActualWidth / 2 - world.X * _scale, pointer.Y - ActualHeight / 2 - world.Y * _scale);
            InvalidateVisual(); args.Handled = true; base.OnMouseWheel(args);
        }
        protected override void OnKeyDown(KeyEventArgs args)
        {
            if (args.Key == Key.Enter && Model.Nodes.FirstOrDefault(node => node.Note.Id == SelectedId) is { } node) { OpenRequested?.Invoke(node.Note); args.Handled = true; }
            else if (args.Key == Key.F) { Fit(); args.Handled = true; }
            else if (args.Key is Key.Left or Key.Right or Key.Up or Key.Down && Model.Nodes.Count > 0)
            {
                var current = Model.Nodes.FindIndex(node => node.Note.Id == SelectedId);
                var delta = args.Key is Key.Left or Key.Up ? -1 : 1;
                var next = Model.Nodes[(Math.Max(0, current) + delta + Model.Nodes.Count) % Model.Nodes.Count];
                SelectedId = next.Note.Id; Selected?.Invoke(next.Note); InvalidateVisual(); args.Handled = true;
            }
            base.OnKeyDown(args);
        }
        public void StopInteraction()
        {
            _dragged = null; _panning = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
    }
}
