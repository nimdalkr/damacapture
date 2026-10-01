using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture.Tests;

public static class ServiceTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        string directory = Path.Combine(Path.GetTempPath(), "DamaCapture-service-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Keep test artifacts available for inspection; no user export or application data is accessed.
        var settings = new SettingsStore(Path.Combine(directory, "settings"));
        Assert(settings.Load().KeepHistory && settings.Load().KeepCaptureImages, "Capture metadata and images should be enabled by default.");
        settings.Save(new AppSettings { DelaySeconds = 3, IncludeCursor = true, JpegQuality = 88, ReduceMotion = true });
        var loaded = settings.Load();
        Assert(loaded.DelaySeconds == 3 && loaded.IncludeCursor && loaded.JpegQuality == 88 && loaded.ReduceMotion, "Settings round trip failed.");
        bool writeRejected = false;
        using (var lockedSettings = new FileStream(settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { settings.Save(new AppSettings { JpegQuality = 12 }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { writeRejected = true; }
        }
        Assert(writeRejected && settings.Load().JpegQuality == 88, "A failed settings commit must preserve the previous complete file.");
        settings.Save(new AppSettings { DelaySeconds = -1, JpegQuality = 200, MaxHistory = 5000, RegionHotkey = 0, AfterCapture = "invalid" });
        loaded = settings.Load();
        Assert(loaded.DelaySeconds == 0 && loaded.JpegQuality == 100 && loaded.MaxHistory == 0 && loaded.RegionHotkey == 0x31 && loaded.AfterCapture == "editor", "Settings validation failed.");
        Assert(Directory.GetFiles(Path.GetDirectoryName(settings.FilePath)!, "*.tmp").Length == 0, "Atomic settings left a temporary file.");
        File.WriteAllText(settings.FilePath, "{invalid-json");
        Assert(settings.Load().JpegQuality == 92 && File.ReadAllText(settings.FilePath) == "{invalid-json", "Damaged settings should fall back without overwriting.");
        results.Add("설정: 기본값, 저장·재실행, 범위 검증, 손상 파일 복구 통과");

        // Automatic cleanup: off by default, clamped, and the rule itself keeps the newest and the record being edited.
        Assert(settings.Load().HistoryRetentionDays == 0 && settings.Load().HistoryRetentionCount == 0, "Automatic history cleanup must be off by default.");
        settings.Save(new AppSettings { HistoryRetentionDays = 30, HistoryRetentionCount = -5 });
        loaded = settings.Load();
        Assert(loaded.HistoryRetentionDays == 30 && loaded.HistoryRetentionCount == 0, "Retention settings did not round-trip or clamp.");
        var now = DateTimeOffset.Now;
        HistoryEntry Aged(int daysAgo) => new("", default) { Id = Guid.NewGuid(), CapturedAt = now.AddDays(-daysAgo) };
        var fresh = Aged(0); var tenDays = Aged(10); var fortyDays = Aged(40); var yearOld = Aged(400);
        var all = new[] { tenDays, yearOld, fresh, fortyDays };
        Assert(HistoryStore.Expired(all, 0, 0, now).Count == 0, "Both rules off must expire nothing.");
        var byAge = HistoryStore.Expired(all, 30, 0, now);
        Assert(byAge.Count == 2 && byAge.Contains(fortyDays.Id) && byAge.Contains(yearOld.Id), "The 30-day rule must expire exactly the two older records.");
        var byCount = HistoryStore.Expired(all, 0, 2, now);
        Assert(byCount.SequenceEqual(new[] { fortyDays.Id, yearOld.Id }), "The count rule must keep the two newest and expire the rest, oldest last.");
        var protectedRun = HistoryStore.Expired(all, 30, 1, now, protect: yearOld.Id);
        Assert(protectedRun.Count == 2 && !protectedRun.Contains(yearOld.Id) && !protectedRun.Contains(fresh.Id), "The record being edited must survive both rules while the newest stays.");
        var saved = new HistoryEntry("", DateTime.Now.AddDays(-50)) { Id = Guid.NewGuid() };
        Assert(HistoryStore.Expired(new[] { saved, fresh }, 30, 0, now).SequenceEqual(new[] { saved.Id }), "A record without a capture time must age by its saved time.");
        results.Add("기록 자동 정리: 기본 끄기, 범위 검증, 기간·개수 규칙과 편집 중 기록 보호 통과");

        HistoryTests(directory, results);

        var first = Frame(160, 200, 0);
        var second = Frame(160, 200, 125);
        var joined = ScrollStitcher.Append(first, second, out bool matched);
        Assert(matched && joined.PixelHeight == 325, "75px scrolling overlap was not correctly matched.");
        Assert(Pixels(joined).SequenceEqual(Pixels(Frame(160, 325, 0))), "Joined pixels differ from the complete document.");
        var document = DocumentFrame(240, 260, 0);
        var documentNext = DocumentFrame(240, 260, 173);
        var documentJoined = ScrollStitcher.Append(document, documentNext, out matched);
        Assert(matched && documentJoined.PixelHeight == 433, "White document with sparse text-like detail failed to join.");
        var repeated = ScrollStitcher.Append(joined, second, out matched);
        Assert(matched && ReferenceEquals(joined, repeated), "A repeated frame added duplicate content.");
        var unmatched = ScrollStitcher.Append(first, Frame(160, 200, 500), out matched);
        Assert(!matched && ReferenceEquals(first, unmatched), "Unmatched scrolling frame was silently appended.");
        var explicitJoin = ScrollStitcher.Append(first, Frame(160, 200, 500), out matched, 25);
        Assert(matched && explicitJoin.PixelHeight == 375, "Manual overlap override failed.");
        var incompatible = ScrollStitcher.Append(first, Frame(161, 200, 125), out matched);
        Assert(!matched && ReferenceEquals(first, incompatible), "Different-width images must not be auto-joined.");
        bool sizeRejected = false;
        try { ScrollStitcher.Append(Frame(4, 15_000, 0), Frame(4, 10_000, 100), out _, 0); }
        catch (InvalidOperationException) { sizeRejected = true; }
        Assert(sizeRejected, "Oversized scrolling capture was accepted.");
        results.Add("스크롤: 실제 픽셀 겹침, 반복 프레임, 불일치 중단, 수동 연결, 크기 한도 통과");
        return results;
    }

    private static void HistoryTests(string directory, List<string> results)
    {
        string exportOne = Path.Combine(directory, "export-one.png"), exportTwo = Path.Combine(directory, "export-two.jpg");
        byte[] exportOneBytes = [1, 2, 3], exportTwoBytes = [4, 5, 6];
        File.WriteAllBytes(exportOne, exportOneBytes); File.WriteAllBytes(exportTwo, exportTwoBytes);
        string historyDirectory = Path.Combine(directory, "history");
        var history = new HistoryStore(directory: historyDirectory, maxEntries: 1);
        var capturedAt = new DateTimeOffset(2026, 1, 2, 10, 20, 30, TimeSpan.FromHours(9));
        var capture = history.RecordCapture(capturedAt, "샘플 문서 - 메모장", "notepad", "Region", 24, 20);
        Assert(capture.Id != Guid.Empty && capture.SavedAt == default && capture.Path == "", "Capture and export timestamps must start separately.");
        Assert(history.Add(exportOne) && history.Add(exportTwo) && history.Entries.Count == 3, "The compatibility cap must not limit session history.");
        Assert(!history.SaveSnapshot(capture.Id, Frame(24, 20, 0)) && !Directory.Exists(historyDirectory), "Session-only history must not write metadata or pixels to disk.");
        history.SetPersistence(true);
        for (var i = 0; i < 150; i++) history.RecordCapture(capturedAt.AddMinutes(i + 1), "창 " + i, "sample-app", "Window", 100 + i, 80);
        var reloaded = new HistoryStore(true, historyDirectory, maxEntries: 1);
        Assert(reloaded.Entries.Count == 153 && reloaded.LastError == null, "History must retain more than 150 entries after restarting.");
        var times = reloaded.Entries.Where(entry => entry.CapturedAt != null).Select(entry => entry.CapturedAt!.Value).ToArray();
        Assert(times.SequenceEqual(times.OrderDescending()), "Capture history is not newest first.");
        var priorPosition = history.Entries.FindIndex(entry => entry.Id == capture.Id);
        Assert(history.AttachExport(capture.Id, exportOne), "A capture export was not attached.");
        var attached = history.Find(capture.Id)!;
        Assert(attached.CapturedAt == capturedAt && attached.CapturedAt.Value.Offset == TimeSpan.FromHours(9)
            && attached.WindowTitle == "샘플 문서 - 메모장" && attached.ApplicationName == "notepad" && attached.CaptureMode == "Region"
            && attached.Width == 24 && attached.Height == 20 && attached.SavedAt > capturedAt.UtcDateTime
            && history.Entries.FindIndex(entry => entry.Id == capture.Id) == priorPosition,
            "Exporting must not replace capture metadata or reorder the capture.");
        var entryPath = Path.Combine(historyDirectory, "history", "entries", capture.Id.ToString("N") + ".json");
        var untouchedTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(entryPath, untouchedTime);
        history.RecordCapture(capturedAt.AddDays(1), "별도 캡처", "sample-app", "Region", 24, 20);
        Assert(File.GetLastWriteTimeUtc(entryPath) == untouchedTime, "Recording another capture rewrote previous metadata.");
        Assert(!File.ReadAllText(entryPath).Contains("base64", StringComparison.OrdinalIgnoreCase), "Capture metadata must not embed image bytes.");
        results.Add("기록: 150개 초과 보관·재실행, 캡처 시각·창·앱·저장 시각 분리, 항목별 저장 통과");

        var original = Frame(24, 20, 0);
        var edited = new ImageDocument(original);
        edited.Add(new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(4, 4, 12, 12), Strength = 4 });
        var flattened = edited.Render();
        Assert(!Pixels(flattened).SequenceEqual(Pixels(original)), "The snapshot fixture must contain an actual mosaic edit.");
        Assert(history.SaveSnapshot(capture.Id, flattened), "The flattened capture snapshot could not be saved.");
        var snapshot = history.Find(capture.Id)!.SnapshotPath!;
        var storedPixels = ImageFiles.Load(snapshot);
        Assert(Pixels(storedPixels).SequenceEqual(Pixels(flattened)), "The snapshot contains different pixels from the flattened mosaic.");
        reloaded = new HistoryStore(true, historyDirectory);
        Assert(reloaded.Find(capture.Id)?.ImagePath == snapshot && File.ReadAllBytes(exportOne).SequenceEqual(exportOneBytes), "Snapshot loading must prefer managed pixels without changing exports.");
        Assert(Directory.GetFiles(Path.GetDirectoryName(snapshot)!, "*.png").Length == 1, "Unexpected original or duplicate snapshot was retained.");
        results.Add("기록 이미지: 합성된 모자이크 PNG 저장·재로드, 원본 사본 미생성, 내보낸 파일 보존 통과");

        var corruptFile = Path.Combine(historyDirectory, "history", "entries", Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(corruptFile, "{invalid-json");
        reloaded = new HistoryStore(true, historyDirectory);
        Assert(reloaded.Entries.Count == 154 && reloaded.LastError != null && File.ReadAllText(corruptFile) == "{invalid-json", "One damaged record must not discard the other records or get overwritten.");
        Assert(history.Remove(capture.Id) && File.Exists(snapshot) && File.Exists(exportOne), "Removing a capture must preserve managed and exported PNGs.");
        Assert(new HistoryStore(true, historyDirectory).Find(capture.Id) == null, "A removed capture returned after restart.");
        Assert(history.Remove(exportTwo) && File.ReadAllBytes(exportTwo).SequenceEqual(exportTwoBytes), "Removing a legacy export reference changed the exported file.");
        history.Clear();
        Assert(history.Entries.Count == 0 && new HistoryStore(true, historyDirectory).Entries.Count == 0
            && File.Exists(snapshot) && File.Exists(exportOne) && File.Exists(exportTwo), "Clearing history deleted images or retained live metadata.");
        history.Add(exportTwo); history.SetPersistence(false);
        Assert(new HistoryStore(true, historyDirectory).Entries.Count == 0 && history.Entries.Count == 1, "Disabling persistence must clear active disk references while keeping the session.");
        Assert(!history.Add(Path.Combine(directory, "missing.png")), "Missing exports should not enter history.");
        results.Add("기록 복구: 손상 항목 격리, 제거·비우기·보관 해제 후 사용자 파일과 관리 이미지 보존 통과");

        var legacyDirectory = Path.Combine(directory, "legacy"); Directory.CreateDirectory(legacyDirectory);
        var legacyPath = Path.Combine(legacyDirectory, "history.json");
        var savedAt = new DateTime(2025, 12, 10, 12, 20, 0, DateTimeKind.Utc);
        var legacyJson = new string(' ', 1_048_580) + JsonSerializer.Serialize(new[]
        {
            new { path = exportOne, savedAt }, new { path = exportTwo, savedAt = savedAt.AddMinutes(1) }
        });
        File.WriteAllText(legacyPath, legacyJson);
        var migrated = new HistoryStore(true, legacyDirectory);
        Assert(migrated.Entries.Count == 2 && migrated.LastError == null, "Legacy files above the former 1 MB limit were not migrated.");
        var oldEntry = migrated.Entries.Single(entry => entry.Path == exportOne);
        Assert(oldEntry.Id != Guid.Empty && oldEntry.SavedAt == savedAt && oldEntry.CapturedAt == null && oldEntry.WindowTitle == "",
            "Migration must preserve saved timestamps without fabricating capture metadata.");
        Assert(new HistoryStore(true, legacyDirectory).Entries.Count == 2
            && new HistoryStore(true, legacyDirectory).Find(oldEntry.Id) != null && File.ReadAllText(legacyPath) == legacyJson,
            "Legacy migration duplicated records, changed IDs, or overwrote the original metadata.");
        migrated.Remove(oldEntry.Id);
        Assert(new HistoryStore(true, legacyDirectory).Find(oldEntry.Id) == null, "Legacy migration resurrected a removed capture.");
        results.Add("이전 기록: 1 MB 초과 legacy 파일 이관, 시각·안정 ID 보존, 재이관 중복 방지 통과");

        var blockedDirectory = Path.Combine(directory, "history-blocked"); File.WriteAllText(blockedDirectory, "keep");
        var blocked = new HistoryStore(true, blockedDirectory);
        var retained = blocked.RecordCapture(capturedAt, "저장 실패 테스트", "sample-app", "Region", 24, 20);
        Assert(blocked.Find(retained.Id) != null && blocked.LastError != null, "Metadata failures must leave the capture available in memory.");
        Assert(blocked.AttachExport(retained.Id, exportOne) && blocked.Find(retained.Id)?.Path == exportOne && blocked.LastError != null,
            "Metadata failure must not prevent attaching an already exported file.");
        Assert(!blocked.SaveSnapshot(retained.Id, flattened) && blocked.LastError != null && blocked.Find(retained.Id) != null
            && File.ReadAllText(blockedDirectory) == "keep", "Snapshot failures must be reported without discarding metadata or overwriting unrelated files.");
        results.Add("기록 실패: 메타데이터·PNG 저장 실패에도 메모리 캡처와 내보낸 파일 연결 보존 통과");
    }

    private static BitmapSource Frame(int width, int height, int documentOffset)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            // Stable aperiodic document content, independently generated for overlapping viewports.
            uint value = unchecked((uint)(x + 11) * 2654435761u ^ (uint)(y + documentOffset + 7) * 2246822519u);
            value ^= value >> 13;
            value *= 3266489917u;
            int i = (y * width + x) * 4;
            bytes[i] = (byte)value;
            bytes[i + 1] = (byte)(value >> 8);
            bytes[i + 2] = (byte)(value >> 16);
            bytes[i + 3] = 255;
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        result.Freeze();
        return result;
    }

    private static BitmapSource DocumentFrame(int width, int height, int documentOffset)
    {
        var bytes = new byte[width * height * 4];
        Array.Fill(bytes, (byte)255);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int globalY = y + documentOffset;
            int line = globalY / 23;
            bool text = globalY % 23 is >= 7 and <= 14 && x > 14 && x < 45 + (line * 37 % (width - 60)) && x % 8 < 5;
            if (!text) continue;
            int i = (y * width + x) * 4;
            bytes[i] = bytes[i + 1] = bytes[i + 2] = (byte)(35 + line % 15);
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        result.Freeze();
        return result;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
