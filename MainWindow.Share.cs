using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using DamaCapture.Services;
using Forms = System.Windows.Forms;

namespace DamaCapture;

/// <summary>
/// Hands a capture to the user's own AI chat in the browser. Nothing is uploaded by the app:
/// the image goes to the clipboard for the user to paste, and text rides in the page address
/// only where the service documents a prompt parameter.
/// </summary>
internal sealed partial class MainWindow
{
    // Services are named in text only; their logos are their owners' trademarks and are not shipped.
    internal sealed record ChatService(string Key, string Name, string Url, string? TextUrl);
    internal static readonly ChatService[] ChatServices =
    {
        new("chatgpt", "ChatGPT", "https://chatgpt.com/", "https://chatgpt.com/?q={0}"),
        new("claude", "Claude", "https://claude.ai/new", "https://claude.ai/new?q={0}"),
        new("gemini", "Gemini", "https://gemini.google.com/app", null)
    };
    // Browsers and servers stop accepting very long addresses well before this.
    private const int MaxTextInUrl = 4000;
    private bool extractingText;

    private void SendImageToChat(ChatService service)
    {
        if (!RequireImage()) return;
        Try(() =>
        {
            surface.CompletePendingEdit();
            ClipboardTransfer.SetImage(document!.Render());
            OpenBrowser(service.Url);
            var hint = $"이미지를 복사했습니다. {service.Name}에 Ctrl+V로 붙여넣으세요";
            Toast(hint);
            tray?.ShowBalloonTip(4000, "담아", hint, Forms.ToolTipIcon.Info);
        });
    }

    /// <summary>Returns the message to show. Text that fits travels in the address; otherwise it is pasted.</summary>
    private string SendTextToChat(ChatService service, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "보낼 텍스트가 없습니다.";
        string hint;
        if (service.TextUrl != null && text.Length <= MaxTextInUrl)
        {
            OpenBrowser(string.Format(service.TextUrl, Uri.EscapeDataString(text)));
            hint = $"{service.Name}을(를) 열었습니다.";
        }
        else
        {
            ClipboardTransfer.SetText(text);
            OpenBrowser(service.Url);
            hint = $"텍스트를 복사했습니다. {service.Name}에 Ctrl+V로 붙여넣으세요.";
        }
        Notify(hint);
        tray?.ShowBalloonTip(4000, "담아", hint, Forms.ToolTipIcon.Info);
        return hint;
    }

    private static void OpenBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private string? toolBeforeExtract;

    /// <summary>The button toggles a drag mode; the drag or a double click starts recognition.</summary>
    private void ExtractText()
    {
        if (!RequireImage() || extractingText) return;
        if (surface.Tool == "Extract") { LeaveExtractMode(); return; }
        surface.CompletePendingEdit();
        toolBeforeExtract = surface.Tool;
        SelectTool("Extract");
    }

    private void LeaveExtractMode()
    {
        if (surface.Tool != "Extract") return;
        SelectTool(toolBeforeExtract is null or "Extract" ? "Select" : toolBeforeExtract);
    }

    private async void ExtractTextFrom(Rect bounds)
    {
        if (document == null || extractingText) return;
        LeaveExtractMode();
        extractingText = true; Notify("텍스트 추출 중…");
        // The composed image is used, so masked areas never reach the text.
        BitmapSource composed = document.Render();
        var x = (int)Math.Clamp(Math.Floor(bounds.X), 0, document.Width - 1);
        var y = (int)Math.Clamp(Math.Floor(bounds.Y), 0, document.Height - 1);
        var width = (int)Math.Clamp(Math.Ceiling(bounds.Right) - x, 1, document.Width - x);
        var height = (int)Math.Clamp(Math.Ceiling(bounds.Bottom) - y, 1, document.Height - y);
        if (width < document.Width || height < document.Height)
        {
            var crop = new CroppedBitmap(composed, new Int32Rect(x, y, width, height)); crop.Freeze(); composed = crop;
        }
        TextRecognition.Result? result = null; Exception? failure = null;
        try { result = await AnalysisHost.RecognizeAsync(composed); }
        catch (Exception ex) { failure = ex; }
        // Recognition runs off the UI thread; come back explicitly instead of relying on the caller's context.
        await Dispatcher.InvokeAsync(() =>
        {
            extractingText = false;
            if (failure != null)
            {
                Notify("텍스트 추출 실패: " + failure.Message);
                MessageBox.Show(this, failure.Message, "텍스트 추출", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (result!.Text.Length == 0) { Toast("글자를 찾지 못했습니다"); return; }
            Notify("텍스트 추출 완료");
            new TextResultWindow(this, result, ChatServices, SendTextToChat).ShowDialog();
        });
    }
}
