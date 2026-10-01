using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace DamaCapture.Services;

public enum FindingKind { Face, Email, Phone, Card, ResidentId, Secret, Password }

/// <summary>Something in a capture the user probably wants hidden before sharing, and where it is.</summary>
public sealed record Finding(FindingKind Kind, Rect Bounds)
{
    public string Label => Kind switch
    {
        FindingKind.Face => "얼굴", FindingKind.Email => "이메일", FindingKind.Phone => "전화번호", FindingKind.Card => "카드번호",
        FindingKind.ResidentId => "주민번호", FindingKind.Secret => "키·토큰", _ => "비밀번호"
    };
}

/// <summary>
/// Finds faces and sensitive text with the recognizers built into Windows. Nothing leaves the PC and
/// nothing is stored; the result only marks candidates for the user to mask.
/// </summary>
public static class SensitiveContentDetector
{
    private static readonly (FindingKind Kind, Regex Pattern)[] TextPatterns =
    {
        (FindingKind.Email, new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)),
        (FindingKind.ResidentId, new Regex(@"(?<!\d)\d{6}[-\s]?[1-8]\d{6}(?!\d)", RegexOptions.Compiled)),
        (FindingKind.Card, new Regex(@"(?<!\d)(?:\d{4}[-\s]?){3}\d{4}(?!\d)", RegexOptions.Compiled)),
        (FindingKind.Phone, new Regex(@"(?<!\d)(?:\+82[-\s]?\d{1,2}|01[016789]|0[2-6]\d?|070|080|050\d?)[-.\s]?\d{3,4}[-.\s]?\d{4}(?!\d)", RegexOptions.Compiled)),
        (FindingKind.Secret, new Regex(@"\b(?:sk|pk|rk|ghp|gho|ghu|ghs|glpat|xox[abps])[-_][A-Za-z0-9_\-]{16,}|\bAKIA[0-9A-Z]{16}\b|\bAIza[0-9A-Za-z_\-]{30,}|\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}|(?i:bearer)\s+[A-Za-z0-9._\-]{20,}", RegexOptions.Compiled)),
        (FindingKind.Password, new Regex(@"(?i)(?:password|passwd|pwd|비밀번호|암호|비번)\s*[:=]\s*(?<value>\S{4,})", RegexOptions.Compiled))
    };

    public static bool FacesSupported
    {
        get { try { return FaceDetector.IsSupported; } catch (Exception) { return false; } }
    }

    public static async Task<IReadOnlyList<Finding>> DetectAsync(BitmapSource image, bool faces = true, bool text = true)
    {
        ArgumentNullException.ThrowIfNull(image);
        var findings = new List<Finding>();
        if (text && TextRecognition.IsAvailable)
            findings.AddRange(FindText(await TextRecognition.RecognizeWordsAsync(image)));
        if (faces && FacesSupported)
            findings.AddRange(await FindFacesAsync(image));
        return Merge(findings, image.PixelWidth, image.PixelHeight);
    }

    /// <summary>Regex matches over each line, mapped back to the words that carry them.</summary>
    internal static IEnumerable<Finding> FindText(IReadOnlyList<TextRecognition.Line> lines)
    {
        foreach (var line in lines)
        {
            var text = new System.Text.StringBuilder();
            var spans = new List<(int Start, int End, Rect Bounds)>();
            foreach (var word in line.Words)
            {
                if (text.Length > 0) text.Append(' ');
                spans.Add((text.Length, text.Length + word.Text.Length, word.Bounds));
                text.Append(word.Text);
            }
            var joined = text.ToString();
            foreach (var (kind, pattern) in TextPatterns)
            {
                foreach (Match match in pattern.Matches(joined))
                {
                    var start = match.Index; var end = match.Index + match.Length;
                    if (kind == FindingKind.Password) { var value = match.Groups["value"]; start = value.Index; end = value.Index + value.Length; }
                    if (kind == FindingKind.Card && !Luhn(match.Value)) continue;
                    var hit = spans.Where(span => span.Start < end && span.End > start).Select(span => span.Bounds).ToArray();
                    if (hit.Length == 0) continue;
                    var bounds = hit[0];
                    foreach (var rect in hit.Skip(1)) bounds.Union(rect);
                    yield return new Finding(kind, bounds);
                }
            }
        }
    }

    private static bool Luhn(string digits)
    {
        var sum = 0; var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(digits[i])) continue;
            var digit = digits[i] - '0';
            if (alternate) { digit *= 2; if (digit > 9) digit -= 9; }
            sum += digit; alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    private static async Task<IReadOnlyList<Finding>> FindFacesAsync(BitmapSource image)
    {
        // Screenshots rarely need full resolution to find a face; a smaller frame is much faster.
        var prepared = TextRecognition.Prepare(image, 1600, out var scale);
        if (scale > 1) { prepared = image; scale = 1; }
        using var bitmap = await TextRecognition.ToSoftwareBitmapAsync(prepared);
        using var gray = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
        var detector = await FaceDetector.CreateAsync();
        var faces = await detector.DetectFacesAsync(gray);
        var findings = new List<Finding>();
        foreach (var face in faces)
        {
            var box = face.FaceBox;
            var rect = new Rect(box.X / scale, box.Y / scale, box.Width / scale, box.Height / scale);
            // The detector boxes the eyes-to-chin area; include hair and ears.
            rect.Inflate(rect.Width * .15, rect.Height * .2);
            rect.Offset(0, -rect.Height * .08);
            findings.Add(new Finding(FindingKind.Face, rect));
        }
        return findings;
    }

    /// <summary>Pads each box a little, clamps to the image and drops duplicates of the same kind.</summary>
    private static IReadOnlyList<Finding> Merge(List<Finding> findings, int width, int height)
    {
        var merged = new List<Finding>();
        foreach (var finding in findings.OrderBy(f => f.Bounds.Y).ThenBy(f => f.Bounds.X))
        {
            var bounds = finding.Bounds;
            if (finding.Kind != FindingKind.Face) bounds.Inflate(Math.Max(2, bounds.Height * .15), Math.Max(2, bounds.Height * .15));
            bounds.Intersect(new Rect(0, 0, width, height));
            if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2) continue;
            var duplicate = merged.FindIndex(other => other.Kind == finding.Kind && Overlap(other.Bounds, bounds) > .6);
            if (duplicate >= 0) { var union = merged[duplicate].Bounds; union.Union(bounds); merged[duplicate] = merged[duplicate] with { Bounds = union }; }
            else merged.Add(finding with { Bounds = bounds });
        }
        return merged;
    }

    private static double Overlap(Rect a, Rect b)
    {
        var shared = Rect.Intersect(a, b);
        if (shared.IsEmpty) return 0;
        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller <= 0 ? 0 : shared.Width * shared.Height / smaller;
    }
}
