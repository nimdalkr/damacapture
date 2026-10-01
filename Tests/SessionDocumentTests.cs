using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture.Tests;

public static class SessionDocumentTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        Check("세션 편집 복원: 모자이크 연산·원본·실행 취소·다시 실행 유지", RestoreEditing, results);
        Check("문서 리비전: 실제 변경만 증가하고 렌더·동일 갱신은 불변", DocumentRevision, results);
        Check("문서 메모리: 원본·렌더·이력의 공유 이미지 중복 집계 방지", MemoryEstimate, results);
        Check("세션 캐시: 개수 제한 없이 메모리 기반 LRU 제거", LeastRecentlyUsed, results);
        Check("세션 캐시: 저장·조회 시 변경 메모리 반영과 단일 초과 문서 유지", MutatedBudget, results);
        return results;
    }

    private static void Check(string name, Action test, List<string> results)
    {
        try { test(); results.Add("PASS: " + name); }
        catch (Exception ex) { throw new InvalidOperationException("FAIL: " + name, ex); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ImageDocument Document(int width = 80, int height = 60)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)((x * 17 + y * 3) % 256);
            pixels[offset + 1] = (byte)((y * 13 + x * 5) % 256);
            pixels[offset + 2] = (byte)((x + y) % 2 == 0 ? 255 : 0);
            pixels[offset + 3] = 255;
        }
        return new ImageDocument(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4));
    }

    private static void RestoreEditing()
    {
        var cache = new SessionDocumentCache();
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        var first = Document();
        var original = ImageDocument.Pixels(first.Source);
        var mask = new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(5, 5, 56, 40), Strength = 8 };
        first.Add(mask);
        var edited = ImageDocument.Pixels(first.Render());
        first.Update(mask with { Strength = 26 });
        first.Undo();
        var revision = first.Revision;
        cache.Store(firstId, first, "첫 캡처", true);
        var second = Document(32, 20);
        second.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(2, 2, 20, 10), Color = Colors.Black });
        cache.Store(secondId, second, "둘째 캡처", false);
        Assert(cache.TryGet(firstId, out var entry), "The first document was not retained.");
        Assert(ReferenceEquals(entry.Document, first) && entry.Name == "첫 캡처" && entry.ExportDirty, "Session metadata or live document identity changed.");
        Assert(first.Revision == revision && first.CanUndo && first.CanRedo && first.Operations.Count == 1, "Restoration lost edit or undo/redo state.");
        Assert(ImageDocument.Pixels(first.Source).SequenceEqual(original) && ImageDocument.Pixels(first.Render()).SequenceEqual(edited), "Restoration replaced the original with flattened pixels.");
        first.Redo();
        Assert(first.Operations[0].Strength == 26 && !ImageDocument.Pixels(first.Render()).SequenceEqual(edited), "Restored redo did not change real mosaic pixels.");
        first.Undo(); first.Undo();
        Assert(first.Operations.Count == 0 && ImageDocument.Pixels(first.Render()).SequenceEqual(original), "Restored undo cannot reach the original.");
        Assert(second.Operations.Count == 1 && second.Operations[0].Kind == EditKind.Solid, "Editing one session changed another.");
        cache.Store(firstId, first, "이름 변경", false);
        Assert(cache.Count == 2 && cache.TryGet(firstId, out entry) && !entry.ExportDirty && entry.Name == "이름 변경", "Storing an existing identifier did not replace its metadata.");
        Assert(cache.Remove(firstId) && !cache.TryGet(firstId, out _), "Explicit removal did not release the cache entry.");
        Assert(first.Width == 80 && first.CanRedo, "Cache removal mutated the document held by its caller.");
    }

    private static void DocumentRevision()
    {
        var document = Document();
        var missing = Guid.NewGuid();
        document.Render(); _ = document.EstimatedMemoryBytes;
        document.Undo(); document.Redo(); document.Remove(missing);
        document.Update(new EditOperation { Id = missing });
        document.Crop(Rect.Empty); document.Crop(new Rect(0, 0, document.Width, document.Height));
        document.Crop(new Rect(-10, -10, 200, 200)); document.Resize(document.Width, document.Height);
        Assert(document.Revision == 0 && !document.CanUndo, "A no-op changed the initial revision/history.");
        var pen = new EditOperation { Kind = EditKind.Pen, Bounds = new Rect(4, 4, 20, 20), Points = [new Point(4, 4), new Point(24, 24)] };
        document.Add(pen); Assert(document.Revision == 1, "Add did not increment the revision once.");
        document.Update(document.Operations[0] with { Points = pen.Points.ToArray() });
        Assert(document.Revision == 1, "An equivalent points array was treated as an edit.");
        document.Update(pen with { Color = Colors.Blue }); Assert(document.Revision == 2, "Update did not increment the revision.");
        document.Undo(); Assert(document.Revision == 3 && document.CanRedo, "Undo must advance a monotonic revision.");
        document.Update(document.Operations[0]); document.Render();
        Assert(document.Revision == 3 && document.CanRedo, "A no-op update/render invalidated redo.");
        document.Redo(); Assert(document.Revision == 4, "Redo did not advance the revision.");
        document.Remove(pen.Id); Assert(document.Revision == 5, "Remove did not advance the revision.");
        document.Crop(new Rect(0, 0, 40, 30)); Assert(document.Revision == 6, "Crop did not advance the revision.");
        document.Resize(20, 15); Assert(document.Revision == 7, "Resize did not advance the revision.");
        document.Rotate90(); Assert(document.Revision == 8, "Rotation did not advance the revision.");
        document.Render(); document.Resize(document.Width, document.Height);
        Assert(document.Revision == 8, "A read or identical transform changed revision.");

        var normalized = Document();
        var mask = new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(1, 1, 20, 20), Strength = 100 };
        normalized.Add(mask); normalized.Update(mask with { Strength = 999 });
        Assert(normalized.Revision == 1, "Equivalent normalized values should not create changes.");
        var singlePixel = Document(1, 1); singlePixel.Rotate90();
        Assert(singlePixel.Revision == 0 && !singlePixel.CanUndo, "Pixel-identical rotation without edits should be a no-op.");
    }

    private static void MemoryEstimate()
    {
        var document = Document(40, 30);
        var sourceBytes = 40L * 30 * 4;
        Assert(document.EstimatedMemoryBytes == sourceBytes, "An untouched document should retain one source bitmap.");
        document.Render();
        Assert(document.EstimatedMemoryBytes == sourceBytes, "Rendering an unchanged source counted the same bitmap twice.");
        document.Add(new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(0, 0, 20, 20) });
        var beforeRender = document.EstimatedMemoryBytes;
        Assert(beforeRender > sourceBytes && beforeRender < sourceBytes * 2, "Operation snapshots overcounted the shared source image.");
        document.Render();
        Assert(document.EstimatedMemoryBytes == beforeRender + sourceBytes, "The composed render bitmap was not counted exactly once.");
        document.Render();
        Assert(document.EstimatedMemoryBytes == beforeRender + sourceBytes, "Repeated renders increased retained memory.");
        document.Crop(new Rect(0, 0, 20, 15));
        Assert(document.EstimatedMemoryBytes >= sourceBytes + 20L * 15 * 4, "Undo's original image was omitted from memory accounting.");
        document.Undo(); document.Redo();
        Assert(document.EstimatedMemoryBytes >= sourceBytes + 20L * 15 * 4, "Undo/redo shared images were not retained in the estimate.");
    }

    private static void LeastRecentlyUsed()
    {
        var unit = Document(16, 16).EstimatedMemoryBytes + 128;
        var cache = new SessionDocumentCache(unit * 2);
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var third = Guid.NewGuid();
        cache.Store(first, Document(16, 16), "", false);
        cache.Store(second, Document(16, 16), "", false);
        Assert(cache.TryGet(first, out _), "First entry was evicted before the budget was reached.");
        cache.Store(third, Document(16, 16), "", false);
        Assert(!cache.TryGet(second, out _) && cache.TryGet(first, out _) && cache.TryGet(third, out _), "Eviction ignored least-recent access.");
        Assert(cache.Count == 2 && cache.EstimatedMemoryBytes <= cache.MemoryBudgetBytes, "Cache did not enforce the byte budget.");

        var many = new SessionDocumentCache();
        for (var i = 0; i < 160; i++) many.Store(Guid.NewGuid(), Document(1, 1), "", false);
        Assert(many.Count == 160, "A hidden entry-count limit was applied.");
        var freshProcessCache = new SessionDocumentCache();
        Assert(freshProcessCache.Count == 0 && !freshProcessCache.TryGet(first, out _), "A new session unexpectedly restored memory-only entries.");
    }

    private static void MutatedBudget()
    {
        var unit = Document(16, 16).EstimatedMemoryBytes + 128;
        foreach (var refreshWithStore in new[] { false, true })
        {
            var cache = new SessionDocumentCache(unit * 2);
            var growing = Document(16, 16); var first = Guid.NewGuid(); var second = Guid.NewGuid();
            cache.Store(first, growing, "", false);
            cache.Store(second, Document(16, 16), "", false);
            growing.Resize(80, 60); growing.Render();
            if (refreshWithStore) cache.Store(first, growing, "changed", true);
            else Assert(cache.TryGet(first, out _), "The newly accessed oversized entry was evicted.");
            Assert(cache.Count == 1 && cache.TryGet(first, out var entry) && ReferenceEquals(entry.Document, growing), "Mutation was not measured on Store/TryGet.");
            Assert(cache.EstimatedMemoryBytes > cache.MemoryBudgetBytes && !cache.TryGet(second, out _), "Newest oversized document must remain alone.");
            var third = Guid.NewGuid();
            cache.Store(third, Document(1, 1), "", false);
            Assert(cache.Count == 1 && !cache.TryGet(first, out _) && cache.TryGet(third, out _), "An older oversized document should not block a newer document.");
        }
    }
}
