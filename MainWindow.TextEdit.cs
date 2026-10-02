using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DamaCapture.Imaging;

namespace DamaCapture;

/// <summary>
/// Text annotations are typed where they will sit. Clicking with the text tool opens an editor at that spot;
/// clicking text that is already placed (or double-clicking it with the select tool) opens it again. Enter
/// commits, Shift+Enter starts a new line, Esc drops the change, and leaving the editor commits.
/// </summary>
internal sealed partial class MainWindow
{
    private Canvas? textLayer;
    private TextBox? textEditor;
    /// <summary>Top-left of the text in image pixels.</summary>
    private Point textAnchor;
    /// <summary>The annotation being changed; null while new text is typed.</summary>
    private EditOperation? textEditing;
    /// <summary>The document the editor was opened on; the open image may be replaced while it is still up.</summary>
    private ImageDocument? textDocument;
    private bool textCommitting;

    /// <summary>Set while a dialog that takes the focus is choosing something for the open editor.</summary>
    private bool keepTextEditor;

    private Canvas BuildTextLayer()
    {
        // A rebuilt page starts without an editor, and no document may stay hidden because of one.
        CloseTextEditor();
        return textLayer = new Canvas { ClipToBounds = true };
    }

    private static double TextPixels(double stroke) => Math.Max(14, stroke * 4 + 10);

    private void BeginTextEdit(Point at, EditOperation? existing)
    {
        if (document == null || textLayer == null) return;
        CommitTextEdit();
        if (existing != null)
        {
            // The annotation's own look moves into the options, so editing keeps it and changes apply to it.
            surface.Font = string.IsNullOrWhiteSpace(existing.Font) ? "Segoe UI" : existing.Font;
            surface.Bold = existing.Bold; surface.Stroke = existing.Stroke; surface.Ink = existing.Color;
            SyncTextOptions();
            // Anything still queued for the record, the clipboard or the followed file is written before the text leaves the image.
            PreserveHistoryImage(); FlushClipboardSync(); FlushAutoSave();
            textDocument = document;
            try { document.Hidden = existing.Id; surface.Refresh(); }
            catch (Exception) { CloseTextEditor(); surface.Refresh(); throw; }
            at = existing.Bounds.TopLeft;
        }
        textEditing = existing; textAnchor = at; textDocument = document;
        var editor = new TextBox
        {
            Text = existing?.Text ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinWidth = 12,
            // No padding and no minimum height, so the letters sit exactly where they will be drawn.
            Padding = new Thickness(0), MinHeight = 0, Template = TextEditorTemplate(), FocusVisualStyle = null, Cursor = Cursors.IBeam
        };
        AutomationProperties.SetName(editor, "텍스트 입력");
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; CancelTextEdit(); surface.Focus(); }
            // Shift+Enter falls through to the editor as a line break.
            else if (e.Key is Key.Enter or Key.Return && Keyboard.Modifiers != ModifierKeys.Shift) { e.Handled = true; CommitTextEdit(); surface.Focus(); }
        };
        editor.LostKeyboardFocus += (_, e) =>
        {
            // Changing the font, size or color keeps the editor open; anything else ends it.
            if (keepTextEditor || (e.NewFocus is DependencyObject target && IsTextOption(target))) return;
            Dispatcher.BeginInvoke(() => { if (!keepTextEditor && ReferenceEquals(textEditor, editor) && !editor.IsKeyboardFocusWithin && !(Keyboard.FocusedElement is DependencyObject now && IsTextOption(now))) CommitTextEdit(); });
        };
        textEditor = editor;
        textLayer.Children.Add(editor);
        StyleTextEditor(); PlaceTextEditor();
        Notify("Enter로 확정 · Shift+Enter 줄바꿈 · Esc 취소");
        Dispatcher.BeginInvoke(() => { if (ReferenceEquals(textEditor, editor)) { editor.Focus(); editor.CaretIndex = editor.Text.Length; } }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // A bare frame around the letters: no fill, so the image stays visible under what is being typed.
    private static ControlTemplate TextEditorTemplate()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(FocusableProperty, false);
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BorderBrushProperty, Ui.Red); frame.SetValue(Border.BorderThicknessProperty, new Thickness(TextEditorBorder));
        frame.SetValue(Border.BackgroundProperty, Ui.Brush("#14FFFFFF")); frame.SetValue(Border.PaddingProperty, new Thickness(0));
        frame.AppendChild(host);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = frame };
    }
    // The text view inside a TextBox insets its letters by two pixels on each side.
    private const double TextEditorBorder = 1, TextViewInset = 2;

    private bool IsTextOption(DependencyObject target)
    {
        if (target is ComboBoxItem) return true;
        DependencyObject? current = target;
        while (current != null)
        {
            if (ReferenceEquals(current, toolProperties)) return true;
            current = current is Visual ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>Applies the current font, size, weight and color to the open editor and hands the keyboard back to it.</summary>
    private void StyleTextEditor()
    {
        if (textEditor == null) return;
        textEditor.FontFamily = new FontFamily(surface.Font);
        textEditor.FontWeight = surface.Bold ? FontWeights.Bold : FontWeights.Normal;
        var brush = new SolidColorBrush(surface.Ink); brush.Freeze();
        textEditor.Foreground = brush; textEditor.CaretBrush = brush;
        textEditor.FontSize = TextPixels(surface.Stroke) * zoom;
    }
    private void RestyleTextEditor()
    {
        if (textEditor == null) return;
        StyleTextEditor(); PlaceTextEditor();
        var editor = textEditor;
        Dispatcher.BeginInvoke(() => { if (ReferenceEquals(textEditor, editor)) editor.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PlaceTextEditor()
    {
        if (textEditor == null || textLayer == null || document == null) return;
        if (!surface.IsVisible || !textLayer.IsVisible || PresentationSource.FromVisual(surface) == null) return;
        Point origin;
        try { origin = surface.TransformToVisual(textLayer).Transform(textAnchor); }
        catch (InvalidOperationException) { return; }
        var size = TextPixels(surface.Stroke) * zoom;
        if (Math.Abs(textEditor.FontSize - size) > 0.01) textEditor.FontSize = size;
        Canvas.SetLeft(textEditor, Math.Round(origin.X - TextEditorBorder - TextViewInset));
        Canvas.SetTop(textEditor, Math.Round(origin.Y - TextEditorBorder));
    }

    private void CloseTextEditor()
    {
        if (textEditor != null) textLayer?.Children.Remove(textEditor);
        textEditor = null; textEditing = null;
        // Cleared on the document that was being edited, which is not necessarily the one open now.
        if (textDocument != null) textDocument.Hidden = null;
        textDocument = null;
    }

    private void CommitTextEdit()
    {
        if (textEditor == null || document == null || textCommitting) return;
        textCommitting = true;
        try
        {
            var existing = textEditing;
            var text = textEditor.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
            var sameDocument = ReferenceEquals(textDocument, document);
            CloseTextEditor();
            // The image was replaced while the editor was open: there is nothing left to place the text on.
            if (!sameDocument) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                // Emptied text is deleted; new text that was never typed leaves nothing behind.
                if (existing != null) { document.Remove(existing.Id); surface.SelectedId = null; dirty = true; }
                RefreshEditor();
                return;
            }
            var extent = ImageDocument.MeasureText(text, surface.Stroke, surface.Font, surface.Bold);
            var x = Math.Clamp(textAnchor.X, 0, Math.Max(0, document.Width - 1)); var y = Math.Clamp(textAnchor.Y, 0, Math.Max(0, document.Height - 1));
            var bounds = new Rect(x, y, Math.Max(1, Math.Min(document.Width - x, extent.Width)), Math.Max(1, Math.Min(document.Height - y, extent.Height)));
            if (existing != null)
            {
                document.Update(existing with { Text = text, Bounds = bounds, Font = surface.Font, Bold = surface.Bold, Stroke = surface.Stroke, Color = surface.Ink });
                surface.SelectedId = existing.Id;
            }
            else
            {
                var added = new EditOperation { Kind = EditKind.Text, Text = text, Bounds = bounds, Font = surface.Font, Bold = surface.Bold, Stroke = surface.Stroke, Color = surface.Ink };
                document.Add(added); surface.SelectedId = added.Id;
            }
            dirty = true; RefreshEditor();
        }
        finally { textCommitting = false; }
    }

    private void CancelTextEdit()
    {
        if (textEditor == null) return;
        CloseTextEditor();
        // The text is back in the image; let the record and clipboard catch up if they were waiting.
        if (document != null && ReferenceEquals(surface.Document, document)) RefreshEditor();
    }

    /// <summary>Brings the option controls in line with the surface after an existing annotation was opened.</summary>
    private void SyncTextOptions()
    {
        updating = true;
        try
        {
            if (strokeChoice != null) strokeChoice.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { 2d, 4d, 8d, 12d }, surface.Stroke));
            if (fontChoice != null)
            {
                var fonts = FontChoices();
                var index = Array.FindIndex(fonts, choice => string.Equals(choice.Source, surface.Font, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) fontChoice.SelectedIndex = index;
            }
        }
        finally { updating = false; }
        RefreshBoldButton(false); RefreshInkSwatches();
    }
}
