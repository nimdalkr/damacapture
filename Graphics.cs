using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DamaCapture;

/// <summary>A circular progress arc that drains clockwise from the top; used for countdowns and undo windows.</summary>
internal sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(Ring),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double Thickness { get; set; } = 3;
    public Brush Track { get; set; } = Ui.Brush("#3A393C");
    public Brush Fill { get; set; } = Ui.Red;

    public Ring(double size) { Width = size; Height = size; IsHitTestVisible = false; }

    /// <summary>Drains from full to empty over the given time.</summary>
    public void Drain(int duration) => Motion.Play(this, ProgressProperty, 1, 0, duration, ease: new SineEase { EasingMode = EasingMode.EaseInOut });

    protected override void OnRender(DrawingContext dc)
    {
        var radius = (Math.Min(Width, Height) - Thickness) / 2;
        var center = new Point(Width / 2, Height / 2);
        dc.DrawEllipse(null, new Pen(Track, Thickness), center, radius, radius);
        var progress = Math.Clamp(Progress, 0, 1);
        if (progress <= 0.001) return;
        var pen = new Pen(Fill, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (progress >= 0.999) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = progress * Math.PI * 2;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(); geometry.Figures.Add(figure);
        dc.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>The 담아 mark: four frame corners with a red dot that drops into them.</summary>
internal sealed class BrandMark : FrameworkElement
{
    public static readonly DependencyProperty FallProperty = DependencyProperty.Register(nameof(Fall), typeof(double), typeof(BrandMark),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpreadProperty = DependencyProperty.Register(nameof(Spread), typeof(double), typeof(BrandMark),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Vertical offset of the dot as a fraction of the mark height; 0 is settled.</summary>
    public double Fall { get => (double)GetValue(FallProperty); set => SetValue(FallProperty, value); }
    /// <summary>Scale of the corners around the centre; 1 is at rest.</summary>
    public double Spread { get => (double)GetValue(SpreadProperty); set => SetValue(SpreadProperty, value); }
    public Brush Frame { get; set; } = Ui.Primary;

    public BrandMark(double size) { Width = size; Height = size; IsHitTestVisible = false; }

    /// <summary>The dot falls in and the corners give way for a moment.</summary>
    public void Play()
    {
        var bounce = new BounceEase { Bounces = 1, Bounciness = 3.2, EasingMode = EasingMode.EaseOut };
        Motion.Play(this, FallProperty, -1.1, 0, 520, ease: bounce);
        Motion.Play(this, SpreadProperty, (1.0, 0, null), (1.0, 250, null), (1.14, 360, new CubicEase { EasingMode = EasingMode.EaseOut }), (1.0, 640, new CubicEase { EasingMode = EasingMode.EaseInOut }));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var s = Math.Min(Width, Height);
        var thickness = s * 0.11;
        var pen = new Pen(Frame, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var inset = thickness / 2 + s * 0.04;
        var arm = s * 0.22;
        var spread = Math.Max(0.5, Spread);
        var centre = new Point(Width / 2, Height / 2);
        dc.PushTransform(new ScaleTransform(spread, spread, centre.X, centre.Y));
        var l = inset; var t = inset; var r = s - inset; var b = s - inset;
        void Corner(Point corner, double dx, double dy)
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(corner.X, corner.Y + dy * arm), IsClosed = false };
            figure.Segments.Add(new LineSegment(corner, true));
            figure.Segments.Add(new LineSegment(new Point(corner.X + dx * arm, corner.Y), true));
            geometry.Figures.Add(figure);
            dc.DrawGeometry(null, pen, geometry);
        }
        Corner(new Point(l, t), 1, 1); Corner(new Point(r, t), -1, 1); Corner(new Point(r, b), -1, -1); Corner(new Point(l, b), 1, -1);
        dc.Pop();
        var drop = Fall;
        var clip = new RectangleGeometry(new Rect(-s, -s * 0.05, s * 3, s * 1.1));
        dc.PushClip(clip);
        // Squash a little on landing so the fall reads as weight, not a slide.
        var squash = drop > -0.06 && drop < 0.06 ? 1 - Math.Abs(drop) * 2 : 1;
        var radius = s * 0.165;
        dc.DrawEllipse(Ui.Red, null, new Point(centre.X, centre.Y + drop * s), radius * (2 - squash), radius * squash);
        dc.Pop();
    }
}

/// <summary>Idle editor illustration: a cursor draws a capture frame and a dot lands in it, on a slow loop.</summary>
internal sealed class EmptyIllustration : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(EmptyIllustration),
        new FrameworkPropertyMetadata(HoldFrame, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    private const double HoldFrame = 0.72;
    private const int Period = 4600;

    public EmptyIllustration(double width, double height)
    {
        Width = width; Height = height; IsHitTestVisible = false;
        Loaded += (_, _) => Motion.Loop(this, ProgressProperty, 0, 1, Period, HoldFrame);
        Unloaded += (_, _) => Motion.Stop(this, ProgressProperty, HoldFrame);
    }

    private static double Ease(double t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - Math.Pow(1 - t, 3);
    private static double Phase(double t, double from, double to) => Math.Clamp((t - from) / (to - from), 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        var t = Progress;
        var frame = new Rect(Width * 0.14, Height * 0.16, Width * 0.72, Height * 0.62);
        var draw = Ease(Phase(t, 0.06, 0.52));
        var settle = Phase(t, 0.52, 0.62);
        var fade = 1 - Phase(t, 0.9, 1);
        dc.PushOpacity(fade);
        if (settle > 0)
        {
            dc.PushOpacity(settle);
            dc.DrawRectangle(Ui.Panel, null, frame);
            dc.Pop();
        }
        var pen = new Pen(Ui.Primary, 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        if (draw > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(frame.X - 2, frame.Y - 2, (frame.Width + 4) * draw, (frame.Height + 4) * draw)));
            dc.DrawRectangle(null, pen, frame);
            dc.Pop();
        }
        var cursorFade = 1 - Phase(t, 0.55, 0.64);
        if (cursorFade > 0)
        {
            var cursor = new Point(frame.X + frame.Width * draw, frame.Y + frame.Height * draw);
            var cross = new Pen(Ui.Primary, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.PushOpacity(cursorFade);
            dc.DrawLine(cross, new Point(cursor.X, cursor.Y - 10), new Point(cursor.X, cursor.Y + 10));
            dc.DrawLine(cross, new Point(cursor.X - 10, cursor.Y), new Point(cursor.X + 10, cursor.Y));
            dc.Pop();
        }
        var pop = Phase(t, 0.54, 0.66);
        if (pop > 0)
        {
            var scale = pop < 0.6 ? Ease(pop / 0.6) * 1.25 : 1.25 - 0.25 * Ease((pop - 0.6) / 0.4);
            dc.DrawEllipse(Ui.Red, null, new Point(frame.Right, frame.Bottom), 6 * scale, 6 * scale);
        }
        dc.Pop();
    }
}
