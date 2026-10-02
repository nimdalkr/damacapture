using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture;

/// <summary>
/// After an image arrives, faces and sensitive text are located on this PC and offered as one decision:
/// mask them all, or not. Nothing is masked automatically and nothing about the findings is stored.
/// </summary>
internal sealed partial class MainWindow
{
    private readonly List<Finding> findings = new();
    private ImageDocument? scannedDocument;
    private int scanGeneration;
    private Border? sensitiveCard;
    private TextBlock? sensitiveText;

    private void ScanSensitiveIfNeeded()
    {
        if (document == null || ReferenceEquals(scannedDocument, document)) return;
        scannedDocument = document;
        ClearFindings(); ClearCodes();
        // Availability is decided by the helper, so this process never loads the recognizers.
        if (!settings.DetectSensitive && !settings.ReadCodes) return;
        var generation = ++scanGeneration;
        var target = document;
        BitmapSource composed = target.Render();
        surface.Sweep(); scanStarted = Environment.TickCount64;
        // Start once the new editor has drawn its first frame.
        Dispatcher.BeginInvoke(() => { if (generation == scanGeneration) _ = ScanAsync(generation, target, composed); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    private long scanStarted;

    private async Task ScanAsync(int generation, ImageDocument target, BitmapSource composed)
    {
        AnalysisHost.Analysis found;
        try { found = await AnalysisHost.AnalyzeAsync(composed, settings.DetectSensitive, settings.ReadCodes); }
        catch (Exception) { return; }
        // Findings land after the sweep has passed, so outlines never appear ahead of the line.
        var wait = EditorSurface.SweepDuration + 60 - (int)(Environment.TickCount64 - scanStarted);
        if (Motion.Enabled && wait > 0) await Task.Delay(wait);
        await Dispatcher.InvokeAsync(() =>
        {
            // The user may have moved on to another image while the scan ran.
            if (generation != scanGeneration || !ReferenceEquals(document, target)) return;
            if (found.Findings.Count > 0)
            {
                findings.Clear(); findings.AddRange(found.Findings);
                surface.Hints = findings.Select(finding => (finding.Bounds, finding.Label)).ToArray();
                UpdateSensitiveCard();
            }
            ShowCodes(found.Codes);
            if (found.Findings.Count > 0 || found.Codes.Count > 0) surface.RevealHints();
        });
    }

    private void ClearFindings()
    {
        findings.Clear();
        surface.Hints = [];
        UpdateSensitiveCard();
    }

    private Border BuildSensitiveCard()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = Ui.Icon("shield", 18, Ui.RedText); icon.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(icon);
        sensitiveText = Ui.Label("", 13, Ui.Text); sensitiveText.TextWrapping = TextWrapping.NoWrap; sensitiveText.Margin = new Thickness(0, 0, 14, 0); row.Children.Add(sensitiveText);
        var mask = Ui.Button("가리기", () => MaskFindings(findings.ToArray()), true); mask.Height = 30; mask.Padding = new Thickness(14, 0, 14, 0); mask.Margin = new Thickness(0, 0, 4, 0); row.Children.Add(mask);
        var dismiss = Ui.Button("아니요", ClearFindings); Ui.SetGhost(dismiss, false, false); dismiss.Height = 30; dismiss.Padding = new Thickness(10, 0, 10, 0); row.Children.Add(dismiss);
        sensitiveCard = new Border
        {
            Child = row, Background = Ui.Panel, BorderBrush = Ui.Brush("#DAD5CE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8, 8, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(sensitiveCard, "민감 정보 감지 결과");
        UpdateSensitiveCard();
        return sensitiveCard;
    }

    private void UpdateSensitiveCard()
    {
        if (sensitiveCard == null || sensitiveText == null) return;
        if (findings.Count == 0) { sensitiveCard.Visibility = Visibility.Collapsed; return; }
        var summary = string.Join(", ", findings.GroupBy(finding => finding.Kind).Select(group => $"{group.First().Label} {group.Count()}곳"));
        sensitiveText.Text = summary + "을 찾았습니다";
        var wasVisible = sensitiveCard.Visibility == Visibility.Visible;
        sensitiveCard.Visibility = Visibility.Visible;
        if (!wasVisible) Motion.Enter(sensitiveCard, y: -6, duration: 160);
    }

    /// <summary>Each finding becomes an ordinary mosaic edit, so it can still be moved, resized or undone.</summary>
    private void MaskFindings(Finding[] selected)
    {
        if (document == null || selected.Length == 0) return;
        Try(() =>
        {
            surface.CompletePendingEdit();
            var target = document;
            foreach (var finding in selected)
            {
                var size = Math.Min(finding.Bounds.Width, finding.Bounds.Height);
                var strength = (int)Math.Clamp(finding.Kind == FindingKind.Face ? size / 6 : size / 3, 8, 48);
                target.Add(new EditOperation { Kind = EditKind.Mosaic, Bounds = finding.Bounds, Strength = strength });
            }
            var masked = selected.ToArray(); var revision = target.Revision;
            ClearFindings();
            dirty = true; surface.SelectedId = null; RefreshEditor();
            Toast($"{masked.Length}곳을 가렸습니다", "되돌리기", () =>
            {
                // Only step back over the masks if nothing else was edited since.
                if (!ReferenceEquals(document, target) || target.Revision != revision) return;
                for (var i = 0; i < masked.Length && target.CanUndo; i++) target.Undo();
                Notify("되돌렸습니다");
                findings.AddRange(masked); surface.Hints = findings.Select(finding => (finding.Bounds, finding.Label)).ToArray(); surface.RevealHints(); UpdateSensitiveCard();
                dirty = true; surface.SelectedId = null; RefreshEditor();
            });
        });
    }
}
