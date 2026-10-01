using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture.Tests;

public static class ClipboardFollowerTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        uint sequence = 10;
        var written = new List<BitmapSource>();
        var follower = new ClipboardFollower(() => sequence, image => { written.Add(image); sequence++; });
        var document = new ImageDocument(Solid(40, 30));

        follower.Start(document);
        Assert(written.Count == 1 && follower.IsFollowing(document), "A capture was not copied when following began.");
        document.Add(new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(0, 0, 20, 20), Strength = 8 });
        Assert(follower.Update(document) && written.Count == 2, "An edit to the captured image did not reach the clipboard.");
        results.Add("클립보드 따라가기: 캡처 즉시 복사, 가리기 편집 뒤 최신 합성 결과로 갱신 통과");

        sequence += 5; // Another application copied something.
        Assert(!follower.Update(document) && written.Count == 2 && !follower.IsFollowing(document), "The clipboard was overwritten after the user copied something else.");
        follower.Start(document);
        Assert(!follower.Update(new ImageDocument(Solid(10, 10))) && written.Count == 3, "Opening another image did not end following.");
        results.Add("클립보드 따라가기: 다른 복사나 다른 이미지 열기 뒤에는 덮어쓰지 않음 통과");

        follower.Start(document);
        sequence++; // An explicit copy of the same image by this app.
        follower.Adopt(document);
        Assert(follower.Update(document) && written.Count == 5, "Following stopped after an explicit copy of the same image.");
        follower.Start(document);
        var failing = new ClipboardFollower(() => sequence, _ => throw new InvalidOperationException("busy"));
        var threw = false;
        try { failing.Start(document); } catch (InvalidOperationException) { threw = true; }
        Assert(threw && !failing.IsFollowing(document), "A failed first copy still claimed to follow the image.");
        results.Add("클립보드 따라가기: 직접 복사 뒤 계속 갱신, 첫 복사 실패 시 따라가지 않음 통과");

        var data = ClipboardTransfer.ImageData(Solid(4, 4), keepOutOfHistory: true);
        Assert(Dword(data, ClipboardPolicy.HistoryPermissionFormat) == 0 && Dword(data, ClipboardTransfer.CloudPermissionFormat) == 0, "Automatic copies are not kept out of clipboard history and sync.");
        var plain = ClipboardTransfer.ImageData(Solid(4, 4), keepOutOfHistory: false);
        Assert(!plain.GetDataPresent(ClipboardPolicy.HistoryPermissionFormat), "An explicit copy was kept out of clipboard history.");
        results.Add("클립보드 따라가기: 자동 복사만 Windows 클립보드 기록·동기화 제외 표시 통과");
        return results;
    }

    private static uint Dword(DataObject data, string format) =>
        data.GetData(format, autoConvert: false) is MemoryStream stream && stream.Length == 4 ? BitConverter.ToUInt32(stream.ToArray(), 0) : uint.MaxValue;

    private static BitmapSource Solid(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze(); return bitmap;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Clipboard follow regression: " + message);
    }
}
