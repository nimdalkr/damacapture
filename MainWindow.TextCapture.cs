using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using DamaCapture.Capture;
using DamaCapture.Services;
using Forms = System.Windows.Forms;
using CaptureMode = DamaCapture.Capture.CaptureMode;

namespace DamaCapture;

/// <summary>
/// 글자 캡처: choose an area and its text goes straight to the clipboard. The picture itself is not kept: it opens
/// no editor, adds no record and leaves 이전 영역 alone, so reading a line off the screen costs nothing else.
/// </summary>
internal sealed partial class MainWindow
{
    private async Task StartTextCapture()
    {
        if (removingHistory || closingHistory || capturing || OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) return;
        capturing = true;
        // A minimized window counts too: it was on the taskbar and must stay there.
        var wasShown = IsVisible;
        try
        {
            await HideForCaptureAsync();
            var image = await capture.CaptureAsync(CaptureMode.Region, remember: false);
            // The window comes back without taking the focus, so the text can be pasted where the user was.
            if (wasShown) { ShowActivated = false; Show(); ShowActivated = true; }
            if (image == null) { Notify("캡처를 취소했습니다."); return; }
            Notify("글자 읽는 중…");
            var result = await AnalysisHost.RecognizeAsync(image);
            await Dispatcher.InvokeAsync(() =>
            {
                var message = result.Text.Length == 0 ? "글자를 찾지 못했습니다" : "글자를 복사했습니다";
                if (result.Text.Length > 0) ClipboardTransfer.SetText(result.Text);
                if (IsVisible) Toast(message); else { Notify(message); tray?.ShowBalloonTip(1500, "담아", message, Forms.ToolTipIcon.Info); }
            });
        }
        catch (Exception ex)
        {
            if (wasShown && !IsVisible) { ShowActivated = false; Show(); ShowActivated = true; }
            Notify("글자 캡처 실패: " + ex.Message);
            if (!IsVisible) tray?.ShowBalloonTip(2500, "담아", "글자 캡처 실패: " + ex.Message, Forms.ToolTipIcon.Warning);
        }
        finally { capturing = false; NativeMethods.SetWindowTransitions(new WindowInteropHelper(this).Handle, true); }
    }
}
