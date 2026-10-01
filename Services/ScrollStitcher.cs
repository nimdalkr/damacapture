using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DamaCapture.Services;

/// <summary>Joins downward scroll captures only when a sufficiently detailed, matching overlap can be found.</summary>
public static class ScrollStitcher
{
    public const int MaximumHeight = 20_000;
    public const long MaximumPixels = 64_000_000;

    /// <summary>
    /// Returns current and matched=false on an ambiguous or missing overlap, so a bad frame is never silently appended.
    /// An explicit overlapOverride (0..min heights) bypasses matching after the user selects the seam.
    /// Repeated frames return the original current object with matched=true.
    /// </summary>
    public static BitmapSource Append(BitmapSource current, BitmapSource next, out bool matched, int overlapOverride = -1)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        ValidateSize(current.PixelWidth, current.PixelHeight);
        ValidateSize(next.PixelWidth, next.PixelHeight);
        matched = false;
        if (current.PixelWidth != next.PixelWidth) return current;
        int width = current.PixelWidth, height = current.PixelHeight, nextHeight = next.PixelHeight;
        int maximumOverlap = Math.Min(height, nextHeight);
        if (overlapOverride < -1 || overlapOverride > maximumOverlap)
            throw new ArgumentOutOfRangeException(nameof(overlapOverride));

        byte[] first = Pixels(current), second = Pixels(next);
        if (overlapOverride >= 0)
        {
            var explicitResult = Join(current, first, second, nextHeight, overlapOverride);
            matched = true;
            return explicitResult;
        }

        // This also detects an unchanged viewport at the end of a document after earlier frames have been joined.
        if (height >= nextHeight)
        {
            var repeated = Compare(first, second, width, height - nextHeight, nextHeight, 1, 1);
            if (repeated.MeanError <= .20 && repeated.ChangedFraction <= .002)
            {
                matched = true;
                return current;
            }
        }

        int minimumOverlap = Math.Max(8, Math.Min(32, maximumOverlap / 3));
        if (maximumOverlap < minimumOverlap) return current;
        var candidates = new List<(int Overlap, double Error)>();
        for (int overlap = minimumOverlap; overlap <= maximumOverlap; overlap++)
        {
            var sample = Compare(first, second, width, height - overlap, overlap,
                Math.Max(1, width / 24), Math.Max(1, overlap / 16));
            if (sample.MeanError <= 12 && sample.ChangedFraction <= .20 && sample.DetailedSamples >= 6)
                candidates.Add((overlap, sample.MeanError));
        }

        var valid = new List<(int Overlap, double Error)>();
        foreach (var candidate in candidates.OrderBy(candidate => candidate.Error).Take(40))
        {
            var comparison = Compare(first, second, width, height - candidate.Overlap, candidate.Overlap,
                Math.Max(1, width / 160), 1);
            if (comparison.MeanError <= 3 && comparison.ChangedFraction <= .035 && comparison.DetailedSamples >= 24)
                valid.Add((candidate.Overlap, comparison.MeanError));
        }
        if (valid.Count == 0) return current;
        var best = valid.OrderBy(candidate => candidate.Error).ThenByDescending(candidate => candidate.Overlap).First();
        // Repeating stripes, blank forms and duplicate rows do not uniquely identify a seam.
        if (valid.Any(candidate => Math.Abs(candidate.Overlap - best.Overlap) > 3 && candidate.Error <= best.Error + .25))
            return current;

        var result = Join(current, first, second, nextHeight, best.Overlap);
        matched = true;
        return result;
    }

    private static BitmapSource Join(BitmapSource current, byte[] first, byte[] second, int nextHeight, int overlap)
    {
        if (overlap == nextHeight) return current;
        int width = current.PixelWidth;
        int outputHeight = checked(current.PixelHeight + nextHeight - overlap);
        ValidateSize(width, outputHeight);
        int stride = checked(width * 4);
        byte[] output = new byte[checked(stride * outputHeight)];
        Buffer.BlockCopy(first, 0, output, 0, first.Length);
        Buffer.BlockCopy(second, checked(overlap * stride), output, first.Length, checked((nextHeight - overlap) * stride));
        var joined = BitmapSource.Create(width, outputHeight,
            current.DpiX > 0 ? current.DpiX : 96, current.DpiY > 0 ? current.DpiY : 96,
            PixelFormats.Bgra32, null, output, stride);
        joined.Freeze();
        return joined;
    }

    private readonly record struct Comparison(double MeanError, double ChangedFraction, int DetailedSamples);

    private static Comparison Compare(byte[] first, byte[] second, int width, int firstY, int rows, int xStep, int yStep)
    {
        long difference = 0;
        int changed = 0, count = 0, detailed = 0;
        int stride = width * 4;
        // Compare detail to a local baseline, rather than accepting two almost entirely blank samples.
        int baselineB = second[0], baselineG = second[1], baselineR = second[2];
        for (int y = 0; y < rows; y += yStep)
        {
            int firstRow = (firstY + y) * stride, secondRow = y * stride;
            int offset = xStep == 1 ? 0 : (y * 13) % xStep;
            for (int x = offset; x < width; x += xStep)
            {
                int a = firstRow + x * 4, b = secondRow + x * 4;
                int delta = Math.Abs(first[a] - second[b]) + Math.Abs(first[a + 1] - second[b + 1]) + Math.Abs(first[a + 2] - second[b + 2]);
                difference += delta;
                if (delta > 54) changed++;
                if (Math.Abs(second[b] - baselineB) + Math.Abs(second[b + 1] - baselineG) + Math.Abs(second[b + 2] - baselineR) > 90) detailed++;
                count++;
            }
        }
        return new Comparison(difference / (count * 3.0), changed / (double)count, detailed);
    }

    private static byte[] Pixels(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || height > MaximumHeight || (long)width * height > MaximumPixels)
            throw new InvalidOperationException("스크롤 캡처는 높이 20,000px, 전체 6,400만 픽셀까지 이어 붙일 수 있습니다.");
    }
}
