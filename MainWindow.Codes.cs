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
/// window chrome, not part of the image: saving, copying and printing never include them. Once the user starts
/// working on the image a bubble folds into a small chip on its code, so it never sits on what is being edited.
/// </summary>
internal sealed partial class MainWindow
{
    private sealed class CodeBubble(CodeFinding code, Grid root, FrameworkElement card, Grid tail, Button chip)
    {
        public CodeFinding Code { get; } = code;
        public Grid Root { get; } = root;
        public FrameworkElement Card { get; } = card;
        public Grid Tail { get; } = tail;
        public Button Chip { get; } = chip;
        public bool Expanded { get; set; } = true;
    }

    private readonly List<CodeBubble> bubbles = new();
    private Canvas? codeLayer;
    /// <summary>The image size the bubbles were found on; a crop makes their positions meaningless.</summary>
    private Size codeImageSize;
    private EventHandler? overlayLayoutHandler;
    private const double BubbleGap = 12, BubbleWidth = 300, BubbleTail = 8, ChipSize = 28;

    private Canvas BuildCodeLayer()
    {
        // No background, so only the bubbles themselves take clicks; the canvas underneath keeps working.
        codeLayer = new Canvas { ClipToBounds = true };
        overlayLayoutHandler ??= (_, _) => PlaceOverlays();
        surface.LayoutUpdated -= overlayLayoutHandler; surface.LayoutUpdated += overlayLayoutHandler;
        if (canvasScroll != null) canvasScroll.ScrollChanged += (_, _) => PlaceOverlays();
        return codeLayer;
    }

    /// <summary>Overlays on the canvas follow the image through zoom, scroll and panel changes.</summary>
    private void PlaceOverlays() { PlaceBubbles(); PlaceTextEditor(); }

    private void ShowCodes(IReadOnlyList<CodeFinding> found)
    {
        ClearCodes();
        if (found.Count == 0 || document == null || codeLayer == null) return;
        codeImageSize = new Size(document.Width, document.Height);
        foreach (var code in found)
        {
            var bubble = BuildBubble(code);
            bubbles.Add(bubble); codeLayer.Children.Add(bubble.Root);
            Motion.Enter(bubble.Root, x: -6, duration: 180);
        }
        surface.Marks = bubbles.Select(bubble => bubble.Code.Bounds).ToArray();
        UpdateCodeLayerVisibility();
        PlaceBubbles();
    }

    private void ClearCodes()
    {
        bubbles.Clear();
        surface.Marks = [];
        codeLayer?.Children.Clear();
    }

    private void CloseBubble(CodeBubble bubble)
    {
        bubbles.Remove(bubble);
        codeLayer?.Children.Remove(bubble.Root);
        surface.Marks = bubbles.Select(other => other.Code.Bounds).ToArray();
    }

    /// <summary>Folds every open bubble into its chip; called when the user turns to the image.</summary>
    private bool CollapseCodes()
    {
        var any = false;
        foreach (var bubble in bubbles.Where(bubble => bubble.Expanded)) { SetBubbleExpanded(bubble, false); any = true; }
        if (any) PlaceBubbles();
        return any;
    }

    private void SetBubbleExpanded(CodeBubble bubble, bool expanded)
    {
        bubble.Expanded = expanded;
        bubble.Card.Visibility = bubble.Tail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        bubble.Chip.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        if (expanded) Motion.Enter(bubble.Root, x: -4, duration: 140);
    }

    // Text extraction drags over the whole image, so nothing may sit on it in that mode.
    private void UpdateCodeLayerVisibility()
    {
        if (codeLayer != null) codeLayer.Visibility = surface.Tool == "Extract" ? Visibility.Collapsed : Visibility.Visible;
    }

    private CodeBubble BuildBubble(CodeFinding code)
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
        // Folded, only this chip remains on the code's corner.
        var chip = Ui.IconButton(link != null ? "QR 코드 링크 보기" : "QR 코드 내용 보기", "qrcode", () => { }, compact: true);
        chip.Width = ChipSize; chip.Height = ChipSize; chip.Padding = new Thickness(5); chip.Visibility = Visibility.Collapsed;
        Ui.SetPrimary(chip, false, false);
        var root = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Add(card); root.Children.Add(tail); root.Children.Add(chip);
        AutomationProperties.SetName(root, link != null ? "QR 코드 링크" : "QR 코드 내용");
        var bubble = new CodeBubble(code, root, card, tail, chip);
        close.Click += (_, _) => CloseBubble(bubble);
        chip.Click += (_, _) => { SetBubbleExpanded(bubble, true); PlaceBubbles(); };
        PointTail(tail, "left");
        return bubble;
    }

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
        if (codeLayer == null || bubbles.Count == 0 || document == null) return;
        if (document.Width != codeImageSize.Width || document.Height != codeImageSize.Height) { ClearCodes(); return; }
        if (!surface.IsVisible || !codeLayer.IsVisible || PresentationSource.FromVisual(surface) == null) return;
        GeneralTransform transform;
        try { transform = surface.TransformToVisual(codeLayer); }
        catch (InvalidOperationException) { return; }
        var viewport = new Rect(0, 0, codeLayer.ActualWidth, codeLayer.ActualHeight);
        foreach (var bubble in bubbles)
        {
            var root = bubble.Root;
            var box = transform.TransformBounds(bubble.Code.Bounds);
            root.Visibility = box.IntersectsWith(viewport) ? Visibility.Visible : Visibility.Collapsed;
            if (!bubble.Expanded)
            {
                // The chip sits on the code's top-right corner, half outside it.
                Canvas.SetLeft(root, Math.Round(Math.Clamp(box.Right - ChipSize / 2, 0, Math.Max(0, viewport.Right - ChipSize))));
                Canvas.SetTop(root, Math.Round(Math.Clamp(box.Top - ChipSize / 2, 0, Math.Max(0, viewport.Bottom - ChipSize))));
                continue;
            }
            var width = root.ActualWidth > 0 ? root.ActualWidth : root.DesiredSize.Width;
            var height = root.ActualHeight > 0 ? root.ActualHeight : root.DesiredSize.Height;
            // Beside the code when there is room, to the right first; otherwise below it.
            double x, y = box.Top - BubbleTail - 8;
            if (box.Right + BubbleGap + width <= viewport.Right) { x = box.Right + BubbleGap - BubbleTail; PointTail(bubble.Tail, "left"); }
            else if (box.Left - BubbleGap - width >= viewport.Left) { x = box.Left - BubbleGap - width + BubbleTail; PointTail(bubble.Tail, "right"); }
            else { x = Math.Clamp(box.Left - 16, viewport.Left, Math.Max(viewport.Left, viewport.Right - width)); y = box.Bottom + BubbleGap - BubbleTail; PointTail(bubble.Tail, "top"); }
            y = Math.Clamp(y, 4, Math.Max(4, viewport.Bottom - height - 4));
            Canvas.SetLeft(root, Math.Round(x)); Canvas.SetTop(root, Math.Round(y));
        }
    }
}
