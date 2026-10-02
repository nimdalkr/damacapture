using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DamaCapture.Imaging;
using DamaCapture.Services;
using Forms = System.Windows.Forms;

namespace DamaCapture;

/// <summary>
/// "폴더에 바로 저장": the capture is written to the save folder at once, and that file then follows the edits made
/// to the capture, so the folder never keeps an image from before something was masked. The file belongs to the
/// user: it is never deleted here, and once it is moved or removed it is no longer followed.
/// </summary>
internal sealed partial class MainWindow
{
    private sealed class FollowedFile(string path)
    {
        public string Path { get; } = path;
        /// <summary>The document whose revisions are counted; a record reopened from its PNG is a new document.</summary>
        public WeakReference<ImageDocument>? Document { get; set; }
        /// <summary>Revision of that document last handed to the writer; null when the file is known to be behind.</summary>
        public long? Queued { get; set; }
        /// <summary>A write has finished at least once, so a missing file means the user moved or removed it.</summary>
        public bool Landed { get; set; }
        public int Pending { get; set; }
        public int Failures { get; set; }
    }

    private readonly Dictionary<Guid, FollowedFile> followedFiles = new();
    private readonly System.Windows.Threading.DispatcherTimer autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    /// <summary>Writes run one after another off the UI thread, so a later edit can never be overtaken by an earlier one.</summary>
    private Task autoSaveTail = Task.CompletedTask;
    private bool AutoSaveIdle => autoSaveTail.IsCompleted && followedFiles.Values.All(followed => followed.Pending == 0);

    private string NewAutoSavePath()
    {
        Directory.CreateDirectory(settings.SaveFolder);
        var extension = settings.SaveFormat == "jpg" ? ".jpg" : ".png";
        var stem = "담아_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(settings.SaveFolder, stem + extension);
        // A name already promised to a write that has not landed yet counts as taken.
        bool Taken(string candidate) => File.Exists(candidate) || followedFiles.Values.Any(followed => string.Equals(followed.Path, candidate, StringComparison.OrdinalIgnoreCase));
        for (var n = 2; Taken(path); n++) path = Path.Combine(settings.SaveFolder, $"{stem}_{n}{extension}");
        return path;
    }

    /// <summary>Queues the capture just taken for the save folder and starts following it. Returns the file name, or null on failure.</summary>
    private string? AutoSaveCapture()
    {
        if (document == null || currentHistoryId is not Guid id) return null;
        try
        {
            var path = NewAutoSavePath();
            followedFiles[id] = new FollowedFile(path);
            WriteFollowedFile(id);
            return Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            followedFiles.Remove(id);
            ReportAutoSave("폴더에 저장하지 못했습니다. " + ex.Message, warning: true);
            return null;
        }
    }

    private void ScheduleAutoSave()
    {
        if (currentHistoryId is not Guid id || !followedFiles.ContainsKey(id)) return;
        autoSaveTimer.Stop(); autoSaveTimer.Start();
    }

    /// <summary>Writes a pending change before the document is put aside or the app goes away.</summary>
    private void FlushAutoSave()
    {
        if (autoSaveTimer.IsEnabled && currentHistoryId is Guid id) WriteFollowedFile(id);
    }

    private void WriteFollowedFile(Guid id)
    {
        autoSaveTimer.Stop();
        // Text being typed in place is not in the image yet; committing or dropping it schedules this again.
        if (document == null || currentHistoryId != id || document.Hidden != null) return;
        if (!followedFiles.TryGetValue(id, out var followed)) return;
        var sameDocument = followed.Document != null && followed.Document.TryGetTarget(out var known) && ReferenceEquals(known, document);
        if (sameDocument && followed.Queued == document.Revision) return;
        // Only once a write has finished, and none is on its way, does a missing file mean the user took it away.
        if (followed.Landed && followed.Pending == 0 && !File.Exists(followed.Path)) { followedFiles.Remove(id); return; }
        var image = document.Render(); if (!image.IsFrozen) image.Freeze();
        var announce = !followed.Landed && followed.Pending == 0;
        followed.Document = new WeakReference<ImageDocument>(document); followed.Queued = document.Revision; followed.Pending++;
        var path = followed.Path; var quality = settings.JpegQuality;
        autoSaveTail = autoSaveTail.ContinueWith(_ =>
        {
            Exception? failure = null;
            // A viewer or a sync client may hold the file for a moment; that must not leave an unmasked image behind.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { ImageFiles.Save(image, path, quality); failure = null; break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; Thread.Sleep(350 * (attempt + 1)); }
                catch (Exception ex) { failure = ex; break; }
            }
            Dispatcher.BeginInvoke(() => FollowedWriteFinished(id, followed, announce, failure));
        }, TaskScheduler.Default);
    }

    private void FollowedWriteFinished(Guid id, FollowedFile followed, bool announce, Exception? failure)
    {
        followed.Pending--;
        if (failure == null)
        {
            followed.Failures = 0;
            if (!followed.Landed)
            {
                followed.Landed = true;
                StoreFor(id).AttachFollowedFile(id, followed.Path);
                RefreshHistoryIfVisible();
            }
            if (announce) ReportAutoSave("폴더에 저장했습니다 · " + Path.GetFileName(followed.Path), warning: false);
            return;
        }
        // The file may still show what it showed before this edit. It stays followed, marked as behind, so the next
        // edit or reopening the record writes it again; a few retries are made right away while it is still open.
        followed.Queued = null; followed.Failures++;
        if (followed.Failures == 1)
            ReportAutoSave((followed.Landed ? "폴더의 파일을 갱신하지 못했습니다. 이전 내용이 남아 있을 수 있습니다. " : "폴더에 저장하지 못했습니다. ") + failure.Message, warning: true);
        if (!followed.Landed && followed.Failures >= 3) { if (ReferenceEquals(followedFiles.GetValueOrDefault(id), followed)) followedFiles.Remove(id); return; }
        if (followed.Failures < 3 && currentHistoryId == id) { autoSaveTimer.Stop(); autoSaveTimer.Start(); }
    }

    // Saving happens while the window may be in the tray, so the result is said where the user will see it.
    private void ReportAutoSave(string message, bool warning)
    {
        if (IsVisible && !warning) Toast(message); else Notify(message);
        if (!IsVisible) tray?.ShowBalloonTip(warning ? 4000 : 1500, "담아", message, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    /// <summary>A record opened from history resumes following its file if that file is still where it was saved.</summary>
    private void ResumeFollowing(HistoryEntry entry)
    {
        if (document == null) return;
        if (followedFiles.TryGetValue(entry.Id, out var followed))
        {
            // Same record, new document: its revisions start over. A file left behind by a failed write is written again.
            var behind = followed.Queued == null;
            followed.Document = new WeakReference<ImageDocument>(document);
            followed.Queued = behind ? null : document.Revision;
            if (behind) ScheduleAutoSave();
            return;
        }
        if (!string.IsNullOrWhiteSpace(entry.FollowedPath) && File.Exists(entry.FollowedPath))
            followedFiles[entry.Id] = new FollowedFile(entry.FollowedPath) { Document = new WeakReference<ImageDocument>(document), Queued = document.Revision, Landed = true };
    }
}
