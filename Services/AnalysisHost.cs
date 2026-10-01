using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DamaCapture.Services;

/// <summary>
/// Runs Windows OCR and face detection in a short-lived copy of this app. Their recognizers hold 60–90 MB that
/// would otherwise stay in the resident tray process for good, and their CPU work no longer competes with the
/// editor. Pixels travel over the child's standard input; nothing is written to disk.
/// </summary>
internal static class AnalysisHost
{
    public const string Argument = "--analyze";
    private const int Magic = 0x414D4144, DetectRequest = 1, TextRequest = 2;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    /// <summary>Whether the most recent request was answered by the helper rather than in this process.</summary>
    internal static bool LastAnsweredByHelper { get; private set; }

    public static Task<IReadOnlyList<Finding>> DetectAsync(BitmapSource image) =>
        RunAsync(DetectRequest, image, ReadFindings, () => SensitiveContentDetector.DetectAsync(image));

    public static Task<TextRecognition.Result> RecognizeAsync(BitmapSource image) =>
        RunAsync(TextRequest, image, ReadText, () => TextRecognition.RecognizeAsync(image));

    private static async Task<T> RunAsync<T>(int request, BitmapSource image, Func<JsonElement, T> read, Func<Task<T>> inProcess)
    {
        LastAnsweredByHelper = false;
        var start = HelperStart();
        if (start == null) return await Task.Run(inProcess);
        JsonDocument document;
        try
        {
            using var process = Process.Start(start) ?? throw new Win32Exception("분석 프로세스를 시작하지 못했습니다.");
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            var output = process.StandardOutput.ReadToEndAsync();
            await Task.Run(() => { using var input = process.StandardInput.BaseStream; WriteRequest(input, request, image); });
            if (await Task.WhenAny(output, Task.Delay(Limit)) != output)
            {
                try { process.Kill(); } catch (Exception) { }
                throw new TimeoutException("이미지 분석이 너무 오래 걸려 멈췄습니다.");
            }
            await process.WaitForExitAsync();
            document = JsonDocument.Parse(await output);
        }
        // The helper could not run or answered nonsense: do the work here rather than lose the feature.
        catch (Exception ex) when (ex is Win32Exception or IOException or JsonException) { return await Task.Run(inProcess); }
        using (document)
        {
            LastAnsweredByHelper = true;
            var root = document.RootElement;
            if (!root.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(root.GetProperty("error").GetString());
            return read(root);
        }
    }

    private static ProcessStartInfo? HelperStart()
    {
        // A single-file build has no assembly path and is its own host; a build output runs through dotnet.
#pragma warning disable IL3000 // The empty single-file value is exactly what selects the first branch.
        var location = typeof(AnalysisHost).Assembly.Location;
#pragma warning restore IL3000
        string file, arguments;
        if (string.IsNullOrEmpty(location)) { file = Environment.ProcessPath ?? ""; arguments = Argument; }
        else
        {
            file = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            arguments = $"\"{location}\" {Argument}";
        }
        if (!File.Exists(file)) return null;
        return new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
    }

    private static void WriteRequest(Stream stream, int request, BitmapSource image)
    {
        var source = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic); writer.Write(request); writer.Write(width); writer.Write(height);
        // Row by row, so a large capture never needs a second full-size copy in this process.
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            source.CopyPixels(new Int32Rect(0, y, width, 1), row, stride, 0);
            writer.Write(row);
        }
    }

    private static IReadOnlyList<Finding> ReadFindings(JsonElement root) => root.GetProperty("findings").EnumerateArray()
        .Select(item => new Finding(Enum.Parse<FindingKind>(item.GetProperty("k").GetString()!),
            new Rect(item.GetProperty("x").GetDouble(), item.GetProperty("y").GetDouble(), item.GetProperty("w").GetDouble(), item.GetProperty("h").GetDouble())))
        .ToArray();

    private static TextRecognition.Result ReadText(JsonElement root) => new(root.GetProperty("text").GetString() ?? "",
        root.GetProperty("lang").GetString() ?? "", root.GetProperty("lines").GetInt32(), root.GetProperty("words").GetInt32());

    /// <summary>The child side: read one request, answer with one JSON object, exit.</summary>
    public static int Serve(Stream input, Stream output)
    {
        string json;
        try { json = Task.Run(() => AnswerAsync(input)).GetAwaiter().GetResult(); }
        catch (Exception ex) { json = JsonSerializer.Serialize(new { ok = false, error = (ex as AggregateException)?.InnerException?.Message ?? ex.Message }); }
        var bytes = new UTF8Encoding(false).GetBytes(json);
        output.Write(bytes, 0, bytes.Length); output.Flush();
        return 0;
    }

    private static async Task<string> AnswerAsync(Stream input)
    {
        using var reader = new BinaryReader(input);
        if (reader.ReadInt32() != Magic) throw new InvalidDataException("잘못된 분석 요청입니다.");
        var request = reader.ReadInt32(); var width = reader.ReadInt32(); var height = reader.ReadInt32();
        ImageDocumentGuard(width, height);
        var pixels = reader.ReadBytes(checked(width * height * 4));
        if (pixels.Length != width * height * 4) throw new EndOfStreamException("이미지가 끝까지 전달되지 않았습니다.");
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        if (request == DetectRequest)
        {
            var findings = await SensitiveContentDetector.DetectAsync(image);
            return JsonSerializer.Serialize(new { ok = true, findings = findings.Select(f => new { k = f.Kind.ToString(), x = f.Bounds.X, y = f.Bounds.Y, w = f.Bounds.Width, h = f.Bounds.Height }) });
        }
        if (request == TextRequest)
        {
            var result = await TextRecognition.RecognizeAsync(image);
            return JsonSerializer.Serialize(new { ok = true, text = result.Text, lang = result.LanguageTag, lines = result.LineCount, words = result.WordCount });
        }
        throw new InvalidDataException("알 수 없는 분석 요청입니다.");
    }

    private static void ImageDocumentGuard(int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > 64_000_000) throw new InvalidDataException("분석할 수 없는 이미지 크기입니다.");
    }
}
