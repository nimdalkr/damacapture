using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DamaCapture.Services;
using DamaCapture.Imaging;

namespace DamaCapture;

internal sealed partial class MainWindow
{
    private readonly HistoryStore clipboardHistory;
    private readonly HistorySnapshotWriter clipboardWriter;
    private ClipboardMonitor? clipboardMonitor;

    // Collection and retention are separate: stopping collection never hides archived items.
    private List<HistoryEntry> AllHistoryEntries => history.Entries.Concat(clipboardHistory.Entries)
        .OrderByDescending(CaptureTime).ToList();
    private HistoryStore StoreFor(Guid id) => clipboardHistory.Find(id) != null ? clipboardHistory : history;
    private HistorySnapshotWriter WriterFor(Guid id) => clipboardHistory.Find(id) != null ? clipboardWriter : historyWriter;

    // A capture is copied at once, and the clipboard follows its edits until something else is copied,
    // so masking after a capture also changes what gets pasted.
    private readonly ClipboardFollower captureClipboard = new(() => ClipboardTransfer.Sequence, image => ClipboardTransfer.SetImage(image, keepOutOfHistory: true));
    private readonly System.Windows.Threading.DispatcherTimer clipboardSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool CopyCapture()
    {
        if (document == null) return false;
        try { captureClipboard.Start(document); return true; }
        catch (Exception) { Notify("클립보드에 복사하지 못했습니다."); return false; }
    }
    private void ScheduleClipboardSync()
    {
        if (!captureClipboard.IsFollowing(document)) { captureClipboard.Stop(); return; }
        clipboardSyncTimer.Stop(); clipboardSyncTimer.Start();
    }
    private void SyncClipboard()
    {
        clipboardSyncTimer.Stop();
        try { captureClipboard.Update(document); }
        catch (Exception) { Notify("클립보드를 편집 결과로 바꾸지 못했습니다. 복사를 다시 누르세요."); }
    }
    // Leaving the window is how a paste elsewhere begins, so a pending update is written first.
    private void FlushClipboardSync() { if (clipboardSyncTimer.IsEnabled) SyncClipboard(); }
    private HistoryEntry? FindHistory(Guid id) => clipboardHistory.Find(id) ?? history.Find(id);
    private async Task FlushHistoryWritersAsync()
    {
        await Task.WhenAll(historyWriter.FlushAsync(), clipboardWriter.FlushAsync());
        await Task.Run(() => { history.RetryFailedEntries(); clipboardHistory.RetryFailedEntries(); });
    }

    private void ApplyClipboardSetting()
    {
        // Demo/test windows never attach to the user's clipboard, even if their isolated setting is enabled.
        if (demo) return;
        try
        {
            if (settings.ArchiveClipboard) clipboardMonitor ??= new ClipboardMonitor(this, ArchiveClipboardCapture);
            clipboardMonitor?.SetEnabled(settings.ArchiveClipboard);
        }
        catch (Exception ex) { Notify("복사 기록을 시작하지 못했습니다. " + ex.Message); }
    }

    private void ArchiveClipboardCapture(ClipboardCapture capture)
    {
        if (!settings.ArchiveClipboard || closingHistory || exiting) return;
        try
        {
            var entry = clipboardHistory.RecordClipboard(capture.Kind, capture.CopiedAt, capture.WindowTitle,
                capture.ApplicationName, capture.Text, capture.FilePaths,
                capture.Image?.PixelWidth ?? 0, capture.Image?.PixelHeight ?? 0);
            if (capture.Image != null) clipboardWriter.Enqueue(entry.Id, capture.Image);
            RefreshHistoryIfVisible();
            if (clipboardHistory.LastError != null) Notify("복사 기록을 저장하지 못했습니다. 이번 실행 동안은 유지됩니다.");
        }
        catch (Exception ex) { Notify("복사 기록을 남기지 못했습니다. " + ex.Message); }
    }

    private bool CopyHistoryEntry(HistoryEntry entry)
    {
        var copied = false;
        Try(() =>
        {
            if (entry.Kind == HistoryKind.ClipboardText) ClipboardTransfer.SetText(entry.Text);
            else if (entry.Kind == HistoryKind.ClipboardFiles)
            {
                // Missing sources remain visible in the archive, but are never silently dropped from a copy.
                if (entry.FilePaths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
                { Notify("원본이 없는 파일이 있습니다. 기록을 열어 경로를 확인하세요."); return; }
                ClipboardTransfer.SetFiles(entry.FilePaths);
            }
            else
            {
                if (currentHistoryId == entry.Id) { surface.CompletePendingEdit(); PreserveHistoryImage(); }
                var pixels = currentHistoryId == entry.Id ? document?.Render()
                    : sessionDocuments.TryGet(entry.Id, out var session) ? session.Document.Render()
                    : WriterFor(entry.Id).PendingOrFailed(entry.Id);
                var latest = FindHistory(entry.Id) ?? entry;
                if (pixels == null && File.Exists(latest.ImagePath)) pixels = ImageFiles.Load(latest.ImagePath);
                if (pixels == null) { Notify("다시 복사할 이미지가 없습니다."); return; }
                ClipboardTransfer.SetImage(pixels);
            }
            copied = true;
            Toast("복사했습니다");
        });
        return copied;
    }

    private void OpenClipboardPreview(HistoryEntry entry)
    {
        new ClipboardPreviewWindow(this, entry, () => CopyHistoryEntry(entry)).ShowDialog();
    }

    private void AddClipboardDemoEntries()
    {
        var now = DateTimeOffset.Now;
        clipboardHistory.RecordClipboard(HistoryKind.ClipboardText, now.AddMinutes(-2), "작업 메모 - 메모장", "메모장",
            "캡처 기록 개선안\n\n필요한 내용을 복사해 두고 나중에 다시 찾기.\n날짜와 작업하던 창을 함께 기억하기.");
        clipboardHistory.RecordClipboard(HistoryKind.ClipboardText, now.AddMinutes(-9), "참고 자료 - 브라우저", "브라우저", "https://example.com/design-reference");
        clipboardHistory.RecordClipboard(HistoryKind.ClipboardFiles, now.AddMinutes(-18), "작업 자료 - 파일 탐색기", "파일 탐색기",
            filePaths: new[] { Path.Combine(Path.GetTempPath(), "담아-예시", "화면 기획.pdf"), Path.Combine(Path.GetTempPath(), "담아-예시", "회의 메모.txt") });
        var pixels = ImageFactory.Sample();
        var image = clipboardHistory.RecordClipboard(HistoryKind.ClipboardImage, now.AddHours(-1), "화면 참고 이미지", "사진", width: pixels.PixelWidth, height: pixels.PixelHeight);
        clipboardWriter.Enqueue(image.Id, pixels);
    }
}
