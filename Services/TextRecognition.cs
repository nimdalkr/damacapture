using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DamaCapture.Services;

/// <summary>
/// Reads text out of an image with the OCR engine built into Windows. Everything runs on this PC:
/// no network, no account, no package. Languages come from the installed Windows language packs.
/// </summary>
public static class TextRecognition
{
    public sealed record Result(string Text, string LanguageTag, int LineCount, int WordCount);
    /// <summary>A recognized word with its bounds in the original image's pixel coordinates.</summary>
    public sealed record Word(string Text, Rect Bounds);
    public sealed record Line(IReadOnlyList<Word> Words);

    public static IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            try { return OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray(); }
            catch (Exception) { return []; }
        }
    }

    public static bool IsAvailable => AvailableLanguages.Count > 0;

    public static async Task<Result> RecognizeAsync(BitmapSource image, string? preferredLanguage = null)
    {
        var (recognized, tag, _) = await RecognizeCoreAsync(image, preferredLanguage);
        // Japanese and Chinese words are single characters; other languages keep a space between words.
        var separator = tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "" : " ";
        var lines = recognized.Lines.Select(line => string.Join(separator, line.Words.Select(word => word.Text)).Trim())
            .Where(line => line.Length > 0).ToArray();
        return new Result(string.Join(Environment.NewLine, lines), tag, lines.Length, recognized.Lines.Sum(line => line.Words.Count));
    }

    /// <summary>Words with positions, for callers that need to know where text sits in the image.</summary>
    public static async Task<IReadOnlyList<Line>> RecognizeWordsAsync(BitmapSource image, string? preferredLanguage = null)
    {
        var (recognized, _, scale) = await RecognizeCoreAsync(image, preferredLanguage);
        return recognized.Lines.Select(line => new Line(line.Words.Select(word =>
                new Word(word.Text, new Rect(word.BoundingRect.X / scale, word.BoundingRect.Y / scale, word.BoundingRect.Width / scale, word.BoundingRect.Height / scale))).ToArray()))
            .Where(line => line.Words.Count > 0).ToArray();
    }

    private static async Task<(OcrResult Recognized, string LanguageTag, double Scale)> RecognizeCoreAsync(BitmapSource image, string? preferredLanguage)
    {
        ArgumentNullException.ThrowIfNull(image);
        var engine = CreateEngine(preferredLanguage)
            ?? throw new InvalidOperationException("Windows에 OCR 언어가 설치되어 있지 않습니다. 설정 → 시간 및 언어 → 언어에서 한국어 또는 영어 언어 팩을 추가하세요.");
        var prepared = Prepare(image, (int)OcrEngine.MaxImageDimension, out var scale);
        using var bitmap = await ToSoftwareBitmapAsync(prepared);
        var recognized = await engine.RecognizeAsync(bitmap);
        return (recognized, engine.RecognizerLanguage.LanguageTag, scale);
    }

    private static OcrEngine? CreateEngine(string? preferredLanguage)
    {
        try
        {
            if (preferredLanguage != null && OcrEngine.IsLanguageSupported(new Language(preferredLanguage)))
                return OcrEngine.TryCreateFromLanguage(new Language(preferredLanguage));
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(engine => engine != null);
        }
        catch (Exception) { return null; }
    }

    internal static BitmapSource Prepare(BitmapSource image, int maxDimension) => Prepare(image, maxDimension, out _);

    /// <summary>Small text reads better enlarged; oversized images must fit the engine's limit.</summary>
    internal static BitmapSource Prepare(BitmapSource image, int maxDimension, out double scale)
    {
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        scale = 1;
        if (longest > maxDimension) scale = (double)maxDimension / longest;
        else if (longest < 1200) scale = Math.Min(2, (double)maxDimension / longest);
        if (Math.Abs(scale - 1) < .01) { scale = 1; return image; }
        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>Hands WPF pixels to Windows Runtime imaging through an in-memory PNG.</summary>
    internal static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(BitmapSource image)
    {
        var png = await Task.Run(() => EncodePng(image));
        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(png);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }
}
