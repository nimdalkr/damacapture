using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;

namespace DamaCapture.Services;

public enum HistoryKind { Capture = 0, ClipboardText = 1, ClipboardImage = 2, ClipboardFiles = 3 }

public sealed record HistoryEntry(string Path, DateTime SavedAt)
{
    public Guid Id { get; init; }
    public HistoryKind Kind { get; init; } = HistoryKind.Capture;
    public string Text { get; init; } = "";
    public string[] FilePaths { get; init; } = [];
    public DateTimeOffset? CapturedAt { get; init; }
    public string WindowTitle { get; init; } = "";
    public string ApplicationName { get; init; } = "";
    public string CaptureMode { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public string? SnapshotPath { get; init; }
    /// <summary>A file in the user's save folder that is rewritten as this record is edited ("폴더에 바로 저장").</summary>
    public string? FollowedPath { get; init; }

    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path);

    [JsonIgnore]
    public string ImagePath => !string.IsNullOrWhiteSpace(SnapshotPath) && File.Exists(SnapshotPath) ? SnapshotPath : Path;

    [JsonIgnore]
    public bool IsClipboard => Kind is HistoryKind.ClipboardText or HistoryKind.ClipboardImage or HistoryKind.ClipboardFiles;

    [JsonIgnore]
    public bool HasImage => Kind is HistoryKind.Capture or HistoryKind.ClipboardImage;

    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (Kind == HistoryKind.ClipboardText)
            {
                var remaining = (Text ?? "").AsSpan();
                while (!remaining.IsEmpty)
                {
                    int end = remaining.IndexOfAny('\r', '\n');
                    var line = (end < 0 ? remaining : remaining[..end]).Trim();
                    if (!line.IsEmpty) return ShortTitle(line);
                    if (end < 0) break;
                    remaining = remaining[(end + 1)..];
                }
                return "복사한 텍스트";
            }
            if (Kind == HistoryKind.ClipboardFiles)
            {
                if (FilePaths is not { Length: > 0 }) return "복사한 파일";
                var first = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(FilePaths[0]));
                if (string.IsNullOrEmpty(first)) first = FilePaths[0];
                return ShortTitle(first.AsSpan()) + (FilePaths.Length > 1 ? $" 외 {FilePaths.Length - 1}개" : "");
            }
            if (!string.IsNullOrWhiteSpace(WindowTitle)) return ShortTitle(WindowTitle.AsSpan().Trim());
            if (!string.IsNullOrEmpty(Name)) return ShortTitle(Name.AsSpan());
            return Kind == HistoryKind.ClipboardImage ? "복사한 이미지" : "캡처 이미지";
        }
    }

    private static string ShortTitle(ReadOnlySpan<char> text)
    {
        if (text.Length <= 120) return text.ToString();
        int length = char.IsHighSurrogate(text[119]) ? 119 : 120;
        return text[..length].ToString() + "…";
    }
}

/// <summary>Unbounded capture metadata, with optional flattened snapshots. Exported images are never modified or deleted.</summary>
public sealed class HistoryStore
{
    public const int MaxClipboardTextLength = 524_288;
    public const int MaxClipboardFiles = 1_000;
    private const long MaxRecordBytes = 4 * 1024 * 1024;
    private readonly object gate = new();
    private readonly object snapshotWrites = new();
    private readonly string legacyPath, entryDirectory, imageDirectory, migrationPath;
    private readonly List<HistoryEntry> entries = [];
    private readonly HashSet<Guid> failedEntries = [];
    private bool persist;
    private string? lastError;
    private sealed record StoredEntry(Guid Id, bool Deleted, HistoryEntry? Entry);
    private sealed record MigrationState(bool LegacyImported);

    /// <summary>A newest-first snapshot; mutating this list does not change the store.</summary>
    public List<HistoryEntry> Entries { get { lock (gate) return [.. entries]; } }
    public bool PersistenceEnabled { get { lock (gate) return persist; } }
    public bool HasPersistenceFailures { get { lock (gate) return failedEntries.Count > 0; } }
    public string? LastError { get { lock (gate) return lastError; } }
    internal void ReportBackgroundError(Exception exception) { lock (gate) lastError = exception.Message; }

    /// <summary>When a record happened: the capture or copy time, else when it was saved.</summary>
    public static DateTimeOffset EntryTime(HistoryEntry entry) => entry.CapturedAt ?? new DateTimeOffset(DateTime.SpecifyKind(entry.SavedAt, entry.SavedAt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Local : entry.SavedAt.Kind));

    /// <summary>
    /// Records that automatic cleanup would delete: older than <paramref name="days"/>, or beyond the newest
    /// <paramref name="count"/>. Zero for either rule turns it off. The protected record (the one being edited)
    /// is never returned, though it still occupies its place in the count.
    /// </summary>
    public static List<Guid> Expired(IEnumerable<HistoryEntry> entries, int days, int count, DateTimeOffset now, Guid? protect = null)
    {
        var expired = new List<Guid>();
        if (days <= 0 && count <= 0) return expired;
        var ordered = entries.OrderByDescending(EntryTime).ToList();
        var cutoff = days > 0 ? now.AddDays(-days) : DateTimeOffset.MinValue;
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            if (entry.Id == protect) continue;
            if ((days > 0 && EntryTime(entry) < cutoff) || (count > 0 && i >= count)) expired.Add(entry.Id);
        }
        return expired;
    }

    public HistoryStore(bool persist = false, string? directory = null, int maxEntries = 100)
    {
        this.persist = persist;
        // The argument remains for existing callers; capture history no longer has a count cap.
        _ = maxEntries;
        var root = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamaCapture"));
        legacyPath = Path.Combine(root, "history.json");
        entryDirectory = Path.Combine(root, "history", "entries");
        imageDirectory = Path.Combine(root, "history", "images");
        migrationPath = Path.Combine(root, "history", "migration.json");
        if (persist) entries.AddRange(ReadPersisted());
    }

    public HistoryEntry RecordCapture(DateTimeOffset capturedAt, string windowTitle, string applicationName, string mode, int width, int height)
    {
        lock (gate)
        {
            lastError = null;
            var entry = new HistoryEntry("", default)
            {
                Id = Guid.NewGuid(), CapturedAt = capturedAt,
                WindowTitle = windowTitle ?? "", ApplicationName = applicationName ?? "", CaptureMode = mode ?? "",
                Width = Math.Max(0, width), Height = Math.Max(0, height)
            };
            entries.Add(entry); SortEntries();
            Persist(entry);
            return entry;
        }
    }

    /// <summary>Archives clipboard content or file references without opening, copying, or enumerating source files.</summary>
    public HistoryEntry RecordClipboard(HistoryKind kind, DateTimeOffset copiedAt, string windowTitle, string applicationName,
        string text = "", string[]? filePaths = null, int width = 0, int height = 0)
    {
        if (kind is not (HistoryKind.ClipboardText or HistoryKind.ClipboardImage or HistoryKind.ClipboardFiles))
            throw new ArgumentOutOfRangeException(nameof(kind), "클립보드 기록 형식이 올바르지 않습니다.");
        var payload = NormalizePayload(kind, text, filePaths);
        lock (gate)
        {
            lastError = null;
            var entry = new HistoryEntry("", default)
            {
                Id = Guid.NewGuid(), Kind = kind, CapturedAt = copiedAt,
                WindowTitle = windowTitle ?? "", ApplicationName = applicationName ?? "", CaptureMode = "Clipboard",
                Text = payload.Text, FilePaths = payload.Files,
                Width = kind == HistoryKind.ClipboardImage ? Math.Max(0, width) : 0,
                Height = kind == HistoryKind.ClipboardImage ? Math.Max(0, height) : 0
            };
            entries.Add(entry); SortEntries();
            Persist(entry);
            return entry;
        }
    }

    public HistoryEntry? Find(Guid id) { lock (gate) return entries.FirstOrDefault(entry => entry.Id == id); }

    /// <summary>Retries metadata for failed live entries only; removals and disabled persistence are never undone.</summary>
    public bool RetryFailedEntries()
    {
        lock (gate)
        {
            lastError = null;
            if (!persist) { failedEntries.Clear(); return true; }
            foreach (var id in failedEntries.ToArray())
            {
                var entry = entries.FirstOrDefault(item => item.Id == id);
                if (entry is null) failedEntries.Remove(id);
                else Persist(entry);
            }
            return failedEntries.Count == 0;
        }
    }

    /// <summary>Attaches an export without changing capture time or window metadata.</summary>
    /// <summary>Remembers the file that follows this record's edits, so reopening the record later keeps it up to date.</summary>
    public bool AttachFollowedFile(Guid id, string path)
    {
        var fullPath = ExportPath(path);
        if (fullPath is null) return false;
        lock (gate)
        {
            lastError = null;
            var index = entries.FindIndex(entry => entry.Id == id);
            if (index < 0 || !entries[index].HasImage) return false;
            var entry = entries[index] with { FollowedPath = fullPath };
            entries[index] = entry;
            Persist(entry);
            return true;
        }
    }

    public bool AttachExport(Guid id, string path)
    {
        var fullPath = ExportPath(path);
        if (fullPath is null || !File.Exists(fullPath)) return false;
        lock (gate)
        {
            lastError = null;
            var index = entries.FindIndex(entry => entry.Id == id);
            if (index < 0) return false;
            if (!entries[index].HasImage) { lastError = "텍스트와 파일 목록 기록에는 이미지 내보내기를 연결할 수 없습니다."; return false; }
            var entry = entries[index] with { Path = fullPath, SavedAt = DateTime.UtcNow };
            entries[index] = entry;
            Persist(entry); SortEntries();
            return true;
        }
    }

    /// <summary>The caller supplies completed, flattened pixels. Replaces the same managed PNG atomically.</summary>
    public bool SaveSnapshot(Guid id, BitmapSource bitmap)
    {
        lock (gate)
        {
            lastError = null;
            var target = entries.FirstOrDefault(entry => entry.Id == id);
            if (!persist || target is null) return false;
            if (!target.HasImage) { lastError = "이미지 형식의 기록만 PNG로 저장할 수 있습니다."; return false; }
        }
        // PNG encoding may be slow; it must never hold the metadata lock used by the UI.
        lock (snapshotWrites)
        {
            lock (gate) { if (!persist || entries.All(entry => entry.Id != id || !entry.HasImage)) return false; }
            var path = Path.Combine(imageDirectory, id.ToString("N") + ".png");
            try
            {
                ArgumentNullException.ThrowIfNull(bitmap);
                Directory.CreateDirectory(imageDirectory);
                ImageFiles.Save(bitmap, path);
                lock (gate)
                {
                    // An export or removal may have occurred while writing. Merge with the latest
                    // entry, and never replace a tombstone or re-enable disabled persistence.
                    var index = entries.FindIndex(entry => entry.Id == id);
                    if (!persist || index < 0) return false;
                    var entry = entries[index] with { SnapshotPath = path };
                    entries[index] = entry;
                    return Persist(entry);
                }
            }
            catch (Exception ex)
            {
                lock (gate) { lastError = ex.Message; DiscardStaleImage(id, path); }
                return false;
            }
        }
    }

    /// <summary>
    /// After a failed write the PNG on disk predates the edit that was being saved, and may even be
    /// the unmasked capture. Remove it so the record never serves pixels the user has since hidden;
    /// the newest composite stays in the writer's memory for retry.
    /// </summary>
    private void DiscardStaleImage(Guid id, string path)
    {
        var index = entries.FindIndex(entry => entry.Id == id);
        if (index >= 0 && entries.Any(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            RejectReparsePoints(path);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) == 0) File.Delete(path);
        }
        catch (Exception ex) { lastError += " 이전 보관 이미지를 정리하지 못했습니다: " + ex.Message; return; }
        if (index < 0 || entries[index].SnapshotPath is null) return;
        var entry = entries[index] with { SnapshotPath = null };
        entries[index] = entry;
        Persist(entry);
    }

    /// <summary>Number of live entries whose managed PNG exists on disk.</summary>
    public int ManagedImageCount()
    {
        lock (gate) return entries.Count(entry => entry.HasImage && File.Exists(Path.Combine(imageDirectory, entry.Id.ToString("N") + ".png")));
    }

    /// <summary>Managed PNGs whose ID no longer has a live entry: hidden, cleared or disabled history.</summary>
    public IReadOnlyList<string> OrphanImages()
    {
        lock (gate)
        {
            if (!Directory.Exists(imageDirectory)) return [];
            var live = entries.Select(entry => entry.Id).ToHashSet();
            var exports = entries.Select(entry => entry.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orphans = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(imageDirectory, "*.png"))
                {
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) || live.Contains(id)) continue;
                    if (exports.Contains(Path.GetFullPath(file))) continue;
                    orphans.Add(file);
                }
            }
            catch (Exception ex) { lastError = ex.Message; }
            return orphans;
        }
    }

    /// <summary>Deletes orphaned managed PNGs only. Exported files are never touched.</summary>
    public int DeleteOrphanImages(out int failed)
    {
        lock (snapshotWrites)
        lock (gate)
        {
            var deleted = 0; failed = 0; lastError = null;
            foreach (var file in OrphanImages())
            {
                try
                {
                    RejectReparsePoints(file);
                    if ((File.GetAttributes(file) & FileAttributes.Directory) != 0) throw new IOException("이미지 경로가 파일이 아닙니다.");
                    File.Delete(file); deleted++;
                }
                catch (Exception ex) { failed++; lastError = ex.Message; }
            }
            return deleted;
        }
    }

    /// <summary>Compatibility API for an exported image without capture metadata.</summary>
    public bool Add(string path)
    {
        string? fullPath = ExportPath(path);
        if (fullPath is null || !File.Exists(fullPath)) return false;
        lock (gate)
        {
            lastError = null;
            var index = entries.FindIndex(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase));
            var entry = index < 0 ? new HistoryEntry(fullPath, DateTime.UtcNow) { Id = Guid.NewGuid() }
                : entries[index] with { SavedAt = DateTime.UtcNow };
            if (index < 0) entries.Add(entry); else entries[index] = entry;
            Persist(entry); SortEntries();
        }
        return true;
    }

    /// <summary>Removes a reference only, retaining the exported image on disk.</summary>
    public bool Remove(string path)
    {
        lock (gate)
        {
            lastError = null;
            if (Guid.TryParse(path, out var id)) return RemoveCore(id);
            var fullPath = ExportPath(path);
            if (fullPath is null) return false;
            var matches = entries.Where(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.SnapshotPath, fullPath, StringComparison.OrdinalIgnoreCase)).Select(item => item.Id).ToArray();
            bool removed = matches.Length > 0;
            foreach (var match in matches) removed &= RemoveCore(match);
            return removed;
        }
    }

    public bool Remove(Guid id) { lock (gate) { lastError = null; return RemoveCore(id); } }

    /// <summary>
    /// Deletes only the ID-derived managed PNG, then removes its metadata with a tombstone.
    /// Export paths are never deletion targets. A metadata failure leaves the entry for a safe retry.
    /// </summary>
    public bool DeleteManagedImage(Guid id)
    {
        // Also protects callers outside HistorySnapshotWriter: an older PNG commit must finish first,
        // and a later SaveSnapshot must see the removed entry before it can write another image.
        lock (snapshotWrites)
        lock (gate)
        {
            lastError = null;
            var index = entries.FindIndex(entry => entry.Id == id);
            if (id == Guid.Empty || index < 0)
            {
                lastError = "삭제할 캡처 기록을 찾을 수 없습니다.";
                return false;
            }
            var imagePath = Path.GetFullPath(Path.Combine(imageDirectory, id.ToString("N") + ".png"));
            var recordPath = Path.Combine(entryDirectory, id.ToString("N") + ".json");
            try
            {
                RejectReparsePoints(recordPath);
                bool hasImage = false;
                if (entries[index].HasImage)
                {
                    RejectReparsePoints(imagePath);
                    try
                    {
                        if ((File.GetAttributes(imagePath) & FileAttributes.Directory) != 0)
                            throw new IOException("캡처 이미지 경로가 파일이 아닙니다.");
                        hasImage = true;
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
                if (hasImage && entries.Any(entry => string.Equals(entry.Path, imagePath, StringComparison.OrdinalIgnoreCase)
                    || entry.FilePaths.Any(path => string.Equals(path, imagePath, StringComparison.OrdinalIgnoreCase))))
                    throw new IOException("내보낸 파일과 같은 경로의 이미지는 삭제하지 않았습니다.");

                // File.Delete is a no-op for metadata-only captures. It never uses Path/SnapshotPath
                // from persisted input, so a modified record cannot redirect deletion to an export.
                if (hasImage) File.Delete(imagePath);
                if ((persist || File.Exists(recordPath)) && !WriteRecord(new StoredEntry(id, true, null)))
                {
                    lastError = (hasImage ? "이미지는 삭제했지만 캡처 기록을 정리하지 못했습니다. " : "캡처 기록을 정리하지 못했습니다. ") + lastError;
                    return false;
                }
                entries.RemoveAt(index);
                failedEntries.Remove(id);
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                return false;
            }
        }
    }

    private static void RejectReparsePoints(string path)
    {
        // Check every existing ancestor as well as the file: a junction in the managed directory
        // must not turn an apparently local ID-derived path into a deletion outside that directory.
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("연결된 경로의 캡처 이미지는 삭제할 수 없습니다.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private bool RemoveCore(Guid id)
    {
        var index = entries.FindIndex(entry => entry.Id == id);
        if (index < 0) return false;
        if (persist && !WriteRecord(new StoredEntry(id, true, null))) return false;
        entries.RemoveAt(index);
        failedEntries.Remove(id);
        return true;
    }

    /// <summary>Clears references only. Saved images are never deleted.</summary>
    public void Clear()
    {
        lock (gate)
        {
            lastError = null;
            if (persist) foreach (var entry in entries) WriteRecord(new StoredEntry(entry.Id, true, null));
            entries.Clear();
            failedEntries.Clear();
        }
    }

    /// <summary>Disabling persistence clears stored references, while retaining this session's list in memory.</summary>
    public void SetPersistence(bool enabled)
    {
        lock (gate)
        {
            lastError = null;
            if (persist == enabled) return;
            if (!enabled)
            {
                foreach (var entry in entries) WriteRecord(new StoredEntry(entry.Id, true, null));
                persist = false;
                failedEntries.Clear();
                return;
            }
            var merged = entries.Concat(ReadPersisted()).DistinctBy(item => item.Id).ToList();
            entries.Clear();
            entries.AddRange(merged);
            persist = true; SortEntries();
            foreach (var entry in entries) Persist(entry);
        }
    }

    private void SortEntries() => entries.Sort((a, b) => CaptureTime(b).CompareTo(CaptureTime(a)));
    private static DateTimeOffset CaptureTime(HistoryEntry entry) => entry.CapturedAt ??
        new DateTimeOffset(DateTime.SpecifyKind(entry.SavedAt, DateTimeKind.Utc));

    private bool Persist(HistoryEntry entry)
    {
        bool saved = !persist || WriteRecord(new StoredEntry(entry.Id, false, entry));
        if (saved) failedEntries.Remove(entry.Id);
        else failedEntries.Add(entry.Id);
        return saved;
    }
    private bool WriteRecord(StoredEntry record)
    {
        try { AtomicJson.Write(Path.Combine(entryDirectory, record.Id.ToString("N") + ".json"), record); return true; }
        catch (Exception ex) { lastError = ex.Message; return false; }
    }

    private List<HistoryEntry> ReadPersisted()
    {
        var records = new Dictionary<Guid, StoredEntry>();
        if (Directory.Exists(entryDirectory))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(entryDirectory, "*.json"))
                {
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)) continue;
                    try
                    {
                        using var stream = File.OpenRead(file);
                        if (stream.Length > MaxRecordBytes) throw new JsonException("기록 한 항목의 저장 크기가 너무 큽니다.");
                        var record = JsonSerializer.Deserialize<StoredEntry>(stream, SettingsStore.JsonOptions);
                        if (record is null || record.Id != id || (!record.Deleted && record.Entry is null))
                            throw new JsonException("캡처 기록 형식이 올바르지 않습니다.");
                        records[id] = record.Deleted ? record : record with { Entry = Normalize(record.Entry!, id) };
                    }
                    catch (Exception ex) { lastError = ex.Message; }
                }
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        if (File.Exists(legacyPath) && !LegacyImported())
        {
            var complete = true;
            try
            {
                using var stream = File.OpenRead(legacyPath);
                using var json = JsonDocument.Parse(stream);
                if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("이전 캡처 기록 형식이 올바르지 않습니다.");
                foreach (var item in json.RootElement.EnumerateArray())
                {
                    try
                    {
                        var entry = item.Deserialize<HistoryEntry>(SettingsStore.JsonOptions);
                        if (entry is null || ExportPath(entry.Path) is not { } path) continue;
                        var id = entry.Id != Guid.Empty ? entry.Id : LegacyId(path, entry.SavedAt);
                        if (records.ContainsKey(id)) continue;
                        entry = Normalize(entry with { Path = path }, id);
                        var record = new StoredEntry(id, false, entry);
                        records[id] = record;
                        if (!WriteRecord(record)) complete = false;
                    }
                    catch (Exception ex) { lastError = ex.Message; complete = false; }
                }
                if (complete) AtomicJson.Write(migrationPath, new MigrationState(true));
            }
            catch (Exception ex) { lastError = ex.Message; }
        }
        return records.Values.Where(record => !record.Deleted && record.Entry != null).Select(record => record.Entry!)
            .OrderByDescending(CaptureTime).ToList();
    }

    private bool LegacyImported()
    {
        try
        {
            if (!File.Exists(migrationPath)) return false;
            using var stream = File.OpenRead(migrationPath);
            return JsonSerializer.Deserialize<MigrationState>(stream, SettingsStore.JsonOptions)?.LegacyImported == true;
        }
        catch (Exception ex) { lastError = ex.Message; return false; }
    }

    private HistoryEntry Normalize(HistoryEntry entry, Guid id)
    {
        var payload = NormalizePayload(entry.Kind, entry.Text, entry.FilePaths);
        if (!entry.HasImage && (!string.IsNullOrEmpty(entry.Path) || !string.IsNullOrEmpty(entry.SnapshotPath)))
            throw new JsonException("텍스트와 파일 목록 기록에는 이미지 경로를 저장할 수 없습니다.");
        var expectedSnapshot = Path.GetFullPath(Path.Combine(imageDirectory, id.ToString("N") + ".png"));
        // Managed snapshots can only be the app-owned file for this entry, never an arbitrary source path.
        var snapshot = string.Equals(entry.SnapshotPath, expectedSnapshot, StringComparison.OrdinalIgnoreCase) ? expectedSnapshot : null;
        return entry with
        {
            Id = id, Path = ExportPath(entry.Path) ?? "", SnapshotPath = snapshot,
            FollowedPath = entry.HasImage ? ExportPath(entry.FollowedPath) : null,
            Text = payload.Text, FilePaths = payload.Files,
            WindowTitle = entry.WindowTitle ?? "", ApplicationName = entry.ApplicationName ?? "", CaptureMode = entry.CaptureMode ?? "",
            Width = entry.HasImage ? Math.Max(0, entry.Width) : 0, Height = entry.HasImage ? Math.Max(0, entry.Height) : 0
        };
    }

    private static (string Text, string[] Files) NormalizePayload(HistoryKind kind, string? text, string[]? files)
    {
        if (kind is not (HistoryKind.Capture or HistoryKind.ClipboardText or HistoryKind.ClipboardImage or HistoryKind.ClipboardFiles))
            throw new ArgumentOutOfRangeException(nameof(kind), "기록 형식이 올바르지 않습니다.");
        text ??= "";
        files ??= [];
        if (text.Length > MaxClipboardTextLength) throw new ArgumentException("복사한 텍스트가 보관 가능한 길이를 초과했습니다.", nameof(text));
        if (files.Length > MaxClipboardFiles) throw new ArgumentException("복사한 파일 목록이 1,000개를 초과했습니다.", nameof(files));
        if (kind == HistoryKind.ClipboardText)
        {
            if (text.Length == 0 || files.Length != 0) throw new ArgumentException("텍스트 기록 내용이 올바르지 않습니다.");
            return (text, []);
        }
        if (text.Length != 0) throw new ArgumentException("이 형식의 기록에는 텍스트를 함께 저장할 수 없습니다.");
        if (kind != HistoryKind.ClipboardFiles)
        {
            if (files.Length != 0) throw new ArgumentException("이 형식의 기록에는 파일 목록을 함께 저장할 수 없습니다.");
            return ("", []);
        }
        if (files.Length == 0) throw new ArgumentException("파일 목록이 비어 있습니다.", nameof(files));
        var normalized = new List<string>(files.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (var value in files)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 32_767 || !Path.IsPathFullyQualified(value)
                || value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal)
                || value.IndexOfAny(['\0', '*', '?', '"', '<', '>', '|']) >= 0
                || value.AsSpan(2).Contains(':')
                || value.Split(['\\', '/']).Any(part => part is "." or ".."))
                throw new ArgumentException("파일 목록에는 올바른 절대 경로만 보관할 수 있습니다.", nameof(files));
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!seen.Add(fullPath)) continue;
            characters += fullPath.Length;
            if (characters > MaxClipboardTextLength) throw new ArgumentException("파일 경로 목록의 전체 길이가 보관 범위를 초과했습니다.", nameof(files));
            normalized.Add(fullPath);
        }
        return ("", normalized.ToArray());
    }

    private static Guid LegacyId(string path, DateTime savedAt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant() + "\n" + savedAt.ToString("O")));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string? ExportPath(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
            string fullPath = Path.GetFullPath(path);
            return Path.GetExtension(fullPath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
