using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture.Tests;

/// <summary>Exercises the built-in Windows OCR path on the fictional sample image only.</summary>
public static class TextRecognitionTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        var languages = TextRecognition.AvailableLanguages;
        if (languages.Count == 0)
        {
            results.Add("NOTE: Windows OCR 언어가 설치되어 있지 않아 텍스트 추출 검사를 건너뜀");
            return results;
        }
        var small = ImageFactory.Sample();
        Assert(TextRecognition.Prepare(small, 10000).PixelWidth == small.PixelWidth * 2, "Small images must be enlarged for recognition.");
        var large = new ImageDocument(small).Render();
        Assert(TextRecognition.Prepare(large, 500).PixelWidth <= 500, "Images beyond the engine limit must be scaled down.");

        var recognized = Task.Run(() => TextRecognition.RecognizeAsync(small)).GetAwaiter().GetResult();
        Assert(recognized.LineCount > 0 && recognized.Text.Length > 0, "The sample image contains text but none was recognized.");
        var normalized = recognized.Text.Replace(" ", "");
        Assert(normalized.Contains("example.com") || normalized.Contains("DEMO-2026"), "Recognized text lost the sample's e-mail or document number: " + recognized.Text);

        // Masked pixels are composed before recognition, so hidden text must not come back.
        var masked = new ImageDocument(small);
        masked.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(190, 270, 420, 60) });
        var afterMask = Task.Run(() => TextRecognition.RecognizeAsync(masked.Render())).GetAwaiter().GetResult();
        Assert(!afterMask.Text.Replace(" ", "").Contains("example.com"), "Text under a solid mask was still extracted.");
        results.Add($"텍스트 추출: Windows OCR({string.Join(", ", languages)})로 예시 이미지의 이메일·문서번호 인식, 가리기 뒤 텍스트 제외, 크기 보정 통과");
        return results;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Text recognition regression: " + message);
    }
}
