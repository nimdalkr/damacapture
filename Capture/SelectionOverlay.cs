using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Forms = System.Windows.Forms;

namespace DamaCapture.Capture;

/// <summary>A physical-pixel surface, avoiding WPF DIP conversions across differently scaled monitors.</summary>
internal sealed class SelectionOverlay : Forms.Form
{
    private readonly Bitmap _desktopImage;
    private readonly Rectangle _desktopBounds;
    private readonly CaptureMode _mode;
    private readonly Size _fixedSize;
    private readonly Font _font = new("Malgun Gothic", 14, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _smallFont = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly List<Point> _polygon = [];
    private Point _origin, _pointer;
    private Rectangle _selection;
    private bool _dragging;
    // The shaded desktop is composed once; each frame only repaints what moved.
    private Bitmap? _dimmedImage;
    private Rectangle _lastDirty = Rectangle.Empty;
    // The selection edge marches while it is shown; only the edge strips are repainted for it.
    private readonly Forms.Timer _march = new() { Interval = 40 };
    private float _marchOffset;
    private const int HandleSize = 7;
    private const int MagnifierSample = 15, MagnifierZoom = 90;
    public Rectangle? Selection { get; private set; }
    public IReadOnlyList<Point> Polygon => _polygon;

    internal SelectionOverlay(Bitmap desktopImage, Rectangle desktopBounds, CaptureMode mode, int width, int height)
    {
        _desktopImage = desktopImage;
        _desktopBounds = desktopBounds;
        _mode = mode;
        _fixedSize = new Size(width, height);
        AutoScaleMode = Forms.AutoScaleMode.None;
        FormBorderStyle = Forms.FormBorderStyle.None;
        StartPosition = Forms.FormStartPosition.Manual;
        Bounds = desktopBounds;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Forms.Cursors.Cross;
        Text = "담아 · 캡처 영역 선택";
        SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint |
            Forms.ControlStyles.OptimizedDoubleBuffer | Forms.ControlStyles.ResizeRedraw, true);
        _march.Tick += (_, _) =>
        {
            _marchOffset = (_marchOffset + 1) % 7;
            if (_selection.Width > 0 && _selection.Height > 0) Invalidate(EdgeRegion());
        };
    }

    private Region EdgeRegion()
    {
        var outer = _selection; outer.Inflate(6, 6);
        if (_mode == CaptureMode.Freehand) return new Region(outer);
        var inner = _selection; inner.Inflate(-6, -6);
        var region = new Region(outer);
        if (inner.Width > 0 && inner.Height > 0) region.Exclude(inner);
        return region;
    }

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000080 | 0x00000008; // Tool window and topmost, no taskbar entry.
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeMethods.SetWindowPos(Handle, new IntPtr(-1), _desktopBounds.X, _desktopBounds.Y,
            _desktopBounds.Width, _desktopBounds.Height, 0x0040);
        Activate();
        var position = Forms.Cursor.Position;
        _pointer = new Point(position.X - _desktopBounds.X, position.Y - _desktopBounds.Y);
        UpdateHover();
        _lastDirty = DynamicBounds();
        Invalidate();
        if (Motion.Enabled) _march.Start();
    }

    private void InvalidateDynamic()
    {
        var now = DynamicBounds();
        var dirty = _lastDirty.IsEmpty ? now : now.IsEmpty ? _lastDirty : Rectangle.Union(_lastDirty, now);
        _lastDirty = now;
        if (!dirty.IsEmpty) Invalidate(dirty);
    }

    /// <summary>Everything drawn on top of the shaded desktop that can move between frames.</summary>
    private Rectangle DynamicBounds()
    {
        var bounds = Rectangle.Empty;
        void Add(Rectangle part)
        {
            if (part.Width <= 0 || part.Height <= 0) return;
            bounds = bounds.IsEmpty ? part : Rectangle.Union(bounds, part);
        }
        if (_selection.Width > 0 && _selection.Height > 0)
        {
            var outline = _selection; outline.Inflate(6, 6); Add(outline);
            Add(DimensionBounds(out _));
        }
        if (_mode is CaptureMode.Region or CaptureMode.Freehand or CaptureMode.LastRegion) Add(MagnifierBounds());
        return bounds;
    }

    protected override void WndProc(ref Forms.Message message)
    {
        // A single overlay spans monitors. Keep its physical virtual-desktop dimensions when DPI changes.
        if (message.Msg == 0x02E0) { message.Result = IntPtr.Zero; return; }
        base.WndProc(ref message);
    }

    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == Forms.MouseButtons.Right) { Cancel(); return; }
        if (e.Button != Forms.MouseButtons.Left) return;
        _pointer = Clamp(e.Location);
        if (_mode is CaptureMode.Window or CaptureMode.Element or CaptureMode.FixedSize)
        {
            UpdateHover();
            Complete();
            return;
        }
        _dragging = true; _moving = false;
        Capture = true;
        _origin = _pointer;
        _selection = Rectangle.Empty;
        _polygon.Clear();
        if (_mode == CaptureMode.Freehand) _polygon.Add(_origin);
        InvalidateDynamic();
    }

    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer = Clamp(e.Location);
        // Space held during a drag: the rectangle keeps its size and follows the pointer, stopping at the desktop edges.
        if (_dragging && _moving && _mode != CaptureMode.Freehand)
            _origin = new Point(
                Math.Clamp(_pointer.X - _moveSize.Width, Math.Max(0, -_moveSize.Width), Math.Min(_desktopBounds.Width, _desktopBounds.Width - _moveSize.Width)),
                Math.Clamp(_pointer.Y - _moveSize.Height, Math.Max(0, -_moveSize.Height), Math.Min(_desktopBounds.Height, _desktopBounds.Height - _moveSize.Height)));
        if (_dragging)
        {
            if (_mode == CaptureMode.Freehand)
            {
                var last = _polygon[^1];
                if (Math.Abs(last.X - _pointer.X) + Math.Abs(last.Y - _pointer.Y) >= 2)
                    _polygon.Add(_pointer);
                _selection = PolygonBounds();
            }
            else _selection = Spanned(_origin, Corner());
        }
        else UpdateHover();
        InvalidateDynamic();
    }

    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != Forms.MouseButtons.Left || !_dragging) return;
        // The last mouse movement can be coalesced before release. Include the actual release coordinate.
        _pointer = Clamp(e.Location);
        if (_mode == CaptureMode.Freehand)
        {
            if (_polygon.Count == 0 || _polygon[^1] != _pointer) _polygon.Add(_pointer);
            _selection = PolygonBounds();
        }
        else _selection = Spanned(_origin, Corner());
        _moving = false;
        _dragging = false;
        Capture = false;
        Complete();
    }

    private bool _moving;
    /// <summary>The rectangle's signed size, kept while Space moves it.</summary>
    private Size _moveSize;
    private Point Corner() => _moving ? new Point(_origin.X + _moveSize.Width, _origin.Y + _moveSize.Height) : _pointer;
    private static Rectangle Spanned(Point a, Point b) => Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    protected override void OnKeyDown(Forms.KeyEventArgs e)
    {
        if (e.KeyCode == Forms.Keys.Escape) { e.Handled = true; Cancel(); }
        else if (e.KeyCode == Forms.Keys.Enter) { e.Handled = true; Complete(); }
        else if (e.KeyCode == Forms.Keys.Space)
        {
            e.Handled = true; e.SuppressKeyPress = true;
            // Key repeat arrives while held; only the first press fixes the size.
            if (_dragging && !_moving && _mode != CaptureMode.Freehand) { _moveSize = new Size(_pointer.X - _origin.X, _pointer.Y - _origin.Y); _moving = true; }
        }
        else if (e.KeyCode is Forms.Keys.Left or Forms.Keys.Right or Forms.Keys.Up or Forms.Keys.Down)
        {
            // The pointer itself moves, a pixel at a time (ten with Shift), so the edge under it lands exactly.
            e.Handled = true;
            var step = e.Shift ? 10 : 1;
            var position = Forms.Cursor.Position;
            position.Offset(e.KeyCode == Forms.Keys.Left ? -step : e.KeyCode == Forms.Keys.Right ? step : 0, e.KeyCode == Forms.Keys.Up ? -step : e.KeyCode == Forms.Keys.Down ? step : 0);
            Forms.Cursor.Position = position;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(Forms.KeyEventArgs e)
    {
        if (e.KeyCode == Forms.Keys.Space)
        {
            e.Handled = true;
            if (_moving)
            {
                // Sizing resumes from the corner where the rectangle now is, not from wherever the pointer overshot to.
                var corner = Corner(); _moving = false;
                if (corner != _pointer) Forms.Cursor.Position = PointToScreen(corner);
            }
        }
        base.OnKeyUp(e);
    }

    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, _desktopBounds.Width),
        Math.Clamp(point.Y, 0, _desktopBounds.Height));

    private void UpdateHover()
    {
        if (_mode == CaptureMode.FixedSize)
        {
            _selection = new Rectangle(Math.Clamp(_pointer.X - _fixedSize.Width / 2, 0,
                _desktopBounds.Width - _fixedSize.Width), Math.Clamp(_pointer.Y - _fixedSize.Height / 2,
                0, _desktopBounds.Height - _fixedSize.Height), _fixedSize.Width, _fixedSize.Height);
        }
        else if (_mode is CaptureMode.Window or CaptureMode.Element)
        {
            var physical = new Point(_pointer.X + _desktopBounds.X, _pointer.Y + _desktopBounds.Y);
            var bounds = NativeMethods.FindWindowBounds(physical, Handle, _mode == CaptureMode.Element);
            if (bounds is { } candidate)
            {
                _selection = Rectangle.Intersect(candidate, _desktopBounds);
                _selection.Offset(-_desktopBounds.X, -_desktopBounds.Y);
            }
            else _selection = Rectangle.Empty;
        }
    }

    private Rectangle PolygonBounds()
    {
        if (_polygon.Count < 2) return Rectangle.Empty;
        return Rectangle.FromLTRB(_polygon.Min(p => p.X), _polygon.Min(p => p.Y),
            _polygon.Max(p => p.X), _polygon.Max(p => p.Y));
    }

    private void Complete()
    {
        if (_selection.Width < 2 || _selection.Height < 2 ||
            (_mode == CaptureMode.Freehand && _polygon.Count < 3)) return;
        var result = _selection;
        result.Offset(_desktopBounds.Location);
        Selection = result;
        DialogResult = Forms.DialogResult.OK;
        Close();
    }

    private void Cancel() { DialogResult = Forms.DialogResult.Cancel; Close(); }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        var graphics = e.Graphics;
        // GDI+ clips to the invalidated rectangle, so a partial repaint only touches that area.
        graphics.DrawImageUnscaled(_dimmedImage ??= Dim(_desktopImage), 0, 0);
        using var line = new Pen(Color.FromArgb(238, 32, 46), 2);
        if (_march.Enabled) { line.DashPattern = [4f, 3f]; line.DashOffset = _marchOffset; }
        using var polygonPath = new GraphicsPath();
        if (_mode == CaptureMode.Freehand && _polygon.Count > 2) polygonPath.AddPolygon(_polygon.ToArray());
        if (_selection.Width > 0 && _selection.Height > 0)
        {
            var visible = Rectangle.Intersect(_selection, e.ClipRectangle);
            if (visible.Width > 0 && visible.Height > 0)
            {
                var state = graphics.Save();
                if (polygonPath.PointCount > 0) graphics.SetClip(polygonPath, CombineMode.Intersect);
                graphics.DrawImage(_desktopImage, visible, visible, GraphicsUnit.Pixel);
                graphics.Restore(state);
            }
            if (polygonPath.PointCount > 0) graphics.DrawPath(line, polygonPath);
            else
            {
                graphics.DrawRectangle(line, _selection.X, _selection.Y, _selection.Width - 1, _selection.Height - 1);
                DrawHandles(graphics);
            }
            DrawDimensions(graphics);
        }
        DrawInstructions(graphics);
        if (_mode is CaptureMode.Region or CaptureMode.Freehand or CaptureMode.LastRegion) DrawMagnifier(graphics);
    }

    // Corner squares make the rectangle read as an object with edges, not just a line.
    private void DrawHandles(Graphics graphics)
    {
        using var edge = new Pen(Color.FromArgb(8, 8, 9), 1);
        var half = HandleSize / 2;
        foreach (var corner in new[] { new Point(_selection.Left, _selection.Top), new Point(_selection.Right - 1, _selection.Top), new Point(_selection.Right - 1, _selection.Bottom - 1), new Point(_selection.Left, _selection.Bottom - 1) })
        {
            var box = new Rectangle(corner.X - half, corner.Y - half, HandleSize, HandleSize);
            graphics.FillRectangle(Brushes.White, box);
            graphics.DrawRectangle(edge, box);
        }
    }

    // The shade is 145/255 black over an opaque desktop, which keeps 110/255 of each channel. Scaling the
    // channels directly, rows in parallel, is several times faster than letting GDI+ blend a full-screen fill.
    private static readonly byte[] Shade = Enumerable.Range(0, 256).Select(value => (byte)((value * 110 + 127) / 255)).ToArray();
    private static Bitmap Dim(Bitmap source)
    {
        var dimmed = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        var area = new Rectangle(0, 0, source.Width, source.Height);
        var from = source.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var to = dimmed.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var width = source.Width * 4;
            System.Threading.Tasks.Parallel.For(0, source.Height, () => new byte[width], (y, _, row) =>
            {
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(from.Scan0, y * from.Stride), row, 0, width);
                for (var x = 0; x < width; x += 4) { row[x] = Shade[row[x]]; row[x + 1] = Shade[row[x + 1]]; row[x + 2] = Shade[row[x + 2]]; row[x + 3] = 255; }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(to.Scan0, y * to.Stride), width);
                return row;
            }, _ => { });
        }
        finally { source.UnlockBits(from); dimmed.UnlockBits(to); }
        return dimmed;
    }

    private void DrawInstructions(Graphics graphics)
    {
        var verb = _mode switch
        {
            CaptureMode.Window => "창 위에 마우스를 올리고 클릭",
            CaptureMode.Element => "창의 구성 요소를 가리키고 클릭 · 직접 그린 UI는 창 단위로 선택",
            CaptureMode.FixedSize => $"{_fixedSize.Width} × {_fixedSize.Height} 영역을 배치하고 클릭",
            CaptureMode.Freehand => "마우스를 누른 채 자유롭게 둘러 그리기",
            _ => "드래그해 캡처 영역 선택 · Space 누른 채 이동 · 방향키 1px"
        };
        var text = $"{verb}     ESC 취소";
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
        var width = (int)Math.Ceiling(graphics.MeasureString(text, _font).Width) + 32;
        var x = Math.Clamp(screen.X - _desktopBounds.X + (screen.Width - width) / 2, 0, Math.Max(0, Width - width));
        var y = Math.Max(0, screen.Top - _desktopBounds.Top + 22);
        using var background = new SolidBrush(Color.FromArgb(245, 8, 8, 9));
        graphics.FillRectangle(background, x, y, width, 42);
        graphics.DrawString(text, _font, Brushes.White, x + 16, y + 11);
    }

    // Measured without a Graphics so the dirty rectangle and the drawing agree.
    private Rectangle DimensionBounds(out string text)
    {
        text = $"{_selection.Width} × {_selection.Height} px";
        var size = Forms.TextRenderer.MeasureText(text, _smallFont);
        var x = Math.Clamp(_selection.Left, 0, Math.Max(0, Width - size.Width - 20));
        var y = _selection.Top >= 30 ? _selection.Top - 29 : Math.Min(Height - 28, _selection.Bottom + 4);
        return new Rectangle(x, y, size.Width + 18, 25);
    }

    private void DrawDimensions(Graphics graphics)
    {
        var box = DimensionBounds(out var text);
        graphics.FillRectangle(Brushes.Black, box);
        graphics.DrawString(text, _smallFont, Brushes.White, box.X + 9, box.Y + 5);
    }

    private Rectangle MagnifierBounds()
    {
        var x = _pointer.X + 26;
        var y = _pointer.Y + 28;
        if (x + MagnifierZoom + 4 > Width) x = _pointer.X - MagnifierZoom - 26;
        if (y + MagnifierZoom + 29 > Height) y = _pointer.Y - MagnifierZoom - 45;
        return new Rectangle(Math.Max(0, x) - 2, Math.Max(0, y) - 2, MagnifierZoom + 4, MagnifierZoom + 28);
    }

    private void DrawMagnifier(Graphics graphics)
    {
        const int sample = MagnifierSample, zoom = MagnifierZoom;
        if (_desktopImage.Width < sample || _desktopImage.Height < sample) return;
        var box = MagnifierBounds();
        var x = box.X + 2; var y = box.Y + 2;
        graphics.FillRectangle(Brushes.Black, box);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        var sourceX = Math.Clamp(_pointer.X - sample / 2, 0, _desktopImage.Width - sample);
        var sourceY = Math.Clamp(_pointer.Y - sample / 2, 0, _desktopImage.Height - sample);
        graphics.DrawImage(_desktopImage, new Rectangle(x, y, zoom, zoom), new Rectangle(sourceX, sourceY, sample, sample), GraphicsUnit.Pixel);
        using var red = new Pen(Color.FromArgb(238, 32, 46));
        graphics.DrawLine(red, x + zoom / 2, y, x + zoom / 2, y + zoom);
        graphics.DrawLine(red, x, y + zoom / 2, x + zoom, y + zoom / 2);
        graphics.DrawString($"{_pointer.X + _desktopBounds.X}, {_pointer.Y + _desktopBounds.Y}", _smallFont, Brushes.White, x + 3, y + zoom + 6);
        graphics.InterpolationMode = InterpolationMode.Default;
        graphics.PixelOffsetMode = PixelOffsetMode.Default;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _march.Dispose(); _font.Dispose(); _smallFont.Dispose(); _dimmedImage?.Dispose(); }
        base.Dispose(disposing);
    }
}
