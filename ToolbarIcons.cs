using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DamaCapture;

internal static class ToolbarIcons
{
    private sealed record Part(string Data, bool Filled = false);
    private static readonly Dictionary<string, Part[]> paths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["capture"] = [new("M8,4 H4 V8 M16,4 H20 V8 M4,16 V20 H8 M20,16 V20 H16 M8,12 H16 M12,8 V16")],
        ["region"] = [new("M8,4 H4 V8 M11,4 H13 M16,4 H20 V8 M4,11 V13 M20,11 V13 M4,16 V20 H8 M11,20 H13 M20,16 V20 H16")],
        ["window"] = [new("M3,5 H21 V19 H3 Z M3,9 H21 M6,7 H6.1 M9,7 H9.1")],
        ["element"] = [new("M3,4 H21 V20 H3 Z M3,9 H21 M8,9 V20 M11,12 H18 V17 H11 Z")],
        ["screen"] = [new("M3,4 H21 V17 H3 Z M8,21 H16 M12,17 V21")],
        ["screens"] = [new("M3,8 H16 V19 H3 Z M8,5 V3 H21 V14 H19 M7,22 H12 M9.5,19 V22")],
        ["scroll"] = [new("M4,7 V4 H15 V7 M4,17 V20 H15 V17 M4,10 V14 M15,10 V14 M20,5 V19 M17.5,7.5 L20,5 L22.5,7.5 M17.5,16.5 L20,19 L22.5,16.5")],
        ["fixed"] = [new("M3,3 H21 V21 H3 Z M7,9 V7 H9 M15,7 H17 V9 M7,15 V17 H9 M15,17 H17 V15")],
        ["freehand"] = [new("M5,6 C9,1 14,3 13,7 C21,3 23,12 17,15 C19,21 11,23 8,18 C2,20 1,13 5,11 C2,9 3,7 5,6 Z")],
        ["last"] = [new("M6,6 A8,8 0 1 1 4,14 M3,3 V8 H8 M12,7 V12 L16,14")],
        ["open"] = [new("M3,9 V5 H9 L11,7 H20 V10 M3,9 H21 L18,20 H3 Z")],
        ["save"] = [new("M4,3 H17 L21,7 V21 H3 V3 Z M7,3 V9 H16 V3 M7,21 V14 H17 V21 M13,5 V7")],
        ["copy"] = [new("M8,7 H21 V21 H8 Z M4,17 H3 V3 H16 V4")],
        ["qrcode"] = [new("M4,4 H9 V9 H4 Z M15,4 H20 V9 H15 Z M4,15 H9 V20 H4 Z M6.5,6.5 H6.6 M17.5,6.5 H17.6 M6.5,17.5 H6.6 M12,4 V6 M12,9 V12 H15 M15,15 H17 V17 M20,13 V15 M20,18 V20 H17 M12,15 V20 H14")],
        ["paste"] = [new("M8,5 H4 V21 H20 V5 H16 M8,3 H16 V7 H8 Z M8,12 H16 M8,16 H14")],
        ["settings"] = [new("M9.5,3 H14.5 L15,6 L17,7 L20,6 L22,10 L19.5,12 L19.5,14 L21,16 L18.5,20 L15.5,19 L14,21 H10 L9,18.5 L7,18 L4,19 L2,15 L4.5,13 V11 L2,9 L4.5,5 L7.5,6 Z M15.5,12 A3.5,3.5 0 1 1 8.5,12 A3.5,3.5 0 1 1 15.5,12")],
        ["select"] = [new("M5,3 L19,13 L12.5,14.5 L10,21 Z")],
        ["mosaic"] = [new("M3,3 H8 V8 H3 Z M10,3 H14 V7 H10 Z M17,3 H21 V8 H17 Z M3,11 H7 V15 H3 Z M10,10 H15 V15 H10 Z M18,11 H21 V14 H18 Z M3,18 H8 V21 H3 Z M11,18 H14 V21 H11 Z M17,17 H21 V21 H17 Z", true)],
        ["blur"] = [new("M12,2 C9,7 5,10 5,14 A7,7 0 0 0 19,14 C19,10 15,7 12,2 Z M8.5,14 C8.5,16.2 10,17.5 12,17.5")],
        ["solid"] = [new("M4,4 H20 V20 H4 Z", true)],
        ["pen"] = [new("M4,20 L5,14 L16,3 L21,8 L10,19 Z M5,14 L10,19 M13,6 L18,11 M4,20 L8,19")],
        ["arrow"] = [new("M4,20 L20,4 M9,4 H20 V15")],
        ["rectangle"] = [new("M3,5 H21 V19 H3 Z")],
        ["ellipse"] = [new("M21,12 A9,7 0 1 1 3,12 A9,7 0 1 1 21,12 Z")],
        ["line"] = [new("M4,20 L20,4")],
        ["highlight"] = [new("M6,14 L14,3 L21,8 L13,19 Z M6,14 L4,18 L8,21 L13,19 M3,22 H15")],
        ["text"] = [new("M4,4 H20 M12,4 V21 M8,21 H16 M4,4 V7 M20,4 V7")],
        ["number"] = [new("M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 Z M9,9 L12,7 V17 M9,17 H15")],
        ["crop"] = [new("M6,2 V18 H22 M2,6 H18 V22 M9,6 H18 V15")],
        ["undo"] = [new("M9,4 L3,10 L9,16 M3,10 H14 C22,10 22,20 14,20")],
        ["redo"] = [new("M15,4 L21,10 L15,16 M21,10 H10 C2,10 2,20 10,20")],
        ["rotate"] = [new("M4,9 A9,9 0 1 1 4,16 M3,3 V9 H9 M9,11 H15 V17 H9 Z")],
        ["resize"] = [new("M3,9 V3 H9 M15,21 H21 V15 M3,3 L10,10 M21,21 L14,14 M4,14 V20 H10 M14,4 H20 V10")],
        ["pin"] = [new("M9,3 H19 L17,7 V11 L20,14 H13 L8,19 V12 L5,9 H9 Z M8,19 L3,22")],
        ["print"] = [new("M7,8 V3 H17 V8 M6,17 H3 V8 H21 V17 H18 M7,14 H17 V21 H7 Z M17,11 H18")],
        ["external"] = [new("M13,3 H21 V11 M21,3 L11,13 M9,5 H3 V21 H19 V15")],
        ["menu"] = [new("M4,6 H20 M4,12 H20 M4,18 H20")],
        ["history"] = [new("M3,4 H21 V20 H3 Z M7,8 H10 V11 H7 Z M14,8 H18 M14,11 H18 M7,15 H10 M14,15 H18")],
        ["delete"] = [new("M3,6 H21 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17")],
        ["zoom-in"] = [new("M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10 Z M15,15 L21,21 M6,10 H14 M10,6 V14")],
        ["zoom-out"] = [new("M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10 Z M15,15 L21,21 M6,10 H14")],
        ["fit"] = [new("M8,3 H3 V8 M16,3 H21 V8 M3,16 V21 H8 M21,16 V21 H16 M7,8 H17 V16 H7 Z")],
        ["close"] = [new("M5,5 L19,19 M19,5 L5,19")]
        ,["chevron-down"] = [new("M6,9 L12,15 L18,9")]
        ,["more"] = [new("M4,11 H6 V13 H4 Z M11,11 H13 V13 H11 Z M18,11 H20 V13 H18 Z", true)]
        ,["shield"] = [new("M12,3 L20,6 V12 C20,16.5 16.5,19.5 12,21 C7.5,19.5 4,16.5 4,12 V6 Z M9,12 L11,14 L15,10")]
        ,["ocr"] = [new("M8,4 H4 V8 M16,4 H20 V8 M4,16 V20 H8 M20,16 V20 H16 M8,9 H16 M8,12 H16 M8,15 H13")]
        ,["send"] = [new("M4,12 L20,4 L14,20 L11,13 Z M11,13 L20,4")]
        ,["search"] = [new("M17,10.5 A6.5,6.5 0 1 1 4,10.5 A6.5,6.5 0 1 1 17,10.5 Z M15.5,15.5 L20.5,20.5")]
        ,["chevron-left"] = [new("M15,6 L9,12 L15,18")]
        ,["chevron-right"] = [new("M9,6 L15,12 L9,18")]
        ,["widen"] = [new("M11,6 L5,12 L11,18 M19,6 L13,12 L19,18")]
        ,["narrow"] = [new("M13,6 L19,12 L13,18 M5,6 L11,12 L5,18")]
    };

    public static FrameworkElement Create(string key, double size = 20, Brush? brush = null)
    {
        if (!paths.TryGetValue(key, out var parts)) throw new ArgumentException($"Unknown toolbar icon: {key}", nameof(key));
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        foreach (var part in parts)
        {
            var path = new Path
            {
                Data = Geometry.Parse(part.Data), StrokeThickness = 1.65,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false
            };
            var property = part.Filled ? Shape.FillProperty : Shape.StrokeProperty;
            if (brush != null) path.SetValue(property, brush);
            else path.SetBinding(property, new Binding(nameof(Control.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1)
            });
            canvas.Children.Add(path);
        }
        return new Viewbox
        {
            Width = size, Height = size, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = canvas, IsHitTestVisible = false
        };
    }
}
