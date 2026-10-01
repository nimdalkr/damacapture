using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DamaCapture.Imaging;

/// <summary>Pixel-based editing with immutable source images and bounded undo history.</summary>
public sealed class ImageDocument
{
    private const int MaxHistory = 24;
    private const long HistoryByteBudget = 128L * 1024 * 1024;
    private readonly List<Snapshot> undo = [];
    private readonly List<Snapshot> redo = [];
    private readonly List<EditOperation> operations = [];
    private BitmapSource source;
    private BitmapSource? rendered;

    private sealed record Snapshot(BitmapSource Image, EditOperation[] Operations);

    public ImageDocument(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        source = Normalize(image);
    }

    public BitmapSource Source => source;
    public IReadOnlyList<EditOperation> Operations => operations.Select(Clone).ToArray();
    public int Width => source.PixelWidth;
    public int Height => source.PixelHeight;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public long Revision { get; private set; }
    /// <summary>Approximate retained bitmap and edit data, counting shared objects only once.</summary>
    public long EstimatedMemoryBytes => RetainedBytes(includeRendered: true);

    public void Add(EditOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operations.Any(item => item.Id == operation.Id))
            throw new ArgumentException("An operation with this identifier already exists.", nameof(operation));
        var normalized = Validate(operation);
        Remember();
        operations.Add(normalized);
        Changed();
    }

    public void Update(EditOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var index = operations.FindIndex(item => item.Id == operation.Id);
        if (index < 0) return;
        var normalized = Validate(operation);
        if (Equivalent(operations[index], normalized)) return;
        Remember();
        operations[index] = normalized;
        Changed();
    }

    public void Remove(Guid id)
    {
        var index = operations.FindIndex(item => item.Id == id);
        if (index < 0) return;
        Remember();
        operations.RemoveAt(index);
        Changed();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        redo.Add(Current());
        Restore(undo[^1]);
        undo.RemoveAt(undo.Count - 1);
        Changed();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        undo.Add(Current());
        Restore(redo[^1]);
        redo.RemoveAt(redo.Count - 1);
        Changed();
    }

    public void Crop(Rect bounds)
    {
        var crop = PixelBounds(bounds, Width, Height);
        if (crop.IsEmpty || crop.Width <= 0 || crop.Height <= 0) return;
        if (crop.X == 0 && crop.Y == 0 && crop.Width == Width && crop.Height == Height) return;
        var image = new CroppedBitmap(Render(), crop);
        ReplaceSource(Normalize(image));
    }

    public void Resize(int width, int height)
    {
        ValidateSize(width, height);
        if (width == Width && height == Height) return;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(Render(), new Rect(0, 0, width, height));
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        ReplaceSource(Normalize(result));
    }

    public void Rotate90()
    {
        var original = Render();
        var bytes = Pixels(original);
        var width = original.PixelWidth;
        var height = original.PixelHeight;
        var result = new byte[bytes.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var from = (y * width + x) * 4;
            var to = (x * height + (height - 1 - y)) * 4;
            Buffer.BlockCopy(bytes, from, result, to, 4);
        }
        if (operations.Count == 0 && width == height && bytes.AsSpan().SequenceEqual(result)) return;
        ReplaceSource(FromPixels(height, width, result));
    }

    /// <summary>Returns flattened pixels. Annotations can never be drawn over a redaction.</summary>
    public BitmapSource Render()
    {
        if (rendered is not null) return rendered;
        if (operations.Count == 0) return rendered = source;
        BitmapSource composed = source;
        var annotations = operations.Where(item => !IsRedaction(item.Kind)).ToArray();
        if (annotations.Length > 0)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawImage(source, new Rect(0, 0, Width, Height));
                foreach (var operation in annotations) DrawAnnotation(drawing, operation);
            }
            var result = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            composed = Normalize(result);
        }
        var redactions = operations.Where(item => IsRedaction(item.Kind)).ToArray();
        if (redactions.Length == 0) return rendered = composed;
        var pixels = Pixels(composed);
        foreach (var operation in redactions)
        {
            var bounds = PixelBounds(operation.Bounds, Width, Height);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
            switch (operation.Kind)
            {
                case EditKind.Mosaic: Mosaic(pixels, Width, bounds, operation.Strength); break;
                case EditKind.Blur: Blur(pixels, Width, bounds, operation.Strength); break;
                case EditKind.Solid: Solid(pixels, Width, bounds, operation.Color); break;
            }
        }
        return rendered = FromPixels(Width, Height, pixels);
    }

    public static bool IsRedaction(EditKind kind) => kind is EditKind.Mosaic or EditKind.Blur or EditKind.Solid;

    internal static BitmapSource Normalize(BitmapSource image)
    {
        ValidateSize(image.PixelWidth, image.PixelHeight);
        var converted = image.Format == PixelFormats.Bgra32
            ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        return FromPixels(converted.PixelWidth, converted.PixelHeight, Pixels(converted));
    }

    internal static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
        image.CopyPixels(pixels, checked(image.PixelWidth * 4), 0);
        return pixels;
    }

    internal static BitmapSource FromPixels(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    internal static void ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > 32768 || height > 32768 || (long)width * height > 64_000_000)
            throw new ArgumentOutOfRangeException(nameof(width), $"이미지는 한 변 32,768px 이하, 전체 6,400만 픽셀 이하만 열 수 있습니다. (현재 {width:N0} × {height:N0})");
    }

    private static EditOperation Clone(EditOperation operation) => operation with { Points = (Point[])operation.Points.Clone() };

    private static bool Equivalent(EditOperation left, EditOperation right) => left.Id == right.Id
        && left.Kind == right.Kind && left.Bounds == right.Bounds && left.Text == right.Text
        && left.Color == right.Color && left.Stroke == right.Stroke && left.Strength == right.Strength
        && left.Points.AsSpan().SequenceEqual(right.Points);

    private static EditOperation Validate(EditOperation operation)
    {
        if (!Enum.IsDefined(operation.Kind)) throw new ArgumentOutOfRangeException(nameof(operation.Kind));
        if (!operation.Bounds.IsEmpty && (!double.IsFinite(operation.Bounds.X) || !double.IsFinite(operation.Bounds.Y)
            || !double.IsFinite(operation.Bounds.Width) || !double.IsFinite(operation.Bounds.Height)))
            throw new ArgumentException("Bounds must be finite.", nameof(operation));
        var points = operation.Points ?? [];
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw new ArgumentException("Points must be finite.", nameof(operation));
        return operation with
        {
            Points = (Point[])points.Clone(),
            Text = operation.Text ?? "",
            Stroke = double.IsFinite(operation.Stroke) ? Math.Clamp(operation.Stroke, 1, 128) : 4,
            Strength = Math.Clamp(operation.Strength, 2, 96)
        };
    }

    private Snapshot Current() => new(source, operations.ToArray());

    private void Remember()
    {
        undo.Add(Current());
        redo.Clear();
        rendered = null;
    }

    private void Restore(Snapshot snapshot)
    {
        source = snapshot.Image;
        operations.Clear();
        operations.AddRange(snapshot.Operations);
        rendered = null;
    }

    private void ReplaceSource(BitmapSource image)
    {
        Remember();
        source = image;
        operations.Clear();
        rendered = null;
        Changed();
    }

    private void Changed()
    {
        Revision++;
        TrimHistory();
    }

    private void TrimHistory()
    {
        while (undo.Count > MaxHistory) undo.RemoveAt(0);
        while (redo.Count > MaxHistory) redo.RemoveAt(0);
        // The budget covers only pixels that snapshots hold beyond the current image, so a large
        // image never loses undo for annotation edits, and the newest step is always kept.
        while (undo.Count + redo.Count > 1 && SnapshotBytes() > HistoryByteBudget)
        {
            if (undo.Count > 0) undo.RemoveAt(0);
            else redo.RemoveAt(0);
        }
    }

    private long SnapshotBytes()
    {
        var images = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance);
        foreach (var state in undo.Concat(redo)) if (!ReferenceEquals(state.Image, source)) images.Add(state.Image);
        return images.Sum(image => (long)image.PixelWidth * image.PixelHeight * 4);
    }

    private long RetainedBytes(bool includeRendered)
    {
        var images = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance) { source };
        if (includeRendered && rendered is not null) images.Add(rendered);
        foreach (var state in undo.Concat(redo)) images.Add(state.Image);
        long bytes = images.Sum(image => (long)image.PixelWidth * image.PixelHeight * 4);
        // Current state and snapshots share immutable edits, arrays and strings.
        var edits = new HashSet<EditOperation>(ReferenceEqualityComparer.Instance);
        foreach (var edit in operations.Concat(undo.Concat(redo).SelectMany(state => state.Operations))) edits.Add(edit);
        var pointArrays = new HashSet<Point[]>(ReferenceEqualityComparer.Instance);
        var texts = new HashSet<string>(ReferenceEqualityComparer.Instance);
        foreach (var edit in edits) { pointArrays.Add(edit.Points); texts.Add(edit.Text); }
        bytes += edits.Count * 128L + pointArrays.Sum(points => points.Length * 16L + 24)
            + texts.Sum(text => text.Length * 2L + 24);
        bytes += operations.Count * 8L + undo.Concat(redo).Sum(state => state.Operations.Length * 8L + 64);
        return bytes;
    }

    private static Int32Rect PixelBounds(Rect bounds, int width, int height)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(bounds.X)
            || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom)) return Int32Rect.Empty;
        var left = (int)Math.Clamp(Math.Floor(bounds.Left), 0, width);
        var top = (int)Math.Clamp(Math.Floor(bounds.Top), 0, height);
        var right = (int)Math.Clamp(Math.Ceiling(bounds.Right), 0, width);
        var bottom = (int)Math.Clamp(Math.Ceiling(bounds.Bottom), 0, height);
        return right <= left || bottom <= top ? Int32Rect.Empty : new Int32Rect(left, top, right - left, bottom - top);
    }

    private static void Mosaic(byte[] pixels, int width, Int32Rect bounds, int block)
    {
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y += block)
        for (var x = bounds.X; x < bounds.X + bounds.Width; x += block)
        {
            var right = Math.Min(x + block, bounds.X + bounds.Width);
            var bottom = Math.Min(y + block, bounds.Y + bounds.Height);
            long b = 0, g = 0, r = 0;
            var count = (right - x) * (bottom - y);
            for (var py = y; py < bottom; py++)
            for (var px = x; px < right; px++)
            {
                var offset = (py * width + px) * 4;
                b += pixels[offset]; g += pixels[offset + 1]; r += pixels[offset + 2];
            }
            var blue = (byte)(b / count); var green = (byte)(g / count); var red = (byte)(r / count);
            for (var py = y; py < bottom; py++)
            for (var px = x; px < right; px++)
            {
                var offset = (py * width + px) * 4;
                pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red; pixels[offset + 3] = 255;
            }
        }
    }

    private static void Solid(byte[] pixels, int width, Int32Rect bounds, Color color)
    {
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        for (var x = bounds.X; x < bounds.X + bounds.Width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
        }
    }

    // Two separable sliding-window passes: O(region pixels), independent of blur radius.
    private static void Blur(byte[] pixels, int width, Int32Rect bounds, int strength)
    {
        var radius = Math.Clamp(strength / 2, 1, 48);
        var window = radius * 2 + 1;
        var horizontal = new byte[checked(bounds.Width * bounds.Height * 3)];
        for (var y = 0; y < bounds.Height; y++)
        for (var channel = 0; channel < 3; channel++)
        {
            var sum = 0;
            for (var k = -radius; k <= radius; k++)
                sum += pixels[((bounds.Y + y) * width + bounds.X + Math.Clamp(k, 0, bounds.Width - 1)) * 4 + channel];
            for (var x = 0; x < bounds.Width; x++)
            {
                horizontal[(y * bounds.Width + x) * 3 + channel] = (byte)(sum / window);
                sum -= pixels[((bounds.Y + y) * width + bounds.X + Math.Clamp(x - radius, 0, bounds.Width - 1)) * 4 + channel];
                sum += pixels[((bounds.Y + y) * width + bounds.X + Math.Clamp(x + radius + 1, 0, bounds.Width - 1)) * 4 + channel];
            }
        }
        for (var x = 0; x < bounds.Width; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var sum = 0;
            for (var k = -radius; k <= radius; k++)
                sum += horizontal[(Math.Clamp(k, 0, bounds.Height - 1) * bounds.Width + x) * 3 + channel];
            for (var y = 0; y < bounds.Height; y++)
            {
                var target = ((bounds.Y + y) * width + bounds.X + x) * 4;
                pixels[target + channel] = (byte)(sum / window);
                pixels[target + 3] = 255;
                sum -= horizontal[(Math.Clamp(y - radius, 0, bounds.Height - 1) * bounds.Width + x) * 3 + channel];
                sum += horizontal[(Math.Clamp(y + radius + 1, 0, bounds.Height - 1) * bounds.Width + x) * 3 + channel];
            }
        }
    }

    private static void DrawAnnotation(DrawingContext drawing, EditOperation operation)
    {
        var brush = new SolidColorBrush(operation.Color);
        brush.Freeze();
        var pen = new Pen(brush, operation.Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var bounds = operation.Bounds.IsEmpty ? new Rect() : operation.Bounds;
        var points = operation.Points;
        var start = points.Length > 0 ? points[0] : bounds.TopLeft;
        var end = points.Length > 1 ? points[^1] : bounds.BottomRight;
        switch (operation.Kind)
        {
            case EditKind.Pen:
                if (points.Length == 1) drawing.DrawEllipse(brush, null, start, operation.Stroke / 2, operation.Stroke / 2);
                else DrawPolyline(drawing, pen, points);
                break;
            case EditKind.Line: drawing.DrawLine(pen, start, end); break;
            case EditKind.Arrow:
                drawing.DrawLine(pen, start, end);
                var direction = start - end;
                if (direction.Length > 0.1)
                {
                    direction.Normalize();
                    var perpendicular = new Vector(-direction.Y, direction.X);
                    var length = Math.Max(12, operation.Stroke * 3.5);
                    drawing.DrawLine(pen, end, end + direction * length + perpendicular * length * 0.45);
                    drawing.DrawLine(pen, end, end + direction * length - perpendicular * length * 0.45);
                }
                break;
            case EditKind.Rectangle: drawing.DrawRectangle(operation.Filled ? brush : null, pen, bounds); break;
            case EditKind.Ellipse: drawing.DrawEllipse(operation.Filled ? brush : null, pen, new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), bounds.Width / 2, bounds.Height / 2); break;
            case EditKind.Highlight:
                drawing.PushOpacity(0.32);
                if (points.Length > 1) DrawPolyline(drawing, new Pen(brush, Math.Max(14, operation.Stroke * 4)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round }, points);
                else drawing.DrawRectangle(brush, null, bounds);
                drawing.Pop();
                break;
            case EditKind.Text:
                drawing.DrawText(AnnotationText(operation.Text, operation.Stroke, operation.Font, operation.Bold, brush), bounds.TopLeft);
                break;
            case EditKind.Number:
                var radius = Math.Max(13, operation.Stroke * 3.5);
                var center = bounds.TopLeft;
                drawing.DrawEllipse(brush, null, center, radius, radius);
                var text = Text(string.IsNullOrWhiteSpace(operation.Text) ? "1" : operation.Text, radius * 1.2, Brushes.White);
                drawing.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
                break;
        }
    }

    private static FormattedText Text(string text, double size, Brush brush) => new(text, CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1);

    // Text size follows the size option; an empty font keeps how earlier text annotations were drawn.
    private static FormattedText AnnotationText(string text, double stroke, string font, bool bold, Brush brush) => new(text, CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, new Typeface(new FontFamily(string.IsNullOrWhiteSpace(font) ? "Segoe UI" : font), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
        Math.Max(14, stroke * 4 + 10), brush, 1);

    /// <summary>The drawn extent of a text annotation, so its selection box matches the letters.</summary>
    public static Size MeasureText(string text, double stroke, string font, bool bold)
    {
        var formatted = AnnotationText(text, stroke, font, bold, Brushes.Black);
        return new Size(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace), Math.Ceiling(formatted.Height));
    }

    private static void DrawPolyline(DrawingContext drawing, Pen pen, Point[] points)
    {
        if (points.Length < 2) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToArray(), true, true);
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }
}
