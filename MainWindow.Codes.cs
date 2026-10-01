using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using DamaCapture.Services;

namespace DamaCapture;

/// <summary>
/// QR codes found in a capture get a bubble beside them with what they say. A web link can be opened in the
/// browser; anything else is only shown and copied, so a code never launches something on its own. Bubbles are
/// window chrome, not part of the image: saving, copying and printing never include them.
/// </summary>
internal sealed partial class MainWindow
{
    private readonly List<CodeFinding> codes = new();
    private Canvas? codeLayer;
    /// <summary>The image size the bubbles were found on; a crop makes their positions meaningless.</summary>
    private Size codeImageSize;
    private EventHandler? codeLayoutHandler;
    private const double BubbleGap = 12, BubbleWidth = 300;

    private Canvas BuildCodeLayer()
    {
        // No background, so only the bubbles themselves take clicks; the canvas underneath keeps working.
        codeLayer = new Canvas { ClipToBounds = true };
        codeLayoutHandler ??= (_, _) => PlaceBubbles();
        surface.LayoutUpdated -= codeLayoutHandler; surface.LayoutUpdated += codeLayoutHandler;
        if (canvasScroll != null) canvasScroll.ScrollChanged += (_, _) => PlaceBubbles();
        return codeLayer;
    }

    private void ShowCodes(IReadOnlyList<CodeFinding> found)
    {
        ClearCodes();
        if (found.Count == 0 || document == null || codeLayer == null) return;
        codes.AddRange(found);
        codeImageSize = new Size(document.Width, document.Height);
        surface.Marks = codes.Select(code => code.Bounds).ToArray();
        foreach (var code in codes)
        {
            var bubble = BuildBubble(code);
            codeLayer.Children.Add(bubble);
            Motion.Enter(bubble, x: -6, duration: 180);
        }
        PlaceBubbles();
    }

    private void ClearCodes()
    {
        codes.Clear();
        surface.Marks = [];
        codeLayer?.Children.Clear();
    }

    private void CloseBubble(FrameworkElement bubble)
    {
        if (bubble.Tag is CodeFinding code) codes.Remove(code);
        codeLayer?.Children.Remove(bubble);
        surface.Marks = codes.Select(other => other.Bounds).ToArray();
    }

    private FrameworkElement BuildBubble(CodeFinding code)
    {
        var link = code.Link;
        var column = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var close = Ui.IconButton("닫기", "close", () => { }, compact: true);
        close.Width = 24; close.Height = 24; close.Padding = new Thickness(4); close.Margin = new Thickness(8, -2, -4, 0);
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var icon = Ui.Icon("qrcode", 16, Ui.Muted); icon.Margin = new Thickness(0, 0, 6, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left); header.Children.Add(icon);
        header.Children.Add(Ui.Label(link != null ? "QR 코드 · 링크" : "QR 코드", 12, Ui.Muted));
        column.Children.Add(header);
        // The full text is shown, with the host set apart, so the user sees where a link really goes before opening it.
        var text = new TextBlock { FontSize = 13, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 54, Margin = new Thickness(0, 0, 0, 10) };
        if (link != null)
        {
            text.Inlines.Add(new Run(link.GetLeftPart(UriPartial.Scheme)) { Foreground = Ui.Muted });
            text.Inlines.Add(new Run(link.IsDefaultPort ? link.Host : $"{link.Host}:{link.Port}") { FontWeight = FontWeights.SemiBold });
            var rest = link.PathAndQuery + link.Fragment;
            if (rest != "/") text.Inlines.Add(new Run(rest));
        }
        else text.Text = code.Text;
        text.ToolTip = code.Text;
        column.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (link != null)
        {
            var open = Ui.Button("브라우저에서 열기", () => Try(() => OpenBrowser(link.AbsoluteUri)), true);
            open.Height = 28; open.Padding = new Thickness(12, 0, 12, 0); open.Margin = new Thickness(0, 0, 6, 0); actions.Children.Add(open);
        }
        var copy = Ui.Button("복사", () => Try(() => { ClipboardTransfer.SetText(code.Text); Toast("복사했습니다"); }));
        Ui.SetGhost(copy, false, false); copy.Height = 28; copy.Padding = new Thickness(10, 0, 10, 0); actions.Children.Add(copy);
        column.Children.Add(actions);
        var card = new Border
        {
            Child = column, Background = Ui.Panel, BorderBrush = Ui.Brush("#DAD5CE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 10, 10), Margin = new Thickness(BubbleTail), MaxWidth = BubbleWidth
        };
        // The tail is drawn after the card so its base covers the card's border on whichever side it sits.
        var tail = new Grid { IsHitTestVisible = false, Tag = "" };
        tail.Children.Add(new Path { Fill = Ui.Panel });
        tail.Children.Add(new Path { Stroke = Ui.Brush("#DAD5CE"), StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var bubble = new Grid { Tag = code, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        bubble.Children.Add(card); bubble.Children.Add(tail);
        AutomationProperties.SetName(bubble, link != null ? "QR 코드 링크" : "QR 코드 내용");
        close.Click += (_, _) => CloseBubble(bubble);
        PointTail(tail, "left");
        return bubble;
    }

    private const double BubbleTail = 8;

    /// <summary>Sets which edge of the bubble carries the tail: "left" or "right" point sideways at the code, "top" points up at it.</summary>
    private static void PointTail(Grid tail, string side)
    {
        if (Equals(tail.Tag, side)) return;
        tail.Tag = side;
        // The base reaches one pixel into the card so the fill hides the border there; only the two outer edges are stroked.
        const double depth = BubbleTail + 1, half = 8;
        var (a, tip, b) = side switch
        {
            "left" => (new Point(depth, 0), new Point(0, half), new Point(depth, half * 2)),
            "right" => (new Point(0, 0), new Point(depth, half), new Point(0, half * 2)),
            _ => (new Point(0, depth), new Point(half, 0), new Point(half * 2, depth))
        };
        ((Path)tail.Children[0]).Data = new PathGeometry([new PathFigure(a, [new PolyLineSegment([tip, b], true)], true)]);
        ((Path)tail.Children[1]).Data = new PathGeometry([new PathFigure(a, [new PolyLineSegment([tip, b], true)], false)]);
        tail.HorizontalAlignment = side == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        tail.VerticalAlignment = VerticalAlignment.Top;
        tail.Width = side == "top" ? half * 2 : depth; tail.Height = side == "top" ? depth : half * 2;
        tail.Margin = side == "top" ? new Thickness(24, 0, 0, 0) : new Thickness(0, 20, 0, 0);
    }

    private void PlaceBubbles()
    {
        if (codeLayer == null || codes.Count == 0 || document == null) return;
        if (document.Width != codeImageSize.Width || document.Height != codeImageSize.Height) { ClearCodes(); return; }
        if (!surface.IsVisible || !codeLayer.IsVisible || PresentationSource.FromVisual(surface) == null) return;
        GeneralTransform transform;
        try { transform = surface.TransformToVisual(codeLayer); }
        catch (InvalidOperationException) { return; }
        var viewport = new Rect(0, 0, codeLayer.ActualWidth, codeLayer.ActualHeight);
        foreach (var child in codeLayer.Children.OfType<Grid>())
        {
            if (child.Tag is not CodeFinding code) continue;
            var box = transform.TransformBounds(code.Bounds);
            var tail = child.Children.OfType<Grid>().First();
            var width = child.ActualWidth > 0 ? child.ActualWidth : child.DesiredSize.Width;
            var height = child.ActualHeight > 0 ? child.ActualHeight : child.DesiredSize.Height;
            // Beside the code when there is room, to the right first; otherwise below it.
            double x, y = box.Top - BubbleTail - 8;
            if (box.Right + BubbleGap + width <= viewport.Right) { x = box.Right + BubbleGap - BubbleTail; PointTail(tail, "left"); }
            else if (box.Left - BubbleGap - width >= viewport.Left) { x = box.Left - BubbleGap - width + BubbleTail; PointTail(tail, "right"); }
            else { x = Math.Clamp(box.Left - 16, viewport.Left, Math.Max(viewport.Left, viewport.Right - width)); y = box.Bottom + BubbleGap - BubbleTail; PointTail(tail, "top"); }
            y = Math.Clamp(y, 4, Math.Max(4, viewport.Bottom - height - 4));
            Canvas.SetLeft(child, Math.Round(x)); Canvas.SetTop(child, Math.Round(y));
            child.Visibility = box.IntersectsWith(viewport) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
