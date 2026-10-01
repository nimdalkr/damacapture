using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;

namespace DamaCapture.Tests;

public static class ImageEngineTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        Run("Source is immutable 96-DPI BGRA; point arrays are owned", ImmutableSource, results);
        Run("Mosaic changes actual pixels only inside its rectangle", MosaicPixels, results);
        Run("Blur applies a bounded real pixel filter", BlurPixels, results);
        Run("Opaque redaction remains above annotations regardless of insertion order", RedactionOrder, results);
        Run("Add/update/remove support undo, redo, and redo invalidation", EditHistory, results);
        Run("Outside and fractional bounds clamp to image edges", ClampedBounds, results);
        Run("Crop, resize, clockwise rotation remain undoable", Transformations, results);
        Run("All annotation tools produce flattened pixels", AnnotationTools, results);
        Run("PNG, JPG and BMP round-trip flattened masks without layers", EncodedFiles, results);
        Run("Undo retains at most 24 changes", BoundedHistory, results);
        Run("Invalid image sizes and non-finite operations are rejected", InvalidInput, results);
        Run("Fictional demo renders without external resources", DemoImage, results);
        Run("Large images keep undo for edits and always keep the newest step", LargeImageUndo, results);
        Run("Damaged clipboard PNG bytes are rejected without an exception", DamagedPng, results);
        Run("Oversized image dimensions are refused with a Korean message", OversizedFile, results);
        Run("Filled shapes paint inside; text font and weight change the drawn letters", FillAndFont, results);
        return results;
    }

    private static void FillAndFont()
    {
        static BitmapSource White() => BitmapSource.Create(160, 80, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 160 * 80 * 4).ToArray(), 160 * 4);
        static byte[] Draw(EditOperation operation) { var document = new ImageDocument(White()); document.Add(operation); return Pixels(document.Render()); }
        var red = Color.FromRgb(0xEE, 0x20, 0x2E);
        var center = (40 * 160 + 40) * 4;
        var filled = Draw(new EditOperation { Kind = EditKind.Rectangle, Bounds = new Rect(20, 20, 40, 40), Color = red, Filled = true });
        var outlined = Draw(new EditOperation { Kind = EditKind.Rectangle, Bounds = new Rect(20, 20, 40, 40), Color = red });
        Assert(filled[center + 2] == 0xEE && filled[center + 1] == 0x20, "A filled rectangle did not paint its inside.");
        Assert(outlined[center] == 255 && outlined[center + 1] == 255 && outlined[center + 2] == 255, "An outline-only rectangle painted its inside.");
        var ellipse = Draw(new EditOperation { Kind = EditKind.Ellipse, Bounds = new Rect(20, 20, 40, 40), Color = red, Filled = true });
        Assert(ellipse[center + 2] == 0xEE, "A filled ellipse did not paint its inside.");
        EditOperation Label(string font, bool bold) => new() { Kind = EditKind.Text, Bounds = new Rect(4, 4, 150, 70), Text = "가나다 Abc", Color = red, Stroke = 4, Font = font, Bold = bold };
        var original = Draw(Label("", false));
        var malgun = Draw(Label("Malgun Gothic", false));
        var bold = Draw(Label("Malgun Gothic", true));
        Assert(!original.SequenceEqual(malgun) && !malgun.SequenceEqual(bold), "Changing the font or weight did not change the drawn text.");
        Assert(ImageDocument.MeasureText("가나다 Abc", 4, "Malgun Gothic", true).Width >= ImageDocument.MeasureText("가나다 Abc", 4, "Malgun Gothic", false).Width, "Bold text measured narrower than regular text.");
    }

    private static void LargeImageUndo()
    {
        // 36 megapixels: one frame alone exceeds the 128 MiB undo budget.
        const int side = 6000;
        var image = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, new byte[side * side * 4], side * 4);
        var document = new ImageDocument(image);
        document.Add(new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(10, 10, 300, 200), Strength = 18 });
        document.Add(new EditOperation { Kind = EditKind.Rectangle, Bounds = new Rect(10, 10, 300, 200) });
        Assert(document.CanUndo, "Annotation edits on a large image lost their undo history.");
        document.Undo();
        Assert(document.Operations.Count == 1 && document.CanRedo, "Undo on a large image did not restore the previous edits.");
        document.Crop(new Rect(0, 0, side / 2, side / 2));
        Assert(document.CanUndo, "The newest step must survive even when its snapshot exceeds the budget.");
        document.Undo();
        Assert(document.Width == side, "Undoing a crop on a large image did not restore the original size.");
    }

    private static void DamagedPng()
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(ImageFactory.Sample()));
        using var stream = new MemoryStream(); encoder.Save(stream);
        var bytes = stream.ToArray();
        for (var i = 60; i < Math.Min(bytes.Length, 4000); i++) bytes[i] = (byte)(i * 31);
        Assert(Services.ClipboardImageDecoder.TryPng(bytes) == null, "A PNG with a damaged body must decode to null, not throw.");
        Assert(Services.ClipboardImageDecoder.TryPng(stream.ToArray()) != null, "An intact PNG must still decode.");
    }

    private static void OversizedFile()
    {
        // WIC validates a file before exposing its size, so the guard is checked at the size test itself.
        var threw = false;
        try { ImageDocument.ValidateSize(9000, 8000); }
        catch (ArgumentOutOfRangeException error) { threw = error.Message.Contains("6,400만") && error.Message.Contains("9,000"); }
        Assert(threw, "A 72-megapixel size must be refused with the Korean message that names the actual size.");
        ImageDocument.ValidateSize(8000, 8000);
    }

    private static void Run(string name, Action test, List<string> results)
    {
        try { test(); results.Add("PASS: " + name); }
        catch (Exception error) { throw new InvalidOperationException("FAIL: " + name, error); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static BitmapSource Pattern(int width = 48, int height = 36)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            bytes[at] = (byte)(x * 19 % 256);
            bytes[at + 1] = (byte)(y * 37 % 256);
            bytes[at + 2] = (byte)((x + y) % 2 == 0 ? 255 : 0);
            bytes[at + 3] = 255;
        }
        return BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, bytes, width * 4);
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }

    private static bool PixelEquals(byte[] a, byte[] b, int offset)
        => Enumerable.Range(0, 4).All(channel => a[offset + channel] == b[offset + channel]);

    private static void ImmutableSource()
    {
        var document = new ImageDocument(Pattern());
        Assert(document.Source.IsFrozen && document.Render().IsFrozen, "Bitmaps must be frozen.");
        Assert(document.Source.Format == PixelFormats.Bgra32 && document.Source.DpiX == 96 && document.Source.DpiY == 96, "Source format was not normalized.");
        var points = new[] { new Point(2, 2), new Point(15, 15) };
        document.Add(new EditOperation { Kind = EditKind.Pen, Points = points, Bounds = new Rect(2, 2, 13, 13) });
        points[0] = new Point(999, 999);
        var publicOperations = document.Operations;
        publicOperations[0].Points[0] = new Point(-999, -999);
        Assert(document.Operations[0].Points[0] == new Point(2, 2), "External code mutated stored points.");
        Assert(ReferenceEquals(document.Render(), document.Render()), "Unchanged render should be cached.");
    }

    private static void MosaicPixels()
    {
        var document = new ImageDocument(Pattern());
        var original = Pixels(document.Source);
        document.Add(new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(8, 8, 24, 16), Strength = 8 });
        var result = Pixels(document.Render());
        var changed = 0;
        for (var y = 0; y < document.Height; y++)
        for (var x = 0; x < document.Width; x++)
        {
            var at = (y * document.Width + x) * 4;
            if (x >= 8 && x < 32 && y >= 8 && y < 24)
            {
                if (!PixelEquals(original, result, at)) changed++;
            }
            else Assert(PixelEquals(original, result, at), "Mosaic changed a pixel outside its rectangle.");
        }
        Assert(changed > 200, "Mosaic is not replacing image pixels.");
        var first = (8 * document.Width + 8) * 4;
        var last = (15 * document.Width + 15) * 4;
        Assert(Enumerable.Range(0, 4).All(channel => result[first + channel] == result[last + channel]), "Mosaic block is not constant.");
    }

    private static void BlurPixels()
    {
        var document = new ImageDocument(Pattern());
        var original = Pixels(document.Source);
        document.Add(new EditOperation { Kind = EditKind.Blur, Bounds = new Rect(4, 4, 20, 20), Strength = 14 });
        var result = Pixels(document.Render());
        Assert(!original.SequenceEqual(result), "Blur did not change pixels.");
        Assert(PixelEquals(original, result, 0), "Blur changed an outside pixel.");
        var at = (12 * document.Width + 12) * 4;
        Assert(result[at + 2] > 80 && result[at + 2] < 180, "Blur did not smooth the alternating red channel.");
        var small = new ImageDocument(Pattern(1, 1));
        small.Add(new EditOperation { Kind = EditKind.Blur, Bounds = new Rect(0, 0, 1, 1), Strength = 96 });
        Assert(Pixels(small.Render()).SequenceEqual(Pixels(small.Source)), "One-pixel blur should remain valid.");
    }

    private static void RedactionOrder()
    {
        var document = new ImageDocument(Pattern());
        document.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(5, 5, 30, 20), Color = Color.FromArgb(0, 1, 2, 3) });
        document.Add(new EditOperation { Kind = EditKind.Line, Points = [new Point(0, 15), new Point(45, 15)], Color = Colors.Lime, Stroke = 9 });
        var pixels = Pixels(document.Render());
        for (var y = 5; y < 25; y++)
        for (var x = 5; x < 35; x++)
        {
            var at = (y * document.Width + x) * 4;
            Assert(pixels[at] == 3 && pixels[at + 1] == 2 && pixels[at + 2] == 1 && pixels[at + 3] == 255, "Annotation or transparency bypassed opaque mask.");
        }
    }

    private static void EditHistory()
    {
        var document = new ImageDocument(Pattern());
        var original = Pixels(document.Render());
        var edit = new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(2, 2, 10, 10), Color = Colors.Black };
        document.Add(edit);
        var first = Pixels(document.Render());
        document.Update(edit with { Bounds = new Rect(8, 8, 12, 12) });
        var second = Pixels(document.Render());
        document.Remove(edit.Id);
        Assert(Pixels(document.Render()).SequenceEqual(original), "Remove failed.");
        document.Undo();
        Assert(Pixels(document.Render()).SequenceEqual(second), "Undo remove failed.");
        document.Undo();
        Assert(Pixels(document.Render()).SequenceEqual(first), "Undo update failed.");
        document.Undo();
        Assert(Pixels(document.Render()).SequenceEqual(original), "Undo add failed.");
        document.Redo();
        Assert(Pixels(document.Render()).SequenceEqual(first), "Redo add failed.");
        document.Add(new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(15, 15, 8, 8) });
        Assert(!document.CanRedo, "New edit must clear redo history.");
    }

    private static void ClampedBounds()
    {
        var document = new ImageDocument(Pattern());
        var original = Pixels(document.Render());
        document.Add(new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(-100, -100, 20, 20) });
        Assert(Pixels(document.Render()).SequenceEqual(original), "Outside edit must be a no-op.");
        document.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(-2.5, -2.5, 5, 5), Color = Colors.Black });
        var bytes = Pixels(document.Render());
        Assert(bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0, "Negative edges did not clamp.");
        var pixel = (2 * document.Width + 2) * 4;
        Assert(bytes[pixel] == 0 && bytes[pixel + 2] == 0, "Fractional mask did not cover its edge pixel.");
        Assert(PixelEquals(original, bytes, (3 * document.Width + 3) * 4), "Mask exceeded ceiling bounds.");
    }

    private static void Transformations()
    {
        var document = new ImageDocument(Pattern(12, 8));
        document.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(3, 2, 4, 4), Color = Colors.Black });
        document.Crop(new Rect(2, 1, 8, 6));
        Assert(document.Width == 8 && document.Height == 6 && document.Operations.Count == 0, "Crop did not bake current edits.");
        var cropPixels = Pixels(document.Render());
        Assert(cropPixels[(1 * 8 + 1) * 4 + 2] == 0, "Crop lost the redaction.");
        document.Resize(16, 12);
        Assert(document.Width == 16 && document.Height == 12, "Resize size is incorrect.");
        document.Undo();
        Assert(Pixels(document.Render()).SequenceEqual(cropPixels), "Resize undo did not restore cropped pixels.");
        document.Undo();
        Assert(document.Width == 12 && document.Height == 8 && document.Operations.Count == 1, "Crop undo lost editable operations.");
        var rotation = new ImageDocument(Pattern(3, 2));
        var original = Pixels(rotation.Render());
        rotation.Rotate90();
        var rotated = Pixels(rotation.Render());
        Assert(rotation.Width == 2 && rotation.Height == 3, "Rotation dimensions are incorrect.");
        Assert(Enumerable.Range(0, 4).All(channel => original[channel] == rotated[4 + channel]), "Rotation is not clockwise.");
        rotation.Rotate90(); rotation.Rotate90(); rotation.Rotate90();
        Assert(Pixels(rotation.Render()).SequenceEqual(original), "Four rotations did not preserve pixels exactly.");
    }

    private static void AnnotationTools()
    {
        var kinds = new[] { EditKind.Pen, EditKind.Arrow, EditKind.Rectangle, EditKind.Ellipse, EditKind.Line, EditKind.Text, EditKind.Number, EditKind.Highlight };
        foreach (var kind in kinds)
        {
            var document = new ImageDocument(Pattern(120, 90));
            var original = Pixels(document.Render());
            document.Add(new EditOperation { Kind = kind, Bounds = new Rect(18, 18, 64, 42), Points = [new Point(18, 18), new Point(82, 60)], Text = kind == EditKind.Number ? "2" : "가나다", Color = Colors.Blue, Stroke = 4 });
            Assert(!Pixels(document.Render()).SequenceEqual(original), $"{kind} drew no pixels.");
        }
    }

    private static void EncodedFiles()
    {
        var document = new ImageDocument(Pattern(80, 64));
        document.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(8, 8, 60, 48), Color = Colors.Black });
        var pixels = Pixels(document.Render());
        var root = Path.Combine(Path.GetTempPath(), "dama-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var extension in new[] { ".png", ".jpg", ".bmp" })
            {
                var path = Path.Combine(root, "flattened" + extension);
                ImageFiles.Save(document.Render(), path);
                var loaded = ImageFiles.Load(path);
                var bytes = Pixels(loaded);
                Assert(loaded.PixelWidth == 80 && loaded.PixelHeight == 64 && loaded.IsFrozen, "Encoded dimensions or immutability are incorrect.");
                if (extension == ".png" || extension == ".bmp") Assert(bytes.SequenceEqual(pixels), "Lossless format did not preserve pixels.");
                else
                {
                    var center = (32 * 80 + 40) * 4;
                    Assert(bytes[center] < 5 && bytes[center + 1] < 5 && bytes[center + 2] < 5, "JPEG did not retain opaque redaction.");
                }
                // Load uses OnLoad; the file should be immediately renameable and closed.
                var moved = path + ".read-check";
                File.Move(path, moved);
                File.Move(moved, path);
            }
        }
        finally
        {
            // Only remove exact paths generated in this test's unique temporary directory.
            foreach (var path in Directory.EnumerateFiles(root)) File.Delete(path);
            Directory.Delete(root);
        }
    }

    private static void BoundedHistory()
    {
        var document = new ImageDocument(Pattern());
        for (var i = 0; i < 35; i++) document.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(i, 0, 1, 1) });
        var count = 0;
        while (document.CanUndo) { document.Undo(); count++; }
        Assert(count == 24, "Undo history did not respect the 24-state limit.");
    }

    private static void InvalidInput()
    {
        var document = new ImageDocument(Pattern());
        var threw = false;
        try { document.Resize(0, 10); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert(threw, "Zero-width resize was accepted.");
        threw = false;
        try { document.Add(new EditOperation { Kind = EditKind.Pen, Points = [new Point(double.NaN, 1)] }); } catch (ArgumentException) { threw = true; }
        Assert(threw && document.Operations.Count == 0, "Non-finite operation was accepted or modified history.");
    }

    private static void DemoImage()
    {
        var image = ImageFactory.Sample();
        Assert(image.IsFrozen && image.PixelWidth == 1080 && image.PixelHeight == 680, "Demo image is invalid.");
    }
}
