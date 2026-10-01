using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;

namespace DamaCapture;

internal sealed class EditorSurface : FrameworkElement
{
    public ImageDocument? Document { get; private set; }
    public string Tool { get; set; } = "Mosaic";
    public Color Ink { get; set; } = Color.FromRgb(238, 32, 46);
    public int Strength { get; set; } = 18;
    public double Stroke { get; set; } = 4;
    public string AnnotationText { get; set; } = "여기를 확인하세요";
    public string Font { get; set; } = "Malgun Gothic";
    public bool Bold { get; set; }
    /// <summary>New rectangles and ellipses are filled with the ink color.</summary>
    public bool Fill { get; set; }
    public Guid? SelectedId { get; set; }
    public event Action? Changed;
    public event Action? SelectionChanged;
    private IReadOnlyList<(Rect Bounds, string Label)> hints = [];
    /// <summary>Suggested areas drawn over the image until the user masks or dismisses them.</summary>
    public IReadOnlyList<(Rect Bounds, string Label)> Hints { get => hints; set { hints = value ?? []; DrawHints(); } }
    // Rendering is layered so that only what moves is redrawn: the image and committed edits are drawn in
    // OnRender, while the drag preview, detection hints and scan line each live in their own visual.
    // This keeps software rendering cheap even for large captures.
    private readonly DrawingVisual previewLayer = new(), hintLayer = new(), scanLayer = new();
    private readonly ContainerVisual scanClip = new();
    private readonly TranslateTransform scanOffset = new();
    public static readonly DependencyProperty ScanLineProperty = DependencyProperty.Register(nameof(ScanLine), typeof(double), typeof(EditorSurface),
        new FrameworkPropertyMetadata(-1.0, (target, _) => ((EditorSurface)target).MoveScanLine()));
    public static readonly DependencyProperty HintRevealProperty = DependencyProperty.Register(nameof(HintReveal), typeof(double), typeof(EditorSurface),
        new FrameworkPropertyMetadata(1.0, (target, _) => ((EditorSurface)target).DrawHints()));
    /// <summary>Where the detector's pass is drawn, as a fraction of the height; negative when idle.</summary>
    public double ScanLine { get => (double)GetValue(ScanLineProperty); set => SetValue(ScanLineProperty, value); }
    /// <summary>How much of each hint outline has been drawn, 0 to 1.</summary>
    public double HintReveal { get => (double)GetValue(HintRevealProperty); set => SetValue(HintRevealProperty, value); }
    public const int SweepDuration = 900;
    public void Sweep() => Motion.Play(this, ScanLineProperty, 0, 1, SweepDuration, rest: -1, ease: new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut });
    public void RevealHints() => Motion.Play(this, HintRevealProperty, 0, 1, 520);
    public event Action<Rect>? CropRequested;
    /// <summary>Text extraction region in image pixels; the whole image on a double click.</summary>
    public event Action<Rect>? ExtractRequested;
    private BitmapSource? rendered;
    private bool dragging;
    private Point start, last;
    private readonly List<Point> points = new();
    private EditOperation? moving;
    private EditOperation? preview;
    private bool resizing;
    private string activeTool = "Mosaic";

    public EditorSurface()
    {
        Focusable = true; Cursor = Cursors.Cross;
        MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up;
        LostMouseCapture += (_, _) => { if (dragging) CancelPendingEdit(); };
        scanLayer.Transform = scanOffset; scanClip.Children.Add(scanLayer); scanClip.Opacity = 0;
        foreach (var layer in new Visual[] { previewLayer, hintLayer, scanClip }) AddVisualChild(layer);
    }
    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => previewLayer, 1 => hintLayer, _ => scanClip };
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Strokes and labels on the layers are sized for the current zoom.
        if (e.Property == LayoutTransformProperty) { DrawHints(); DrawPreview(); }
    }
    public void SetDocument(ImageDocument document)
    {
        CancelPendingEdit();
        Document = document; SelectedId = null; Width = document.Width; Height = document.Height; Refresh();
    }
    public void Refresh()
    {
        if (Document == null) return;
        Width = Document.Width; Height = Document.Height;
        rendered = Document.Render(); InvalidateVisual(); DrawHints();
    }
    private double ViewScale => Math.Max(.02, Math.Abs(LayoutTransform.Value.M11));
    private void DrawHints()
    {
        using var dc = hintLayer.RenderOpen();
        if (hints.Count == 0 || Document == null) return;
        var scale = ViewScale;
        var thickness = 1.5 / scale;
        var reveal = Math.Clamp(HintReveal, 0, 1);
        var typeface = new Typeface(new FontFamily("Segoe UI, Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        foreach (var (bounds, label) in hints)
        {
            // The outline is traced from the top-left corner, then the tag pops in beside it.
            if (reveal > 0) dc.DrawGeometry(null, new Pen(Ui.Red, thickness) { DashStyle = HintDashes(reveal, 2 * (bounds.Width + bounds.Height) / thickness) }, Outline(bounds));
            var tagProgress = Math.Clamp((reveal - .55) / .45, 0, 1);
            if (tagProgress <= 0) continue;
            var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11 / scale, Brushes.White, 1.0);
            var tag = new Rect(bounds.X, Math.Max(0, bounds.Y - text.Height - 4 / scale), text.Width + 8 / scale, text.Height + 3 / scale);
            var pop = .6 + .4 * (1 - Math.Pow(1 - tagProgress, 3));
            dc.PushTransform(new ScaleTransform(pop, pop, tag.X, tag.Bottom)); dc.PushOpacity(tagProgress);
            dc.DrawRectangle(Ui.Red, null, tag);
            dc.DrawText(text, new Point(tag.X + 4 / scale, tag.Y + 1 / scale));
            dc.Pop(); dc.Pop();
        }
    }
    // The band is drawn once per pass and slid down; each frame only repaints the strip it crosses.
    private void MoveScanLine()
    {
        var scan = ScanLine;
        if (scan < 0 || scan > 1 || Document == null)
        {
            if (scanClip.Opacity != 0) { scanClip.Opacity = 0; using var _ = scanLayer.RenderOpen(); }
            return;
        }
        if (scanClip.Opacity == 0)
        {
            var scale = ViewScale; var band = 28 / scale;
            scanClip.Clip = new RectangleGeometry(new Rect(0, 0, Width, Height));
            using (var dc = scanLayer.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 238, 32, 46), Color.FromArgb(60, 238, 32, 46), 90), null, new Rect(0, -band, Width, band));
                dc.DrawRectangle(Ui.Red, null, new Rect(0, -1 / scale, Width, 2 / scale));
            }
            scanClip.Opacity = 1;
        }
        scanOffset.Y = scan * Height;
    }
    private void DrawPreview()
    {
        using var dc = previewLayer.RenderOpen();
        if (!dragging || Document == null) return;
        if (activeTool == "Pen")
        {
            var pen = new Pen(new SolidColorBrush(Ink), Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (var i = 1; i < points.Count; i++) dc.DrawLine(pen, points[i - 1], points[i]);
        }
        else
        {
            var r = preview?.Bounds ?? new Rect(start, last);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 238, 32, 46)), new Pen(Ui.Red, 2 / ViewScale), r);
        }
    }
    public void Select(Guid? id) { SelectedId = id; SelectionChanged?.Invoke(); InvalidateVisual(); }
    public void DeleteSelection()
    {
        CancelPendingEdit();
        if (SelectedId is not Guid id || Document == null) return;
        Document.Remove(id); SelectedId = null; Refresh(); Changed?.Invoke();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (rendered == null || Document == null) return;
        dc.DrawImage(rendered, new Rect(0, 0, Width, Height));
        foreach (var op in Document.Operations.Where(x => IsRedaction(x.Kind)))
        {
            if (moving?.Id == op.Id && dragging) continue;
            var selected = op.Id == SelectedId;
            dc.DrawRectangle(null, new Pen(selected ? Ui.Red : new SolidColorBrush(Color.FromArgb(160, 238, 32, 46)), selected ? 2.5 : 1) { DashStyle = selected ? DashStyles.Solid : DashStyles.Dash }, op.Bounds);
            if (selected) dc.DrawRectangle(Ui.Red, null, HandleBounds(op));
        }
        if (SelectedId is Guid id)
        {
            var selection = Document.Operations.FirstOrDefault(x => x.Id == id);
            if (selection != null && !IsRedaction(selection.Kind)) dc.DrawRectangle(null, new Pen(Ui.Red, 1.5) { DashStyle = DashStyles.Dash }, VisibleBounds(selection));
        }
    }
    private static PathGeometry Outline(Rect bounds)
    {
        var figure = new PathFigure { StartPoint = bounds.TopLeft, IsClosed = true };
        figure.Segments.Add(new LineSegment(bounds.TopRight, true)); figure.Segments.Add(new LineSegment(bounds.BottomRight, true)); figure.Segments.Add(new LineSegment(bounds.BottomLeft, true));
        var geometry = new PathGeometry(); geometry.Figures.Add(figure); return geometry;
    }
    // Dash lengths are in pen widths. A partial trace repeats the pattern up to the revealed length, then leaves the rest blank.
    private static DashStyle HintDashes(double reveal, double perimeterUnits)
    {
        if (reveal >= 1) return new DashStyle(new double[] { 4, 3 }, 0);
        var dashes = new List<double>();
        for (var drawn = 0.0; drawn < reveal * perimeterUnits; drawn += 7) { dashes.Add(4); dashes.Add(3); }
        dashes.Add(0); dashes.Add(perimeterUnits * 2 + 10);
        return new DashStyle(dashes, 0);
    }
    private Point Position(MouseEventArgs e)
    {
        var p = e.GetPosition(this); return new Point(Math.Clamp(p.X, 0, Width), Math.Clamp(p.Y, 0, Height));
    }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (Document == null) return;
        if (Tool == "Extract" && e.ClickCount == 2) { CancelPendingEdit(); ExtractRequested?.Invoke(new Rect(0, 0, Width, Height)); e.Handled = true; return; }
        Focus(); start = last = Position(e); points.Clear(); points.Add(start); moving = null; preview = null; resizing = false; activeTool = Tool;
        if (activeTool is "Select" or "Mosaic" or "Blur" or "Solid")
        {
            var edits = Document.Operations.Reverse().ToArray();
            // The visible handle extends beyond the region. Give the selected handle priority.
            var hit = edits.FirstOrDefault(x => x.Id == SelectedId && IsRedaction(x.Kind) && HandleBounds(x).Contains(start))
                ?? edits.FirstOrDefault(x => (activeTool == "Select" || IsRedaction(x.Kind)) && VisibleBounds(x).Contains(start));
            if (hit != null)
            {
                moving = hit; preview = hit; SelectedId = hit.Id;
                resizing = IsRedaction(hit.Kind) && HandleBounds(hit).Contains(start);
                SelectionChanged?.Invoke();
            }
            else { SelectedId = null; SelectionChanged?.Invoke(); if (activeTool == "Select") { InvalidateVisual(); return; } }
        }
        if (activeTool == "Text" || activeTool == "Number")
        {
            if (activeTool == "Text" && string.IsNullOrWhiteSpace(AnnotationText)) return;
            if (activeTool == "Number")
            {
                var radius = Math.Max(13, Stroke * 3.5);
                start = new Point(Width <= radius * 2 ? Width / 2 : Math.Clamp(start.X, radius, Width - radius), Height <= radius * 2 ? Height / 2 : Math.Clamp(start.Y, radius, Height - radius));
            }
            // The next number follows the largest one still present, so deleting a badge never repeats a value.
            var nextNumber = Document.Operations.Where(x => x.Kind == EditKind.Number).Select(x => int.TryParse(x.Text, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
            var extent = activeTool == "Text" ? ImageDocument.MeasureText(AnnotationText, Stroke, Font, Bold) : new Size(40, 40);
            var op = new EditOperation { Kind = Enum.Parse<EditKind>(activeTool), Bounds = new Rect(start.X, start.Y, Math.Max(1, Math.Min(Width - start.X, extent.Width)), Math.Max(1, Math.Min(Height - start.Y, extent.Height))), Color = Ink, Stroke = Stroke,
                Text = activeTool == "Number" ? nextNumber.ToString() : AnnotationText, Font = activeTool == "Text" ? Font : "", Bold = activeTool == "Text" && Bold };
            Document.Add(op); SelectedId = op.Id; Refresh(); Changed?.Invoke(); return;
        }
        dragging = true; CaptureMouse(); e.Handled = true; InvalidateVisual(); DrawPreview();
    }
    private void Move(object sender, MouseEventArgs e)
    {
        if (!dragging || Document == null) return;
        last = Position(e);
        UpdatePreview();
        DrawPreview();
    }
    private void UpdatePreview()
    {
        if (moving != null)
        {
            var b = moving.Bounds;
            if (resizing)
            {
                var availableWidth = Math.Max(0, Width - b.X); var availableHeight = Math.Max(0, Height - b.Y);
                b = new Rect(b.X, b.Y, Math.Clamp(last.X - b.X, Math.Min(1, availableWidth), availableWidth), Math.Clamp(last.Y - b.Y, Math.Min(1, availableHeight), availableHeight));
            }
            else
            {
                var visible = VisibleBounds(moving);
                var x = Math.Clamp(visible.X + last.X - start.X, 0, Math.Max(0, Width - visible.Width));
                var y = Math.Clamp(visible.Y + last.Y - start.Y, 0, Math.Max(0, Height - visible.Height));
                b.Offset(x - visible.X, y - visible.Y);
            }
            var dx = b.X - moving.Bounds.X; var dy = b.Y - moving.Bounds.Y;
            preview = moving with { Bounds = b, Points = moving.Points.Select(p => new Point(p.X + dx, p.Y + dy)).ToArray() };
        }
        else if (activeTool == "Pen" && (points.Count == 0 || points[^1] != last)) points.Add(last);
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!dragging || Document == null) return;
        last = Position(e); CompletePendingEdit(); e.Handled = true;
    }
    /// <summary>Commit pointer geometry before any export, including keyboard save during a drag.</summary>
    public void CompletePendingEdit()
    {
        if (!dragging || Document == null) return;
        UpdatePreview(); dragging = false; ReleaseMouseCapture();
        var changed = false;
        if (moving != null && preview != null)
        {
            if (moving.Bounds != preview.Bounds) { Document.Update(preview); changed = true; }
        }
        else
        {
            var bounds = new Rect(start, last);
            if (activeTool == "Pen" && points.Count > 0)
            {
                // Freehand paths can return to their starting point; endpoints are not their extent.
                bounds = Rect.Empty;
                foreach (var point in points) bounds.Union(point);
                bounds.Inflate(Stroke / 2, Stroke / 2);
                bounds.Intersect(new Rect(0, 0, Width, Height));
            }
            if (activeTool == "Crop") { if (bounds.Width >= 2 && bounds.Height >= 2) CropRequested?.Invoke(bounds); }
            else if (activeTool == "Extract") { if (bounds.Width >= 2 && bounds.Height >= 2) ExtractRequested?.Invoke(bounds); }
            else if ((bounds.Width >= 1 || bounds.Height >= 1) && Enum.TryParse<EditKind>(activeTool, out var kind))
            {
                // Keep one-pixel edge edits inside the bitmap even when starting at its far edge.
                bounds.X = Math.Min(bounds.X, Math.Max(0, Width - 1)); bounds.Y = Math.Min(bounds.Y, Math.Max(0, Height - 1));
                bounds.Width = Math.Min(Math.Max(1, bounds.Width), Width - bounds.X); bounds.Height = Math.Min(Math.Max(1, bounds.Height), Height - bounds.Y);
                var op = new EditOperation { Kind = kind, Bounds = bounds, Points = activeTool == "Pen" ? points.ToArray() : activeTool == "Highlight" ? Array.Empty<Point>() : new[] { start, last }, Color = Ink, Stroke = Stroke, Strength = Strength, Filled = Fill && kind is EditKind.Rectangle or EditKind.Ellipse };
                Document.Add(op); SelectedId = op.Id; changed = true;
            }
        }
        moving = preview = null; points.Clear(); DrawPreview(); Refresh(); if (changed) Changed?.Invoke();
    }
    public void CancelPendingEdit()
    {
        dragging = false; moving = preview = null; points.Clear();
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual(); DrawPreview();
    }
    private Rect HandleBounds(EditOperation operation)
    {
        var scale = Math.Abs(LayoutTransform.Value.M11);
        var size = 10 / Math.Max(.02, scale);
        return new Rect(operation.Bounds.Right - size / 2, operation.Bounds.Bottom - size / 2, size, size);
    }
    private static Rect VisibleBounds(EditOperation operation)
    {
        if (operation.Kind is EditKind.Line or EditKind.Arrow)
        {
            // A horizontal or vertical stroke has a one-pixel box; give it a grabbable band.
            var band = operation.Bounds; band.Inflate(operation.Stroke / 2 + 4, operation.Stroke / 2 + 4); return band;
        }
        if (operation.Kind != EditKind.Number) return operation.Bounds;
        var radius = Math.Max(13, operation.Stroke * 3.5);
        return new Rect(operation.Bounds.X - radius, operation.Bounds.Y - radius, radius * 2, radius * 2);
    }
    public static bool IsRedaction(EditKind kind) => kind is EditKind.Mosaic or EditKind.Blur or EditKind.Solid;
}
