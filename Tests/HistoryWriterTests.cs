using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Services;

namespace DamaCapture.Tests;

public static class HistoryWriterTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        string directory = Path.Combine(Path.GetTempPath(), "DamaCapture-history-writer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new HistoryStore(true, directory);
        var first = store.RecordCapture(DateTimeOffset.Now, "첫 번째 창", "메모장", "Region", 8, 8);
        var second = store.RecordCapture(DateTimeOffset.Now, "두 번째 창", "브라우저", "Window", 8, 8);
        var writer = new HistorySnapshotWriter(store);
        int completed = 0, failures = 0;
        writer.Completed += (_, saved) =>
        {
            Interlocked.Increment(ref completed);
            if (!saved) Interlocked.Increment(ref failures);
        };

        for (int i = 0; i < 120; i++)
        {
            writer.Enqueue(first.Id, Frame((byte)i));
            writer.Enqueue(second.Id, Frame((byte)(255 - i)));
        }
        writer.Enqueue(first.Id, Frame(231));
        writer.Enqueue(second.Id, Frame(42));
        Flush(writer);
        string firstPath = store.Find(first.Id)?.SnapshotPath ?? throw new InvalidOperationException("First snapshot path is missing.");
        string secondPath = store.Find(second.Id)?.SnapshotPath ?? throw new InvalidOperationException("Second snapshot path is missing.");
        Assert(Pixels(Read(firstPath)).SequenceEqual(Pixels(Frame(231))), "An older edit overwrote the latest first-entry snapshot.");
        Assert(Pixels(Read(secondPath)).SequenceEqual(Pixels(Frame(42))), "Interleaved entries lost their latest snapshot.");
        Assert(completed >= 2 && !writer.HasFailures, "Successful writes were not reported.");
        Assert(writer.PendingOrFailed(first.Id) is null && writer.PendingOrFailed(second.Id) is null,
            "Successfully stored images must not remain in the memory fallback.");
        results.Add("기록 저장: 연속 편집·교차 캡처의 최종 이미지 보존, 성공 후 메모리 해제 통과");

        var replacement = Frame(177);
        using (var lockedImage = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            writer.Enqueue(first.Id, replacement);
            Flush(writer);
            var fallback = writer.PendingOrFailed(first.Id);
            Assert(writer.HasFailures && failures > 0 && fallback is { IsFrozen: true }, "A failed write did not retain its frozen image.");
            Assert(Pixels(fallback!).SequenceEqual(Pixels(replacement)), "The failed-image fallback returned an older revision.");
            Assert(Pixels(Read(firstPath)).SequenceEqual(Pixels(Frame(231))), "A failed snapshot update damaged the existing PNG.");
        }
        Flush(writer);
        Assert(!writer.HasFailures && writer.PendingOrFailed(first.Id) is null, "A recovered write did not release its fallback.");
        Assert(Pixels(Read(firstPath)).SequenceEqual(Pixels(replacement)), "Retry did not save the newest failed image.");
        using (var lockedImage = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            writer.Enqueue(first.Id, Frame(31));
            Flush(writer);
            Assert(writer.HasFailures, "The removal fixture did not fail its write.");
            writer.Forget(first.Id);
            Assert(!writer.HasFailures && writer.PendingOrFailed(first.Id) is null, "Removing a failed entry must release its memory fallback.");
            Flush(writer);
        }
        Assert(Pixels(Read(firstPath)).SequenceEqual(Pixels(replacement)), "Forgetting a failed entry must not retry its discarded edit.");
        results.Add("기록 저장 실패: 기존 PNG 보존, 최신 이미지 메모리 유지, 재시도 복구·제거 후 메모리 해제 통과");

        var mutable = new WriteableBitmap(8, 8, 96, 96, PixelFormats.Bgra32, null);
        mutable.WritePixels(new Int32Rect(0, 0, 8, 8), Pixels(Frame(65)), 8 * 4, 0);
        writer.Enqueue(second.Id, mutable);
        mutable.WritePixels(new Int32Rect(0, 0, 8, 8), Pixels(Frame(199)), 8 * 4, 0);
        Flush(writer);
        Assert(!mutable.IsFrozen && Pixels(Read(secondPath)).SequenceEqual(Pixels(Frame(65))),
            "Enqueue must isolate mutable callers before crossing threads.");
        results.Add("기록 스냅숏: 변경 가능한 입력을 분리·동결해 이후 원본 변경과 무관하게 저장 통과");
        CheckDeletion(directory, results);
        return results;
    }

    private static void CheckDeletion(string directory, List<string> results)
    {
        var storeDirectory = Path.Combine(directory, "delete-tests");
        var store = new HistoryStore(true, storeDirectory);
        var writer = new HistorySnapshotWriter(store);
        var entry = store.RecordCapture(DateTimeOffset.Now, "삭제 검사", "sample", "Region", 8, 8);
        var export = Path.Combine(directory, "preserved-export.png");
        byte[] exportBytes = [19, 87, 201, 3];
        File.WriteAllBytes(export, exportBytes);
        Assert(store.AttachExport(entry.Id, export), "The exported-file fixture was not attached.");
        writer.Enqueue(entry.Id, Frame(10));
        Flush(writer);
        var managedPath = store.Find(entry.Id)!.SnapshotPath!;

        using (var started = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            writer.BeforeSnapshotWrite = id =>
            {
                if (id != entry.Id) return;
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Active history writer was not released.");
            };
            Task<bool>? deletion = null;
            try
            {
                writer.Enqueue(entry.Id, Frame(20));
                Assert(started.Wait(TimeSpan.FromSeconds(15)), "The race fixture did not reach an active write.");
                writer.Enqueue(entry.Id, Frame(30));
                deletion = writer.DeleteAsync(entry.Id);
                Assert(!deletion.IsCompleted, "Deletion did not wait for its active snapshot write.");
                writer.Enqueue(entry.Id, Frame(40));
            }
            finally { release.Set(); }
            Assert(AwaitDeletion(deletion!), "Deleting the managed image failed after its active write completed.");
            Flush(writer);
            writer.BeforeSnapshotWrite = null;
        }
        Assert(store.Find(entry.Id) is null && !File.Exists(managedPath) && writer.PendingOrFailed(entry.Id) is null,
            "Deleting an active capture retained its entry, PNG, or pending image.");
        writer.Enqueue(entry.Id, Frame(50));
        Flush(writer);
        Assert(!File.Exists(managedPath) && new HistoryStore(true, storeDirectory).Find(entry.Id) is null,
            "A queued or stale edit resurrected a deleted image or its metadata.");
        Assert(File.ReadAllBytes(export).SequenceEqual(exportBytes), "Deleting a managed snapshot changed its exported file.");
        results.Add("기록 삭제: 진행 중 저장·대기 편집 종료 후 관리 PNG 제거, 재등장 방지, 내보낸 파일 보존 통과");

        var locked = store.RecordCapture(DateTimeOffset.Now, "잠금 검사", "sample", "Region", 8, 8);
        writer.Enqueue(locked.Id, Frame(60));
        Flush(writer);
        var lockedPath = store.Find(locked.Id)!.SnapshotPath!;
        using (var lockedImage = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            writer.Enqueue(locked.Id, Frame(70));
            Flush(writer);
            Assert(writer.IsFailed(locked.Id), "The failed-write fixture did not report its individual failure.");
            Assert(!AwaitDeletion(writer.DeleteAsync(locked.Id)) && store.LastError is not null,
                "A locked PNG deletion must report failure.");
            Assert(store.Find(locked.Id) is not null && File.Exists(lockedPath) && writer.IsFailed(locked.Id)
                && Pixels(writer.PendingOrFailed(locked.Id)!).SequenceEqual(Pixels(Frame(70))),
                "Failed deletion discarded its entry or latest recoverable image.");
        }
        Assert(AwaitDeletion(writer.DeleteAsync(locked.Id)) && !File.Exists(lockedPath)
            && writer.PendingOrFailed(locked.Id) is null && !writer.IsFailed(locked.Id),
            "Retrying a failed deletion did not finish or release its recovered image.");

        var metadata = store.RecordCapture(DateTimeOffset.Now, "정보만 보관", "sample", "Window", 8, 8);
        Assert(store.AttachExport(metadata.Id, export), "The metadata-only export was not attached.");
        var metadataPath = Path.Combine(storeDirectory, "history", "entries", metadata.Id.ToString("N") + ".json");
        using (var lockedRecord = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert(!AwaitDeletion(writer.DeleteAsync(metadata.Id)) && store.LastError is not null && store.Find(metadata.Id) is not null,
                "A failed tombstone write must remain visible and keep a retryable entry.");
        Assert(AwaitDeletion(writer.DeleteAsync(metadata.Id)) && new HistoryStore(true, storeDirectory).Find(metadata.Id) is null
            && File.ReadAllBytes(export).SequenceEqual(exportBytes), "Deleting metadata-only history touched its export or failed to persist.");

        var hidden = store.RecordCapture(DateTimeOffset.Now, "숨김 실패 검사", "sample", "Region", 8, 8);
        Assert(store.AttachExport(hidden.Id, export), "The hide fixture did not attach its export.");
        writer.Enqueue(hidden.Id, Frame(75));
        Flush(writer);
        var hiddenImage = store.Find(hidden.Id)!.SnapshotPath!;
        var hiddenPixels = File.ReadAllBytes(hiddenImage);
        var hiddenRecord = Path.Combine(storeDirectory, "history", "entries", hidden.Id.ToString("N") + ".json");
        using (var lockedRecord = new FileStream(hiddenRecord, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert(!store.Remove(hidden.Id) && store.LastError is not null && store.Find(hidden.Id) is not null
                && new HistoryStore(true, storeDirectory).Find(hidden.Id) is not null,
                "A failed hide must report failure and retain its in-memory and persisted entry.");
            Assert(File.ReadAllBytes(hiddenImage).SequenceEqual(hiddenPixels) && File.ReadAllBytes(export).SequenceEqual(exportBytes),
                "A failed hide changed the managed PNG or exported file.");
        }
        Assert(store.Remove(hidden.Id) && store.LastError is null && store.Find(hidden.Id) is null
            && new HistoryStore(true, storeDirectory).Find(hidden.Id) is null && File.ReadAllBytes(hiddenImage).SequenceEqual(hiddenPixels)
            && File.ReadAllBytes(export).SequenceEqual(exportBytes), "Retrying hide must persist its tombstone and preserve every image.");
        results.Add("기록 제거 실패: 파일·메타데이터 잠금 오류 표시, 삭제·숨김 실패 후 항목 보존과 재시도 통과");

        var overlap = store.RecordCapture(DateTimeOffset.Now, "내보내기 경로 검사", "sample", "Region", 8, 8);
        writer.Enqueue(overlap.Id, Frame(80));
        Flush(writer);
        var overlapPath = store.Find(overlap.Id)!.SnapshotPath!;
        Assert(store.AttachExport(overlap.Id, overlapPath), "The overlap fixture did not attach its existing export.");
        Assert(!AwaitDeletion(writer.DeleteAsync(overlap.Id)) && store.LastError is not null && File.Exists(overlapPath)
            && store.Find(overlap.Id) is not null, "An explicit export path must never become a deletion target.");

        var tampered = store.RecordCapture(DateTimeOffset.Now, "경로 변조 검사", "sample", "Region", 8, 8);
        writer.Enqueue(tampered.Id, Frame(90));
        Flush(writer);
        var tamperedEntry = store.Find(tampered.Id)!;
        var tamperedPath = Path.Combine(storeDirectory, "history", "entries", tampered.Id.ToString("N") + ".json");
        File.WriteAllText(tamperedPath, JsonSerializer.Serialize(new
        {
            id = tampered.Id, deleted = false, entry = tamperedEntry with { SnapshotPath = export }
        }, SettingsStore.JsonOptions));
        var reloaded = new HistoryStore(true, storeDirectory);
        Assert(reloaded.DeleteManagedImage(tampered.Id) && !File.Exists(tamperedEntry.SnapshotPath)
            && File.ReadAllBytes(export).SequenceEqual(exportBytes), "A stored snapshot path redirected deletion outside the managed image.");

        var onlyMetadataDirectory = Path.Combine(directory, "delete-metadata-only");
        var onlyMetadataStore = new HistoryStore(true, onlyMetadataDirectory);
        var onlyMetadata = onlyMetadataStore.RecordCapture(DateTimeOffset.Now, "정보만 있는 저장소", "sample", "Region", 8, 8);
        Assert(onlyMetadataStore.DeleteManagedImage(onlyMetadata.Id)
            && !Directory.Exists(Path.Combine(onlyMetadataDirectory, "history", "images")),
            "Deleting metadata-only history should not require or create an image directory.");
        results.Add("기록 삭제 경계: 내보내기 경로 중복·변조 방어, 이미지 폴더 없는 기록 제거 통과");
    }

    private static bool AwaitDeletion(Task<bool> deletion)
    {
        Assert(deletion.Wait(TimeSpan.FromSeconds(15)), "History deletion did not complete.");
        return deletion.GetAwaiter().GetResult();
    }

    private static void Flush(HistorySnapshotWriter writer)
    {
        Task flush = writer.FlushAsync();
        Assert(flush.Wait(TimeSpan.FromSeconds(15)), "History writer did not drain its queue.");
        flush.GetAwaiter().GetResult();
    }

    private static BitmapSource Frame(byte value)
    {
        var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = (byte)(255 - value);
            pixels[i + 2] = (byte)(value / 2);
            pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
        image.Freeze();
        return image;
    }

    private static BitmapSource Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        image.Freeze();
        return image;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        BitmapSource source = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
