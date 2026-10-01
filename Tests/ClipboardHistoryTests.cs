using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Services;

namespace DamaCapture.Tests;

public static class ClipboardHistoryTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "DamaCapture-clipboard-history-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, "clipboard");
        var store = new HistoryStore(true, directory);
        var copiedAt = new DateTimeOffset(2026, 1, 3, 14, 25, 36, TimeSpan.FromHours(9));
        var plainText = "\r\n   \n  첫 번째 줄  \r\n둘째 줄\t끝  ";
        var first = store.RecordClipboard(HistoryKind.ClipboardText, copiedAt, "복사 검사 창", "sample-editor", plainText);
        Assert(first.Kind == HistoryKind.ClipboardText && first.IsClipboard && !first.HasImage
            && first.Text == plainText && first.DisplayTitle == "첫 번째 줄" && first.CapturedAt == copiedAt,
            "Clipboard text must preserve its exact payload and derive a clean first-line title.");
        for (var i = 1; i <= 130; i++)
            store.RecordClipboard(HistoryKind.ClipboardText, copiedAt.AddSeconds(i), "문서 " + i, "sample-editor", "내용 " + i);
        var maximumText = new string('한', HistoryStore.MaxClipboardTextLength);
        var largest = store.RecordClipboard(HistoryKind.ClipboardText, copiedAt.AddMinutes(3), "큰 텍스트", "sample-editor", maximumText);
        var reloaded = new HistoryStore(true, directory);
        Assert(reloaded.Entries.Count == 132 && reloaded.LastError is null && reloaded.Find(first.Id)?.Text == plainText
            && reloaded.Find(largest.Id)?.Text == maximumText, "Clipboard history lost items or payloads during reload.");
        var times = reloaded.Entries.Select(entry => entry.CapturedAt).ToArray();
        Assert(times.SequenceEqual(times.OrderByDescending(time => time)), "Clipboard entries must remain newest first.");
        Assert(largest.DisplayTitle.Length <= 121 && new FileInfo(RecordPath(directory, largest.Id)).Length > 1_048_576,
            "The long-text fixture must exercise a multi-megabyte record with a compact display title.");
        var captureStore = new HistoryStore(true, Path.Combine(root, "capture"));
        captureStore.RecordCapture(copiedAt, "별도 캡처", "sample", "Region", 8, 8);
        captureStore.SetPersistence(false);
        Assert(new HistoryStore(true, directory).Entries.Count == 132, "Changing capture persistence modified the independent clipboard archive.");
        results.Add("클립보드 기록: 130개 초과 재실행 보존, 복사 시각·원문·짧은 제목, 큰 텍스트·캡처 저장소 분리 통과");

        CheckFilesAndImages(root, directory, store, copiedAt, results);
        CheckValidation(root, directory, store, copiedAt, results);
        CheckLegacy(root, copiedAt, results);
        CheckPersistenceRecovery(root, copiedAt, results);
        return results;
    }

    private static void CheckFilesAndImages(string root, string directory, HistoryStore store, DateTimeOffset copiedAt, List<string> results)
    {
        var source = Path.Combine(root, "source-document.txt");
        var folder = Path.Combine(root, "source-folder");
        Directory.CreateDirectory(folder);
        var nested = Path.Combine(folder, "untouched.txt");
        File.WriteAllText(source, "source remains unchanged");
        File.WriteAllText(nested, "nested source remains unchanged");
        string[] input = [source, source.ToUpperInvariant(), folder];
        var files = store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt.AddMinutes(5), "원본 폴더", "explorer", filePaths: input);
        input[0] = "changed after recording";
        Assert(files.IsClipboard && !files.HasImage && files.FilePaths.SequenceEqual(new[] { source, folder })
            && files.DisplayTitle == "source-document.txt 외 1개", "File references must be copied, normalized, deduplicated, and titled.");
        var reloaded = new HistoryStore(true, directory);
        Assert(reloaded.Find(files.Id)?.FilePaths.SequenceEqual(new[] { source, folder }) == true,
            "Clipboard file references did not survive reload.");
        Assert(store.Remove(files.Id) && new HistoryStore(true, directory).Find(files.Id) is null,
            "Removing a file-list archive did not persist its tombstone.");
        var filesToDelete = store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt.AddMinutes(6), "원본 폴더", "explorer", filePaths: [source, folder]);
        Assert(store.DeleteManagedImage(filesToDelete.Id) && new HistoryStore(true, directory).Find(filesToDelete.Id) is null
            && File.ReadAllText(source) == "source remains unchanged" && File.ReadAllText(nested) == "nested source remains unchanged",
            "Deleting a clipboard file-list archive modified its source files or traversed its folder.");

        var text = store.RecordClipboard(HistoryKind.ClipboardText, copiedAt, "텍스트 삭제", "sample", "archived-only-content");
        Assert(!store.SaveSnapshot(text.Id, Frame()) && store.LastError is not null, "Text records must reject image snapshots.");
        Assert(store.Remove(text.Id) && !File.ReadAllText(RecordPath(directory, text.Id)).Contains("archived-only-content", StringComparison.Ordinal),
            "Removing text history must replace its archived payload with a tombstone.");

        var image = store.RecordClipboard(HistoryKind.ClipboardImage, copiedAt.AddMinutes(7), "복사한 그림", "sample-image", width: 8, height: 8);
        var writer = new HistorySnapshotWriter(store);
        writer.Enqueue(image.Id, Frame());
        Complete(writer.FlushAsync());
        var managedImage = store.Find(image.Id)!.SnapshotPath!;
        reloaded = new HistoryStore(true, directory);
        Assert(image.IsClipboard && image.HasImage && image.DisplayTitle == "복사한 그림"
            && reloaded.Find(image.Id)?.ImagePath == managedImage && File.Exists(managedImage),
            "A clipboard image must use the managed snapshot writer and survive reload.");
        var export = Path.Combine(root, "image-export.png");
        byte[] exportedBytes = [31, 49, 82];
        File.WriteAllBytes(export, exportedBytes);
        Assert(store.AttachExport(image.Id, export), "The clipboard-image export fixture could not be attached.");
        var deletion = writer.DeleteAsync(image.Id);
        Complete(deletion);
        Assert(deletion.Result && !File.Exists(managedImage) && File.ReadAllBytes(export).SequenceEqual(exportedBytes),
            "Deleting a clipboard image must remove only its managed PNG and preserve exports.");
        results.Add("클립보드 형식: 파일 참조 중복 제거·원본 보존, 텍스트 제거, 이미지 비동기 저장·삭제 통과");
    }

    private static void CheckValidation(string root, string directory, HistoryStore store, DateTimeOffset copiedAt, List<string> results)
    {
        int before = store.Entries.Count;
        AssertRejected(() => store.RecordClipboard(HistoryKind.Capture, copiedAt, "", "", "text"));
        AssertRejected(() => store.RecordClipboard((HistoryKind)99, copiedAt, "", "", "text"));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardText, copiedAt, "", ""));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardText, copiedAt, "", "", new string('x', HistoryStore.MaxClipboardTextLength + 1)));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt, "", "", filePaths: []));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt, "", "", filePaths: ["relative.txt"]));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt, "", "", filePaths: ["C:relative.txt"]));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt, "", "", filePaths: [Path.Combine(root, "..", "outside.txt")]));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt, "", "", filePaths: ["\\\\.\\PhysicalDrive0"]));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt, "", "", filePaths: [Path.Combine(root, "name.txt:stream")]));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardImage, copiedAt, "", "", text: "unexpected"));
        AssertRejected(() => store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt, "", "", filePaths:
            Enumerable.Repeat(Path.Combine(root, "reference.txt"), HistoryStore.MaxClipboardFiles + 1).ToArray()));
        Assert(store.Entries.Count == before, "Invalid clipboard payloads must not create partial entries.");

        var maximumFiles = Enumerable.Range(0, HistoryStore.MaxClipboardFiles)
            .Select(index => Path.Combine(root, "references-not-opened", index + ".txt")).ToArray();
        var maximumFileRecord = store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt, "최대 파일 목록", "sample", filePaths: maximumFiles);
        Assert(maximumFileRecord.FilePaths.Length == HistoryStore.MaxClipboardFiles
            && !Directory.Exists(Path.Combine(root, "references-not-opened")), "The supported file-count boundary must retain references without accessing targets.");
        var missing = Path.Combine(root, "never-created", "reference.txt");
        var reference = store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt, "참조", "sample", filePaths: [missing]);
        Assert(reference.FilePaths[0] == missing && !Directory.Exists(Path.GetDirectoryName(missing)),
            "Recording file references must not resolve, open, or create their source targets.");

        var invalidEntries = new[]
        {
            new HistoryEntry("", default) { Id = Guid.NewGuid(), Kind = (HistoryKind)77 },
            new HistoryEntry("", default) { Id = Guid.NewGuid(), Kind = HistoryKind.ClipboardText, Text = new string('x', HistoryStore.MaxClipboardTextLength + 1) },
            new HistoryEntry("", default) { Id = Guid.NewGuid(), Kind = HistoryKind.ClipboardFiles, FilePaths = ["relative.txt"] },
            new HistoryEntry("", default) { Id = Guid.NewGuid(), Kind = HistoryKind.ClipboardImage, Text = "wrong type payload" }
        };
        foreach (var invalid in invalidEntries)
            File.WriteAllText(RecordPath(directory, invalid.Id), JsonSerializer.Serialize(new { id = invalid.Id, deleted = false, entry = invalid }, SettingsStore.JsonOptions));
        var oversizedPath = RecordPath(directory, Guid.NewGuid());
        using (var oversized = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write)) oversized.SetLength(4 * 1024 * 1024 + 1);
        var reloaded = new HistoryStore(true, directory);
        Assert(reloaded.LastError is not null && invalidEntries.All(entry => reloaded.Find(entry.Id) is null)
            && reloaded.Find(reference.Id) is not null && reloaded.Find(maximumFileRecord.Id)?.FilePaths.Length == HistoryStore.MaxClipboardFiles
            && new FileInfo(oversizedPath).Length == 4 * 1024 * 1024 + 1,
            "Malformed or oversized records must be rejected without damaging other entries or rewriting input.");
        results.Add("클립보드 검증: 형식·텍스트·파일 수 한도, 상대·탐색·장치 경로 거부, 손상·과대 JSON 격리 통과");
    }

    private static void CheckLegacy(string root, DateTimeOffset copiedAt, List<string> results)
    {
        var directory = Path.Combine(root, "legacy-default-kind");
        var entries = Path.Combine(directory, "history", "entries");
        Directory.CreateDirectory(entries);
        var id = Guid.NewGuid();
        var export = Path.Combine(root, "legacy-export.png");
        File.WriteAllBytes(export, [1, 3, 5]);
        File.WriteAllText(RecordPath(directory, id), JsonSerializer.Serialize(new
        {
            id, deleted = false,
            entry = new { id, path = export, savedAt = copiedAt.UtcDateTime, capturedAt = copiedAt, windowTitle = "이전 캡처" }
        }, SettingsStore.JsonOptions));
        var store = new HistoryStore(true, directory);
        var old = store.Find(id);
        Assert(old is { Kind: HistoryKind.Capture, IsClipboard: false, HasImage: true } && old.Text == ""
            && old.FilePaths.Length == 0 && old.ImagePath == export && old.DisplayTitle == "이전 캡처" && store.LastError is null,
            "Records written before clipboard support must retain their capture kind and image behavior.");
        results.Add("이전 기록 호환: Kind 없는 캡처는 기존 이미지 기록으로 복원, 기본 필드·제목 보존 통과");
    }

    private static void CheckPersistenceRecovery(string root, DateTimeOffset copiedAt, List<string> results)
    {
        var directory = Path.Combine(root, "metadata-write-failure");
        Directory.CreateDirectory(Path.Combine(directory, "history"));
        var blocker = Path.Combine(directory, "history", "entries");
        File.WriteAllText(blocker, "preserve blocker");
        var source = Path.Combine(root, "retry-source.txt");
        File.WriteAllText(source, "preserve source");
        var store = new HistoryStore(true, directory);
        var text = store.RecordClipboard(HistoryKind.ClipboardText, copiedAt, "실패한 텍스트", "sample", "retry this exact text");
        var files = store.RecordClipboard(HistoryKind.ClipboardFiles, copiedAt.AddSeconds(1), "실패한 파일 목록", "sample", filePaths: [source]);
        var removed = store.RecordClipboard(HistoryKind.ClipboardText, copiedAt.AddSeconds(2), "제거할 실패 항목", "sample", "remove before retry");
        Assert(store.HasPersistenceFailures && store.LastError is not null && store.Find(text.Id) is not null
            && store.Find(files.Id) is not null && !store.RetryFailedEntries(),
            "Failed text and file metadata must remain tracked and available until they persist.");

        var preservedBlocker = Path.Combine(directory, "blocker-preserved.txt");
        File.Move(blocker, preservedBlocker);
        Assert(store.Remove(removed.Id), "Removing a pending metadata entry should persist a tombstone when storage recovers.");
        var succeeded = store.RecordClipboard(HistoryKind.ClipboardText, copiedAt.AddSeconds(3), "성공한 새 항목", "sample", "later success");
        Assert(store.LastError is null && store.HasPersistenceFailures,
            "A later successful record must not clear failures belonging to older text or file records.");
        Assert(store.RetryFailedEntries() && !store.HasPersistenceFailures && store.LastError is null,
            "Retrying recovered metadata storage did not clear its failure state.");
        var reloaded = new HistoryStore(true, directory);
        Assert(reloaded.Entries.Count == 3 && reloaded.Find(text.Id)?.Text == text.Text
            && reloaded.Find(files.Id)?.FilePaths.SequenceEqual(new[] { source }) == true && reloaded.Find(succeeded.Id) is not null
            && reloaded.Find(removed.Id) is null && File.ReadAllText(source) == "preserve source"
            && File.ReadAllText(preservedBlocker) == "preserve blocker",
            "Metadata recovery lost clipboard contents, resurrected a removed record, or modified source files.");

        var disabledDirectory = Path.Combine(root, "disabled-metadata-retry");
        Directory.CreateDirectory(Path.Combine(disabledDirectory, "history"));
        var disabledBlocker = Path.Combine(disabledDirectory, "history", "entries");
        File.WriteAllText(disabledBlocker, "preserve disabled blocker");
        var disabled = new HistoryStore(true, disabledDirectory);
        var noRetry = disabled.RecordClipboard(HistoryKind.ClipboardText, copiedAt, "보관 해제", "sample", "do not restore");
        Assert(disabled.HasPersistenceFailures, "The disabled-persistence fixture did not begin with a failed entry.");
        disabled.SetPersistence(false);
        File.Move(disabledBlocker, Path.Combine(disabledDirectory, "blocker-preserved.txt"));
        Assert(!disabled.HasPersistenceFailures && disabled.RetryFailedEntries() && !File.Exists(RecordPath(disabledDirectory, noRetry.Id)),
            "Retrying after persistence is disabled must not write previously failed entries.");
        Assert(disabled.Remove(noRetry.Id), "Removing a session-only record failed.");
        disabled.SetPersistence(true);
        Assert(disabled.RetryFailedEntries() && new HistoryStore(true, disabledDirectory).Find(noRetry.Id) is null,
            "A metadata retry resurrected a removed entry after persistence was re-enabled.");
        results.Add("클립보드 저장 실패: 텍스트·파일 목록의 미저장 상태 유지·재시도 복구, 제거·보관 해제 항목 재생성 방지 통과");
    }

    private static string RecordPath(string directory, Guid id) => Path.Combine(directory, "history", "entries", id.ToString("N") + ".json");

    private static BitmapSource Frame()
    {
        var bytes = Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray();
        var image = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, bytes, 8 * 4);
        image.Freeze();
        return image;
    }

    private static void Complete(Task task)
    {
        Assert(task.Wait(TimeSpan.FromSeconds(15)), "Clipboard-history background work did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static void AssertRejected(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid clipboard payload was accepted.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
