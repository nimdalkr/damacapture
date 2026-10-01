using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DamaCapture.Imaging;

public static class ImageFiles
{
    public static BitmapSource Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Read the header first so an oversized file is refused before its pixels are decoded.
        var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        ImageDocument.ValidateSize(header.PixelWidth, header.PixelHeight);
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return ImageDocument.Normalize(decoder.Frames[0]);
    }

    /// <summary>Saves flattened pixels with no edit layers or source metadata.</summary>
    public static void Save(BitmapSource image, string path, int jpegQuality = 92)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder encoder = extension switch
        {
            ".png" => new PngBitmapEncoder(),
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => throw new NotSupportedException("PNG, JPG, BMP, TIFF 형식으로 저장할 수 있습니다.")
        };
        var normalized = ImageDocument.Normalize(image);
        if (encoder is JpegBitmapEncoder)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                var bounds = new Rect(0, 0, normalized.PixelWidth, normalized.PixelHeight);
                drawing.DrawRectangle(Brushes.White, null, bounds);
                drawing.DrawImage(normalized, bounds);
            }
            var opaque = new RenderTargetBitmap(normalized.PixelWidth, normalized.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            opaque.Render(visual);
            normalized = ImageDocument.Normalize(opaque);
        }
        encoder.Frames.Add(BitmapFrame.Create(normalized));
        var fullPath = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".dama-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
                stream.Flush(true);
            }
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
