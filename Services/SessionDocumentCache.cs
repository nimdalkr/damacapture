using System;
using System.Collections.Generic;
using DamaCapture.Imaging;

namespace DamaCapture.Services;

public sealed record SessionDocument(ImageDocument Document, string Name, bool ExportDirty);

/// <summary>
/// Keeps live edit documents only for this process. Eviction never touches history records or files.
/// Call from the same thread that owns the mutable documents.
/// </summary>
public sealed class SessionDocumentCache
{
    // Enough for a few recent captures with their undo steps; a resident tray app should not hold more.
    public const long DefaultMemoryBudgetBytes = 32L * 1024 * 1024;
    private readonly Dictionary<Guid, LinkedListNode<CacheItem>> entries = new();
    private readonly LinkedList<CacheItem> recency = new();
    private sealed record CacheItem(Guid Id, SessionDocument Entry);

    public SessionDocumentCache(long memoryBudgetBytes = DefaultMemoryBudgetBytes)
    {
        if (memoryBudgetBytes < 1) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
        MemoryBudgetBytes = memoryBudgetBytes;
    }

    public long MemoryBudgetBytes { get; }
    public int Count => entries.Count;
    public long EstimatedMemoryBytes => Measure();
    /// <summary>Check a history badge without making that document recently used.</summary>
    public bool Contains(Guid id) => entries.ContainsKey(id);

    public void Store(Guid id, ImageDocument document, string name, bool exportDirty)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(name);
        if (entries.TryGetValue(id, out var old)) recency.Remove(old);
        var node = recency.AddLast(new CacheItem(id, new SessionDocument(document, name, exportDirty)));
        entries[id] = node;
        EnforceBudget();
    }

    public bool TryGet(Guid id, out SessionDocument entry)
    {
        if (!entries.TryGetValue(id, out var node))
        {
            // Other retained documents may have changed through a previously returned reference.
            EnforceBudget();
            entry = null!;
            return false;
        }
        recency.Remove(node);
        recency.AddLast(node);
        EnforceBudget();
        entry = node.Value.Entry;
        return true;
    }

    /// <summary>Drops the least recently used documents until at most <paramref name="count"/> remain.</summary>
    public void KeepNewest(int count)
    {
        while (entries.Count > Math.Max(0, count))
        {
            var oldest = recency.First!;
            recency.RemoveFirst();
            entries.Remove(oldest.Value.Id);
        }
    }

    public bool Remove(Guid id)
    {
        if (!entries.Remove(id, out var node)) return false;
        recency.Remove(node);
        return true;
    }

    private void EnforceBudget()
    {
        // No count limit. The most recently used document survives even when it alone is oversized.
        while (entries.Count > 1 && Measure() > MemoryBudgetBytes)
        {
            var oldest = recency.First!;
            recency.RemoveFirst();
            entries.Remove(oldest.Value.Id);
        }
    }

    private long Measure()
    {
        var documents = new HashSet<ImageDocument>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        foreach (var item in recency)
        {
            if (documents.Add(item.Entry.Document)) bytes += item.Entry.Document.EstimatedMemoryBytes;
            bytes += 128L + item.Entry.Name.Length * 2L;
        }
        return bytes;
    }
}
