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

/// <summary>Sensitive-text detection on a synthetic image; faces only check that the built-in detector runs cleanly.</summary>
public static class SensitiveDetectionTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        var words = new List<TextRecognition.Word>
        {
            new("문의", new Rect(10, 10, 30, 20)), new("010-1234-5678", new Rect(50, 10, 120, 20)),
        };
        var found = SensitiveContentDetector.FindText([new TextRecognition.Line(words)]).ToArray();
        Assert(found.Length == 1 && found[0].Kind == FindingKind.Phone && found[0].Bounds == new Rect(50, 10, 120, 20), "A phone number must map to its own word box only.");
        var card = SensitiveContentDetector.FindText([new TextRecognition.Line(new[]
        {
            new TextRecognition.Word("4111", new Rect(0, 0, 40, 20)), new TextRecognition.Word("1111", new Rect(50, 0, 40, 20)),
            new TextRecognition.Word("1111", new Rect(100, 0, 40, 20)), new TextRecognition.Word("1111", new Rect(150, 0, 40, 20))
        })]).ToArray();
        Assert(card.Length == 1 && card[0].Kind == FindingKind.Card && card[0].Bounds.Width == 190, "A card number spanning four words must cover all of them.");
        var notCard = SensitiveContentDetector.FindText([new TextRecognition.Line(new[] { new TextRecognition.Word("1234", new Rect(0, 0, 40, 20)), new TextRecognition.Word("5678", new Rect(50, 0, 40, 20)), new TextRecognition.Word("9012", new Rect(100, 0, 40, 20)), new TextRecognition.Word("3456", new Rect(150, 0, 40, 20)) })]).ToArray();
        Assert(notCard.Length == 0, "Sixteen digits that fail the Luhn check must not be flagged as a card.");
        var password = SensitiveContentDetector.FindText([new TextRecognition.Line(new[] { new TextRecognition.Word("비밀번호:", new Rect(0, 0, 60, 20)), new TextRecognition.Word("hunter2!", new Rect(70, 0, 60, 20)) })]).ToArray();
        Assert(password.Length == 1 && password[0].Kind == FindingKind.Password && password[0].Bounds.X == 70, "Only the value after a password label must be flagged.");
        results.Add("민감 정보 규칙: 전화번호·카드번호(Luhn)·비밀번호 값 위치 매핑, 무효 카드번호 제외 통과");

        if (!TextRecognition.IsAvailable) { results.Add("NOTE: Windows OCR이 없어 이미지 기반 민감 정보 검사를 건너뜀"); return results; }
        var image = Render(new[] { "담당자 홍길동", "이메일 sample@example.com", "연락처 010-2345-6789", "주민번호 900101-1234567", "카드 4111 1111 1111 1111", "token sk-abcdefghijklmnopqrstuvwxyz0123" });
        var findings = Task.Run(() => SensitiveContentDetector.DetectAsync(image, faces: false)).GetAwaiter().GetResult();
        foreach (var kind in new[] { FindingKind.Email, FindingKind.Phone, FindingKind.ResidentId, FindingKind.Card, FindingKind.Secret })
            Assert(findings.Any(finding => finding.Kind == kind), $"{kind} was not found in the rendered image: " + string.Join(", ", findings.Select(f => f.Label)));
        Assert(findings.All(finding => finding.Kind != FindingKind.Face), "No face exists in the text image.");
        var email = findings.First(finding => finding.Kind == FindingKind.Email);
        Assert(email.Bounds.Y > 40 && email.Bounds.Bottom < 110 && email.Bounds.X > 60, "The e-mail box must sit on its own line, to the right of its label.");

        // Masking the findings must remove them from what OCR can read afterwards.
        var document = new ImageDocument(image);
        foreach (var finding in findings) document.Add(new EditOperation { Kind = EditKind.Mosaic, Bounds = finding.Bounds, Strength = 16 });
        var after = Task.Run(() => TextRecognition.RecognizeAsync(document.Render())).GetAwaiter().GetResult().Text.Replace(" ", "");
        Assert(!after.Contains("example.com") && !after.Contains("2345-6789") && !after.Contains("1234567"), "Masked sensitive text was still readable: " + after);
        results.Add("민감 정보 감지: 합성 이미지에서 이메일·전화·주민번호·카드·토큰 위치 감지, 가리기 후 재인식 불가 통과");

        // The app asks a helper process; its answers must match the in-process detector exactly.
        var viaHelper = Task.Run(() => AnalysisHost.DetectAsync(image)).GetAwaiter().GetResult();
        Assert(AnalysisHost.LastAnsweredByHelper, "Detection fell back to this process instead of the helper.");
        var expected = Task.Run(() => SensitiveContentDetector.DetectAsync(image)).GetAwaiter().GetResult();
        Assert(viaHelper.Count == expected.Count && viaHelper.Zip(expected).All(pair => pair.First.Kind == pair.Second.Kind && pair.First.Bounds == pair.Second.Bounds),
            "The helper's findings differ from in-process detection.");
        var text = Task.Run(() => AnalysisHost.RecognizeAsync(image)).GetAwaiter().GetResult();
        Assert(AnalysisHost.LastAnsweredByHelper && text.Text.Replace(" ", "").Contains("sample@example.com") && text.LineCount >= 5, "Text extraction through the helper lost lines: " + text.Text);
        results.Add("분석 보조 프로세스: 민감 정보 감지·텍스트 추출이 본체와 같은 결과를 보조 프로세스에서 반환");

        if (SensitiveContentDetector.FacesSupported)
        {
            var faces = Task.Run(() => SensitiveContentDetector.DetectAsync(image, text: false)).GetAwaiter().GetResult();
            Assert(faces.Count == 0, "The text image must not produce face findings.");
            results.Add("얼굴 감지: Windows 내장 감지기가 글자 이미지에서 얼굴 0건을 반환");
        }
        else results.Add("NOTE: 이 PC에서 Windows 얼굴 감지를 지원하지 않아 얼굴 검사를 건너뜀");
        return results;
    }

    private static BitmapSource Render(string[] lines)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 700, 40 + lines.Length * 36));
            var typeface = new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            for (var i = 0; i < lines.Length; i++)
                drawing.DrawText(new FormattedText(lines[i], CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, typeface, 22, Brushes.Black, 1), new Point(20, 20 + i * 36));
        }
        var bitmap = new RenderTargetBitmap(700, 40 + lines.Length * 36, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return ImageDocument.Normalize(bitmap);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Sensitive detection regression: " + message);
    }
}
