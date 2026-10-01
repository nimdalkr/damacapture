using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DamaCapture.Imaging;

public static class ImageFactory
{
    /// <summary>A fictional document, safe for demonstration and screenshots.</summary>
    public static BitmapSource Sample()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            // A plain document: no color bands or accent rules, only neutral separators.
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1080, 680));
            Label(drawing, "공유 전, 필요한 부분만 가려 주세요.", 46, 70, 29, Brushes.Black, true);
            Label(drawing, "아래 문서는 기능 확인을 위한 가상 데이터입니다.", 46, 120, 17, Brushes.DimGray);
            var rule = new Pen(new SolidColorBrush(Color.FromRgb(218, 218, 222)), 1);
            drawing.DrawLine(rule, new Point(46, 180), new Point(1034, 180));
            Label(drawing, "담당자", 48, 214, 17, Brushes.DimGray);
            Label(drawing, "홍길동 · 디자인팀", 205, 211, 22, Brushes.Black);
            Label(drawing, "이메일", 48, 272, 17, Brushes.DimGray);
            Label(drawing, "sample@example.com", 205, 269, 22, Brushes.Black);
            Label(drawing, "문서번호", 48, 330, 17, Brushes.DimGray);
            Label(drawing, "DEMO-2026-001", 205, 327, 22, Brushes.Black);
            drawing.DrawLine(rule, new Point(46, 392), new Point(1034, 392));
            Label(drawing, "검토 메모", 48, 428, 19, Brushes.Black, true);
            Label(drawing, "이 영역에 화살표, 펜, 도형, 텍스트를 추가해 보세요.", 48, 474, 21, Brushes.Black);
            Label(drawing, "모자이크를 선택하고 이름이나 이메일 위를 드래그하면 바로 적용됩니다.", 48, 518, 17, Brushes.DimGray);
            drawing.DrawLine(rule, new Point(46, 596), new Point(1034, 596));
            Label(drawing, "예시 이미지 · 가상 데이터", 48, 618, 13, Brushes.DimGray);
            // A code in the memo area, so the demo shows the link bubble.
            DrawCode(drawing, SampleLink, new Point(860, 418), 5);
            Label(drawing, "예시 QR", 866, 572, 13, Brushes.DimGray);
        }
        var result = new RenderTargetBitmap(1080, 680, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        return ImageDocument.Normalize(result);
    }

    public const string SampleLink = "https://example.com/menu";

    /// <summary>Draws a QR code for <paramref name="text"/> with its top-left corner at <paramref name="origin"/> and returns the area it covers.</summary>
    public static Rect DrawCode(DrawingContext drawing, string text, Point origin, double modulePixels, Brush? ink = null, Brush? paper = null)
    {
        var matrix = new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, 0, 0, new System.Collections.Generic.Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.MARGIN] = 0 });
        var quiet = modulePixels * 4;
        var area = new Rect(origin.X - quiet, origin.Y - quiet, matrix.Width * modulePixels + quiet * 2, matrix.Height * modulePixels + quiet * 2);
        drawing.DrawRectangle(paper ?? Brushes.White, null, area);
        ink ??= Brushes.Black;
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                if (matrix[x, y]) drawing.DrawRectangle(ink, null, new Rect(origin.X + x * modulePixels, origin.Y + y * modulePixels, modulePixels, modulePixels));
        return new Rect(origin.X, origin.Y, matrix.Width * modulePixels, matrix.Height * modulePixels);
    }

    private static void Label(DrawingContext drawing, string text, double x, double y, double size, Brush brush, bool bold = false)
    {
        var typeface = new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        drawing.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, typeface, size, brush, 1), new Point(x, y));
    }
}
