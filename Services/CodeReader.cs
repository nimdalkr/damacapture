using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.QrCode.Internal;

namespace DamaCapture.Services;

/// <summary>A QR code found in a capture: what it says and where it is, in image pixels.</summary>
public sealed record CodeFinding(string Text, Rect Bounds)
{
    /// <summary>The address to open when the code holds a web link. Anything else is only shown and copied, never launched.</summary>
    public Uri? Link => Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
}

/// <summary>
/// Reads QR codes with ZXing.Net on this PC. Nothing is stored and nothing leaves the machine; the result only
/// tells the editor where to put a bubble.
/// </summary>
public static class CodeReader
{
    /// <summary>Longest side that is decoded; larger captures are reduced first, which still leaves a thumbnail-sized code readable.</summary>
    private const int MaxSide = 3000;

    public static IReadOnlyList<CodeFinding> Read(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var prepared = TextRecognition.Prepare(image, MaxSide, out var scale);
        var source = prepared.Format == PixelFormats.Bgra32 ? prepared : new FormatConvertedBitmap(prepared, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[checked(width * height * 4)];
        source.CopyPixels(pixels, width * 4, 0);
        var reader = new BarcodeReaderGeneric { AutoRotate = false, Options = { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
        var luminance = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        // Light codes on dark paper are common in dark-mode screenshots; the multi-code path ignores the TryInverted option, so a second pass covers them.
        var results = reader.DecodeMultiple(luminance);
        if (results == null || results.Length == 0) results = reader.DecodeMultiple(luminance.invert()) ?? [];
        var findings = new List<CodeFinding>();
        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result?.Text) || result.ResultPoints == null) continue;
            var points = result.ResultPoints.Where(point => point != null).ToArray();
            if (points.Length < 3) continue;
            var box = Rect.Empty;
            foreach (var point in points) box.Union(new Point(point.X, point.Y));
            // The points are finder-pattern centres, 3.5 modules inside the symbol; pad out to its edge and a little beyond.
            var module = points.OfType<FinderPattern>().Select(pattern => (double)pattern.EstimatedModuleSize).DefaultIfEmpty(Math.Max(box.Width, box.Height) / 14).Average();
            box.Inflate(module * 4, module * 4);
            box = new Rect(box.X / scale, box.Y / scale, box.Width / scale, box.Height / scale);
            box.Intersect(new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            if (box.IsEmpty || box.Width < 4 || box.Height < 4) continue;
            if (findings.Any(other => other.Text == result.Text && other.Bounds.IntersectsWith(box))) continue;
            findings.Add(new CodeFinding(result.Text, box));
        }
        return findings.OrderBy(finding => finding.Bounds.Y).ThenBy(finding => finding.Bounds.X).ToArray();
    }
}
