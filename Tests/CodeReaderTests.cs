using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture.Tests;

/// <summary>QR decoding on synthetic images, link safety rules, and the helper process answering the same as in-process reading.</summary>
public static class CodeReaderTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        const string link = "https://example.com/menu/october?table=4";
        const string wifi = "WIFI:T:WPA;S:cafe;P:secret;;";
        Rect drawnLink = default, drawnWifi = default;
        var image = Render(700, 500, drawing =>
        {
            drawnLink = ImageFactory.DrawCode(drawing, link, new Point(300, 120), 6);
            drawnWifi = ImageFactory.DrawCode(drawing, wifi, new Point(60, 300), 4);
        });
        var found = CodeReader.Read(image);
        Assert(found.Count == 2, $"Two codes were drawn but {found.Count} were read: " + string.Join(" | ", found.Select(f => f.Text)));
        var first = found.First(f => f.Text == link); var second = found.First(f => f.Text == wifi);
        Assert(Overlap(first.Bounds, drawnLink) > .7, $"The link code's box {first.Bounds} does not sit on the drawn code {drawnLink}.");
        Assert(Overlap(second.Bounds, drawnWifi) > .7, $"The Wi-Fi code's box {second.Bounds} does not sit on the drawn code {drawnWifi}.");
        Assert(found[0] == first && found[1] == second, "Codes must be ordered top to bottom.");
        Assert(first.Link?.Host == "example.com" && first.Link.PathAndQuery == "/menu/october?table=4", "The web link was not recognized as openable.");
        Assert(second.Link == null, "A Wi-Fi payload must not be treated as a link.");
        results.Add("QR 읽기: 합성 이미지의 코드 2개를 내용·위치와 함께 읽고 링크·텍스트를 구분");

        // Only web addresses may be opened; everything else is shown and copied.
        Assert(new CodeFinding("javascript:alert(1)", new Rect(0, 0, 1, 1)).Link == null, "A javascript: payload must never be openable.");
        Assert(new CodeFinding("file:///C:/Windows/System32/cmd.exe", new Rect(0, 0, 1, 1)).Link == null, "A file: payload must never be openable.");
        Assert(new CodeFinding("ms-settings:network", new Rect(0, 0, 1, 1)).Link == null, "A custom scheme must never be openable.");
        Assert(new CodeFinding("example.com/menu", new Rect(0, 0, 1, 1)).Link == null, "A bare domain without a scheme is text, not a link.");
        Assert(new CodeFinding("  HTTP://Example.COM/a b  ", new Rect(0, 0, 1, 1)).Link?.AbsoluteUri == "http://example.com/a%20b", "An http link with surrounding space must still open.");
        results.Add("QR 링크 규칙: http·https만 열기 대상, javascript·file·사용자 지정 스킴·스킴 없는 주소는 텍스트로 취급");

        // Dark-mode screenshots carry light codes on dark paper.
        var inverted = Render(400, 400, drawing => { drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 400, 400)); ImageFactory.DrawCode(drawing, link, new Point(100, 100), 6, Brushes.White, Brushes.Black); });
        var invertedFound = CodeReader.Read(inverted);
        Assert(invertedFound.Count == 1 && invertedFound[0].Text == link, "A light-on-dark code was not read.");
        var plain = Render(600, 200, drawing => { var typeface = new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal); drawing.DrawText(new FormattedText("QR 코드가 없는 문서", CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, typeface, 24, Brushes.Black, 1), new Point(20, 20)); });
        Assert(CodeReader.Read(plain).Count == 0, "Text without a code must not produce a finding.");
        var sample = CodeReader.Read(ImageFactory.Sample());
        Assert(sample.Count == 1 && sample[0].Text == ImageFactory.SampleLink && sample[0].Link != null, "The demo image must carry exactly one link code.");
        results.Add("QR 읽기: 반전된 코드 인식, 코드 없는 이미지 0건, 예시 이미지의 링크 코드 1건");

        // The app asks a helper process; its answers must match in-process reading exactly, with sensitive detection left off.
        var viaHelper = Task.Run(() => AnalysisHost.AnalyzeAsync(image, sensitive: false, codes: true)).GetAwaiter().GetResult();
        Assert(AnalysisHost.LastAnsweredByHelper, "Code reading fell back to this process instead of the helper.");
        Assert(viaHelper.Findings.Count == 0, "Sensitive detection ran although it was not requested.");
        Assert(viaHelper.Codes.Count == found.Count && viaHelper.Codes.Zip(found).All(pair => pair.First == pair.Second), "The helper's codes differ from in-process reading.");
        var codesOff = Task.Run(() => AnalysisHost.AnalyzeAsync(image, sensitive: false, codes: false)).GetAwaiter().GetResult();
        Assert(codesOff.Codes.Count == 0 && codesOff.Findings.Count == 0, "Nothing was requested but the helper still answered with results.");
        results.Add("분석 보조 프로세스: QR 읽기가 본체와 같은 결과를 반환하고 요청하지 않은 분석은 하지 않음");
        return results;
    }

    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            draw(drawing);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return ImageDocument.Normalize(bitmap);
    }

    private static double Overlap(Rect a, Rect b)
    {
        var shared = Rect.Intersect(a, b);
        if (shared.IsEmpty) return 0;
        var union = a.Width * a.Height + b.Width * b.Height - shared.Width * shared.Height;
        return union <= 0 ? 0 : shared.Width * shared.Height / union;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Code reader regression: " + message);
    }
}
