using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace DamaCapture.Services;

/// <summary>
/// Writes flattened history images off the UI thread. Only queued or failed images remain in memory.
/// A single writer orders disk commits, so an older edit cannot overwrite a newer snapshot.
/// </summary>
public sealed class HistorySnapshotWriter(HistoryStore store)
{
    private sealed record PendingSnapshot(BitmapSource Image, long Revision, bool Failed = false);
    private sealed class Deletion(PendingSnapshot? recoverable)
    {
        public PendingSnapshot? Recoverable = recoverable;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object gate = new();
    private readonly Dictionary<Guid, PendingSnapshot> pending = [];
    private readonly Queue<Guid> queue = new();
    private readonly HashSet<Guid> queued = [];
    private readonly Dictionary<Guid, Deletion> deletions = [];
    private Task? worker;
    private TaskCompletionSource<bool>? activeWrite;
    private Guid? activeId;
    private long activeRevision;
    private long revision;

    /// <summary>Raised on the worker thread after a current revision finishes; UI callers must dispatch.</summary>
    public event Action<Guid, bool>? Completed;

    // Deterministically exercises the active-write/deletion boundary without delays or large images.
    internal Action<Guid>? BeforeSnapshotWrite { get; set; }

    public bool HasFailures
    {
        get { lock (gate) return pending.Values.Any(snapshot => snapshot.Failed); }
    }

    public bool IsFailed(Guid id)
    {
        lock (gate) return pending.TryGetValue(id, out var snapshot) && snapshot.Failed;
    }

    /// <summary>Replaces any waiting edit for this entry without performing disk I/O on the caller.</summary>
    public void Enqueue(Guid id, BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (id == Guid.Empty) throw new ArgumentException("A history entry ID is required.", nameof(id));
        if (!image.IsFrozen)
        {
            image = image.CloneCurrentValue();
            if (!image.CanFreeze) throw new ArgumentException("History images must support freezing.", nameof(image));
            image.Freeze();
        }

        lock (gate)
        {
            var snapshot = new PendingSnapshot(image, ++revision);
            if (deletions.TryGetValue(id, out var deletion))
            {
                // Keep the latest edit only for recovery if deletion fails; never queue it for disk.
                deletion.Recoverable = snapshot;
                return;
            }
            if (store.Find(id) is null) return;
            pending[id] = snapshot;
            Schedule(id);
            StartWorker();
        }
    }

    /// <summary>Returns the newest image only while it is waiting for disk or its last write failed.</summary>
    public BitmapSource? PendingOrFailed(Guid id)
    {
        lock (gate) return pending.TryGetValue(id, out var snapshot) ? snapshot.Image : null;
    }

    /// <summary>Releases a removed entry's queued or failed image. An active disk write may finish.</summary>
    public void Forget(Guid id)
    {
        lock (gate) pending.Remove(id);
    }

    /// <summary>
    /// Quiesces this entry's queued/active writes before deleting its managed image and metadata.
    /// False preserves the latest in-memory image for retry; inspect HistoryStore.LastError for details.
    /// </summary>
    public Task<bool> DeleteAsync(Guid id)
    {
        lock (gate)
        {
            if (deletions.TryGetValue(id, out var existing)) return existing.Completion.Task;
            pending.Remove(id, out var recoverable);
            var deletion = new Deletion(recoverable);
            deletions.Add(id, deletion);
            var active = activeId == id ? (Task?)activeWrite?.Task ?? Task.CompletedTask : Task.CompletedTask;
            _ = Task.Run(async () =>
            {
                bool deleted = false;
                try
                {
                    await active.ConfigureAwait(false);
                    deleted = store.DeleteManagedImage(id);
                }
                catch (Exception ex) { store.ReportBackgroundError(ex); }
                finally
                {
                    try
                    {
                        lock (gate)
                        {
                            deletions.Remove(id);
                            if (!deleted && deletion.Recoverable is { } image && store.Find(id) is not null)
                                pending[id] = image with { Failed = true };
                            deletion.Recoverable = null;
                        }
                    }
                    finally { deletion.Completion.TrySetResult(deleted); }
                }
            });
            return deletion.Completion.Task;
        }
    }

    /// <summary>
    /// Retries failed entries once and waits until the current worker drains its queue.
    /// Storage failures are reported through HasFailures and Completed, not a faulted task.
    /// </summary>
    public Task FlushAsync()
    {
        lock (gate)
        {
            foreach (var (id, snapshot) in pending)
                if (snapshot.Failed && !(activeId == id && activeRevision == snapshot.Revision))
                    Schedule(id);
            StartWorker();
            var tasks = deletions.Values.Select(deletion => (Task)deletion.Completion.Task).ToList();
            if (worker is not null) tasks.Add(worker);
            return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
        }
    }

    private void Schedule(Guid id)
    {
        if (queued.Add(id)) queue.Enqueue(id);
    }

    private void StartWorker()
    {
        if (worker is null && queue.Count > 0) worker = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            Guid id;
            PendingSnapshot snapshot;
            lock (gate)
            {
                if (queue.Count == 0)
                {
                    activeId = null;
                    worker = null;
                    return;
                }
                id = queue.Dequeue();
                queued.Remove(id);
                if (!pending.TryGetValue(id, out var next)) continue;
                snapshot = next;
                activeId = id;
                activeRevision = snapshot.Revision;
                activeWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            bool saved;
            try
            {
                BeforeSnapshotWrite?.Invoke(id);
                saved = store.SaveSnapshot(id, snapshot.Image);
            }
            catch (Exception) { saved = false; }

            bool current;
            TaskCompletionSource<bool>? writeFinished;
            lock (gate)
            {
                current = pending.TryGetValue(id, out var latest) && latest.Revision == snapshot.Revision;
                if (current)
                {
                    if (saved) pending.Remove(id);
                    else pending[id] = snapshot with { Failed = true };
                }
                activeId = null;
                writeFinished = activeWrite;
                activeWrite = null;
            }

            writeFinished?.TrySetResult(true);
            if (current) NotifyCompleted(id, saved);
        }
    }

    private void NotifyCompleted(Guid id, bool saved)
    {
        // A UI subscriber shutting down must not stop the remaining history writes.
        if (Completed is not { } handlers) return;
        foreach (Action<Guid, bool> handler in handlers.GetInvocationList())
            try { handler(id, saved); }
            catch (Exception) { }
    }
}
