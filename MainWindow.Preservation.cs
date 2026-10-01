using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture;

internal sealed partial class MainWindow
{
    private readonly SessionDocumentCache sessionDocuments = new();
    private readonly Dictionary<Guid, (WeakReference<ImageDocument> Document, long Revision)> queuedDocuments = [];
    private readonly Dictionary<Guid, (WeakReference<ImageDocument> Document, long Revision)> savedDocuments = [];
    private readonly HashSet<Guid> flattenedHistory = [];
    private bool removingHistory;

    private bool AutoPreservesCurrent => currentHistoryId is Guid id && (clipboardHistory.Find(id)?.HasImage == true || (history.Find(id) != null && settings.KeepHistory && settings.KeepCaptureImages));

    private void RememberCurrentDocument()
    {
        if (currentHistoryId is Guid id && document != null && FindHistory(id) != null)
            sessionDocuments.Store(id, document, documentName, dirty);
    }

    private bool IsCurrentRevision((WeakReference<ImageDocument> Document, long Revision) saved) =>
        saved.Document.TryGetTarget(out var target) && ReferenceEquals(target, document) && saved.Revision == document?.Revision;

    private void HistoryWriteCompleted(Guid id)
    {
        // The dispatcher callback may arrive after a newer edit was queued. Inspect the
        // writer's latest state instead of assigning the earlier callback to that edit.
        if (WriterFor(id).PendingOrFailed(id) == null && queuedDocuments.TryGetValue(id, out var queued))
            savedDocuments[id] = queued;
        RefreshPreservationState();
        thumbnails.Remove(id);
        RefreshHistoryIfVisible();
        if (WriterFor(id).IsFailed(id)) Notify("기록하지 못했습니다. 저장하거나 다시 시도하세요.");
    }

    private void RefreshPreservationState()
    {
        string text;
        var failed = false;
        if (document == null) text = "";
        else if (AutoPreservesCurrent && currentHistoryId is Guid id)
        {
            failed = WriterFor(id).IsFailed(id);
            text = failed ? "기록 실패" :
                savedDocuments.TryGetValue(id, out var saved) && IsCurrentRevision(saved)
                    && WriterFor(id).PendingOrFailed(id) == null ? "기록됨" : "기록 중…";
        }
        else if (currentHistoryId is Guid historyId && flattenedHistory.Contains(historyId) && !dirty) text = "기록에서 열림";
        else text = dirty ? "저장 안 됨" : "저장됨";
        if (preservationLabel != null)
        {
            preservationLabel.Text = text;
            preservationLabel.Foreground = failed ? Ui.RedText : Ui.Muted;
            preservationLabel.ToolTip = failed ? "기록하지 못한 이미지는 파일로 저장할 수 있습니다." : null;
            AutomationProperties.SetName(preservationLabel, text);
        }
        if (currentPage == "editor")
            Title = document == null ? "담아 편집창" : documentName + (dirty && !AutoPreservesCurrent ? " · 저장 전" : "") + " - 담아";
    }

    private async void HideHistoryEntry(HistoryEntry entry)
    {
        if (removingHistory) return;
        surface.CompletePendingEdit(); PreserveHistoryImage();
        removingHistory = true; IsEnabled = false;
        try
        {
            await FlushHistoryWritersAsync();
            if (WriterFor(entry.Id).PendingOrFailed(entry.Id) != null)
            {
                Notify("이미지를 기록하지 못해 숨기지 않았습니다. 저장한 뒤 다시 시도하세요.");
                return;
            }
            var store = StoreFor(entry.Id); var writer = WriterFor(entry.Id);
            var hadImage = System.IO.File.Exists(FindHistory(entry.Id)?.ImagePath);
            store.Remove(entry.Id);
            if (store.LastError != null) { Notify("기록을 숨기지 못했습니다. " + store.LastError); return; }
            writer.Forget(entry.Id); ForgetHistoryState(entry.Id);
            Toast(hadImage ? "숨겼습니다. 기록 이미지는 남아 있습니다." : "숨겼습니다");
        }
        catch (Exception ex) { Notify("기록을 숨기지 못했습니다. " + ex.Message); }
        finally
        {
            removingHistory = false; IsEnabled = true;
            PreserveHistoryImage(); RefreshHistory(); RefreshPreservationState();
        }
    }

    private async void DeleteHistoryEntry(HistoryEntry entry)
    {
        if (removingHistory) return;
        var answer = MessageBox.Show(this,
            entry.HasImage ? "이 기록과 이미지를 삭제할까요?\n직접 저장한 파일은 유지됩니다." : entry.Kind == HistoryKind.ClipboardFiles ? "이 기록을 삭제할까요?\n원본 파일은 유지됩니다." : "이 기록을 삭제할까요?",
            "기록 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        removingHistory = true;
        // Disable interaction while queued disk writes finish and the managed file is removed.
        // The exported file path is never passed to the deletion operation.
        IsEnabled = false;
        historySaveTimer.Stop();
        try
        {
            var store = StoreFor(entry.Id);
            if (await WriterFor(entry.Id).DeleteAsync(entry.Id))
            {
                ForgetHistoryState(entry.Id);
                Toast("기록을 삭제했습니다");
            }
            else Notify("삭제하지 못했습니다. " + store.LastError);
        }
        catch (Exception ex) { Notify("삭제하지 못했습니다. " + ex.Message); }
        finally
        {
            removingHistory = false; IsEnabled = true;
            PreserveHistoryImage();
            RefreshHistory(); RefreshPreservationState();
        }
    }

    private void ForgetHistoryState(Guid id)
    {
        sessionDocuments.Remove(id); queuedDocuments.Remove(id); savedDocuments.Remove(id); flattenedHistory.Remove(id); thumbnails.Remove(id);
        if (currentHistoryId == id) { currentHistoryId = null; dirty = true; }
    }
}
