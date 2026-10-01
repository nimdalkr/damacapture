using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Services;

namespace DamaCapture.Tests;

public static class ClipboardMonitorTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        var events = new List<ClipboardCapture>();
        var source = new FakeSource { Sequence = 41 };
        var scheduler = new FakeScheduler();
        var copiedAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(9));
        using var monitor = new ClipboardMonitor(source, scheduler, events.Add,
            () => new ClipboardOrigin(copiedAt, "테스트 문서", "fixture"));
        monitor.OnClipboardChanged();
        Assert(source.ReadCount == 0 && source.Starts == 0, "Monitor must default to disabled.");
        monitor.SetEnabled(true);
        monitor.OnClipboardChanged();
        Assert(source.ReadCount == 0 && source.Starts == 1, "Enabling read pre-existing clipboard contents.");
        source.Sequence++;
        monitor.OnClipboardChanged();
        monitor.OnClipboardChanged();
        Assert(events.Count == 1 && source.ReadCount == 1 && events[0].CopiedAt == copiedAt &&
            events[0].WindowTitle == "테스트 문서", "A sequence was duplicated or lost its event-time origin.");
        results.Add("클립보드: 기본 꺼짐·활성화 전 내용 제외·변경 순서 중복 방지 통과");

        events.Clear();
        source.Sequence++;
        source.Responses.Enqueue(new(ClipboardReadStatus.Busy));
        monitor.OnClipboardChanged();
        Assert(events.Count == 0 && scheduler.Pending == 1, "Busy clipboard was not scheduled for retry.");
        source.Sequence++;
        monitor.OnClipboardChanged();
        scheduler.RunAll();
        Assert(events.Count == 1 && scheduler.Pending == 0, "A newer sequence did not cancel a stale retry.");

        events.Clear();
        source.Sequence++;
        source.Responses.Enqueue(new(ClipboardReadStatus.Busy));
        monitor.OnClipboardChanged();
        monitor.SetEnabled(false);
        scheduler.RunAll();
        Assert(events.Count == 0 && source.Stops == 1, "Disabled monitoring retained an active retry.");
        source.Sequence++;
        monitor.SetEnabled(true);
        monitor.OnClipboardChanged();
        Assert(events.Count == 0, "Re-enabling imported clipboard data copied while disabled.");

        var beforeRetries = source.ReadCount;
        source.AlwaysBusy = true;
        source.Sequence++;
        monitor.OnClipboardChanged();
        scheduler.RunAll();
        Assert(source.ReadCount - beforeRetries == 5 && events.Count == 0, "Busy retries were not bounded to five attempts.");
        source.AlwaysBusy = false;
        source.ChangeSequenceDuringRead = true;
        source.Sequence++;
        monitor.OnClipboardChanged();
        Assert(events.Count == 0, "A result from a changed sequence was accepted.");
        source.ChangeSequenceDuringRead = false;
        scheduler.RunAll();
        Assert(events.Count == 1, "Delayed rendering's latest sequence was permanently dropped without another notification.");
        monitor.OnClipboardChanged();
        Assert(events.Count == 1, "A delayed rendering sequence was delivered twice.");
        events.Clear();
        results.Add("클립보드: 바쁨 재시도 상한·새 복사 및 감시 해제 시 취소·오래된 결과 폐기 통과");

        Assert(ClipboardPolicy.ShouldIgnore(true, false, false, false, null), "Own process not excluded.");
        Assert(ClipboardPolicy.ShouldIgnore(false, true, false, false, null), "App origin marker not excluded.");
        Assert(ClipboardPolicy.ShouldIgnore(false, false, true, false, null), "Monitor exclusion not honored.");
        Assert(ClipboardPolicy.ShouldIgnore(false, false, false, true, 0), "History DWORD zero not honored.");
        Assert(ClipboardPolicy.ShouldIgnore(false, false, false, true, null), "Malformed privacy permission not rejected.");
        Assert(!ClipboardPolicy.ShouldIgnore(false, false, false, true, 1) &&
            !ClipboardPolicy.ShouldIgnore(false, false, false, false, null), "Ordinary copies were excluded.");
        Assert(!ClipboardPolicy.IsValid(new(HistoryKind.ClipboardText,
            new string('a', ClipboardPolicy.MaxTextCharacters + 1), [], null)), "Oversized text accepted.");
        Assert(!ClipboardPolicy.IsValid(new(HistoryKind.ClipboardFiles, "", new string[ClipboardPolicy.MaxFiles + 1], null)),
            "Oversized file list accepted.");
        Assert(!ClipboardImageDecoder.ValidDimensions(64_000_001, 1) && !ClipboardImageDecoder.ValidDimensions(0, 100),
            "Invalid image dimensions accepted.");
        results.Add("클립보드: 자기 복사·보관 제외 형식·텍스트/파일/이미지 상한 정책 통과");

        var pixels = new byte[] { 30, 80, 160, 255, 55, 100, 190, 255 };
        var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        image.Freeze();
        using var pngStream = new MemoryStream();
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); png.Save(pngStream);
        var decodedPng = ClipboardImageDecoder.TryPng(pngStream.ToArray());
        Assert(decodedPng is { PixelWidth: 2, PixelHeight: 1, IsFrozen: true }, "Known PNG failed to decode.");
        var excessivePng = pngStream.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(excessivePng.AsSpan(16), 64_000_001);
        Assert(ClipboardImageDecoder.TryPng(excessivePng) is null, "Oversized PNG reached decoder.");
        using var bmpStream = new MemoryStream();
        var bmp = new BmpBitmapEncoder(); bmp.Frames.Add(BitmapFrame.Create(image)); bmp.Save(bmpStream);
        var encodedBmp = bmpStream.ToArray();
        var dib = encodedBmp.AsSpan(14).ToArray();
        var decodedDib = ClipboardImageDecoder.TryDib(dib);
        Assert(decodedDib is { PixelWidth: 2, PixelHeight: 1, IsFrozen: true }, "Known DIB failed to decode.");
        Assert(ClipboardImageDecoder.TryDib(dib.AsSpan(0, 40).ToArray()) is null, "Truncated DIB was accepted.");
        results.Add("클립보드: 합성 PNG/DIB 형식·크기 사전 검사·불완전 이미지 거부 통과");

        source.Sequence++;
        source.Responses.Enqueue(new(ClipboardReadStatus.Busy));
        monitor.OnClipboardChanged();
        var beforeDispose = source.ReadCount;
        monitor.Dispose();
        scheduler.RunAll();
        Assert(source.ReadCount == beforeDispose, "Disposal retained clipboard work.");
        return results;
    }

    private sealed class FakeSource : IClipboardSource
    {
        public uint Sequence { get; set; }
        public int Starts, Stops, ReadCount;
        public bool AlwaysBusy, ChangeSequenceDuringRead;
        public Queue<ClipboardReadResult> Responses { get; } = new();
        public bool StartListening() { Starts++; return true; }
        public void StopListening() => Stops++;
        public ClipboardReadResult Read(uint expectedSequence)
        {
            ReadCount++;
            if (ChangeSequenceDuringRead) Sequence++;
            if (AlwaysBusy) return new(ClipboardReadStatus.Busy);
            return Responses.Count > 0 ? Responses.Dequeue() :
                new(ClipboardReadStatus.Ready, new ClipboardPayload(HistoryKind.ClipboardText, "fixture", [], null));
        }
    }

    private sealed class FakeScheduler : IClipboardRetryScheduler
    {
        private readonly Queue<Scheduled> queue = new();
        public int Pending { get { var count = 0; foreach (var item in queue) if (!item.Cancelled) count++; return count; } }
        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var item = new Scheduled(callback); queue.Enqueue(item); return item;
        }
        public void RunAll()
        {
            var limit = 20;
            while (queue.Count > 0 && limit-- > 0)
            {
                var item = queue.Dequeue(); if (!item.Cancelled) item.Callback();
            }
            Assert(queue.Count == 0, "Retry queue failed to drain.");
        }
        private sealed class Scheduled(Action callback) : IDisposable
        {
            public bool Cancelled;
            public Action Callback { get; } = callback;
            public void Dispose() => Cancelled = true;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
