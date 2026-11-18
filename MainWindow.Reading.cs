using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DamaCapture.Services;

namespace DamaCapture;

/// <summary>
/// Text extraction lays what was read over the image: the words stay lit, the rest dims, and dragging across them
/// picks a run in reading order, as on paper. The pick (or everything) is copied, or opened in the text window to
/// correct it or hand it to an AI chat. Reading happens on this PC and the image itself is not changed.
/// </summary>
internal sealed partial class MainWindow
{
    private TextRecognition.Reading? reading;
    private (int Line, TextRecognition.Word Word)[] readingWords = [];
    // The words belong to one image at one revision; another image, or any edit, makes them meaningless.
    private Imaging.ImageDocument? readingDocument;
    private long readingRevision;
    private Border? readingBar;
    private Button? readingCopy;

    private Border BuildReadingBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = Ui.Icon("ocr", 18, Ui.Text); icon.Margin = new Thickness(0, 0, 10, 0); row.Children.Add(icon);
        readingCopy = Ui.Button("모두 복사", CopyReading, true); readingCopy.Height = 30; readingCopy.Padding = new Thickness(14, 0, 14, 0); readingCopy.Margin = new Thickness(0, 0, 4, 0); row.Children.Add(readingCopy);
        var window = Ui.Button("텍스트 창", OpenReadingWindow); window.Height = 30; window.Padding = new Thickness(12, 0, 12, 0); window.Margin = new Thickness(0, 0, 4, 0);
        window.ToolTip = "고치거나 AI에서 편집하기"; row.Children.Add(window);
        var close = Ui.Button("닫기", LeaveReading); Ui.SetGhost(close, false, false); close.Height = 30; close.Padding = new Thickness(10, 0, 10, 0); close.ToolTip = "닫기 (Esc)"; row.Children.Add(close);
        readingBar = new Border
        {
            Child = row, Background = Ui.Panel, BorderBrush = Ui.Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8, 8, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(readingBar, "읽은 글자");
        return readingBar;
    }

    /// <summary>Shows what was read in an area whose top-left sits at <paramref name="origin"/> in image pixels.</summary>
    private void ShowReading(TextRecognition.Reading found, Point origin)
    {
        if (document == null) return;
        var words = new List<(int Line, TextRecognition.Word Word)>();
        var lines = new List<(int Start, int Count)>();
        for (var i = 0; i < found.Lines.Count; i++)
        {
            lines.Add((words.Count, found.Lines[i].Words.Count));
            foreach (var word in found.Lines[i].Words)
                words.Add((i, word with { Bounds = new Rect(word.Bounds.X + origin.X, word.Bounds.Y + origin.Y, word.Bounds.Width, word.Bounds.Height) }));
        }
        reading = found; readingWords = words.ToArray(); readingDocument = document; readingRevision = document.Revision;
        surface.Tool = "Read"; surface.Cursor = Cursors.IBeam;
        surface.SetReading(readingWords.Select(item => item.Word.Bounds).ToArray(), lines);
        CollapseCodes(); UpdateCodeLayerVisibility(); RefreshToolButtons(); UpdateToolProperties(false);
        if (sensitiveCard != null) sensitiveCard.Visibility = Visibility.Collapsed;
        if (readingBar != null) { readingBar.Visibility = Visibility.Visible; Motion.Enter(readingBar, y: -6, duration: 160); }
        UpdateReadingBar();
        // The options strip already says how to pick; the status line says how to leave.
        Notify("Esc를 누르면 읽은 글자를 닫습니다.");
    }

    private void UpdateReadingBar()
    {
        if (readingCopy == null) return;
        var label = surface.ReadingSelection == null ? "모두 복사" : "복사";
        readingCopy.Content = label; AutomationProperties.SetName(readingCopy, label);
    }

    private string ReadingText()
    {
        if (reading == null) return "";
        var picked = surface.ReadingSelection is var (from, to) ? readingWords.Skip(from).Take(to - from + 1) : readingWords;
        return reading.TextOf(picked);
    }

    private void CopyReading()
    {
        var text = ReadingText();
        if (text.Length == 0) return;
        Try(() => { ClipboardTransfer.SetText(text); Toast("복사했습니다"); });
    }

    private void OpenReadingWindow()
    {
        if (reading == null) return;
        var text = ReadingText();
        var lines = text.Split(Environment.NewLine).Length;
        new TextResultWindow(this, new TextRecognition.Result(text, reading.LanguageTag, lines, readingWords.Length), ChatServices, SendTextToChat).ShowDialog();
    }

    /// <summary>Drops the overlay without choosing a tool; for callers that are about to choose one.</summary>
    private void ClearReading()
    {
        if (reading == null && !surface.IsReading) return;
        reading = null; readingWords = []; readingDocument = null;
        surface.SetReading([], []);
        if (readingBar != null) readingBar.Visibility = Visibility.Collapsed;
        UpdateSensitiveCard();
    }

    private void LeaveReading()
    {
        var wasReading = surface.Tool == "Read";
        ClearReading();
        if (wasReading) SelectTool(toolBeforeExtract is null or "Extract" or "Read" ? "Select" : toolBeforeExtract);
        UpdateCodeLayerVisibility();
    }
}
