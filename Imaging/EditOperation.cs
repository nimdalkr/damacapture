using System;
using System.Windows;
using System.Windows.Media;

namespace DamaCapture.Imaging;

public enum EditKind
{
    Mosaic, Blur, Solid, Pen, Arrow, Rectangle, Ellipse, Line, Text, Number, Highlight
}

public sealed record EditOperation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public EditKind Kind { get; init; }
    public Rect Bounds { get; init; }
    public Point[] Points { get; init; } = [];
    public string Text { get; init; } = "";
    public Color Color { get; init; } = Color.FromRgb(0xEE, 0x20, 0x2E);
    public double Stroke { get; init; } = 4;
    public int Strength { get; init; } = 14;
    /// <summary>Rectangles and ellipses are painted inside as well as outlined.</summary>
    public bool Filled { get; init; }
    /// <summary>Text font family; empty keeps the original Segoe UI rendering.</summary>
    public string Font { get; init; } = "";
    public bool Bold { get; init; }
}
