using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DamaCapture.Capture;
using DamaCapture.Services;

namespace DamaCapture;

internal sealed class ScrollCaptureWindow : Window
{
    public BitmapSource Result { get; private set; }
    private readonly System.Drawing.Rectangle region;
    private readonly TextBlock state;
    private readonly TextBox manualOverlap = new() { Text = "120", Width = 70 };
    private readonly CheckBox manual = new() { Content = "겹침 직접 지정 (px)" };
    private readonly Button add, auto;
    private bool busy, running, finishing, accepted;
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int frames = 1;
    public ScrollCaptureWindow(BitmapSource first, System.Drawing.Rectangle bounds)
    {
        Background = Ui.Background; Foreground = Ui.Text; FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic"); FontSize = 13;
        Result = first; region = bounds; Icon = Application.Current?.MainWindow?.Icon; Title = "스크롤 캡처 - 담아"; Width = 390; Height = 248; ResizeMode = ResizeMode.NoResize; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.Height; MinHeight = 248;
        var p = new StackPanel { Margin = new Thickness(16) };
        p.Children.Add(new TextBlock { Text = "화면을 아래로 스크롤한 뒤 다음 구간을 추가하세요.\n이전 구간의 일부가 겹치도록 남겨 두세요.", Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) });
        state = Ui.Label($"1개 구간 · {Result.PixelWidth} × {Result.PixelHeight} px", 12); p.Children.Add(state);
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 4) }; add = Ui.Button("다음 구간 추가", async () => await AddFrame(), true); auto = Ui.Button("자동 스크롤", async () => await AutoScroll()); actions.Children.Add(add); actions.Children.Add(auto); p.Children.Add(actions);
        var manualRow = new StackPanel { Orientation = Orientation.Horizontal }; manualRow.Children.Add(manual); manualOverlap.Margin = new Thickness(10, 3, 0, 3); manualRow.Children.Add(manualOverlap); p.Children.Add(manualRow);
        var done = Ui.Button("완료", () => { running = false; finishing = true; accepted = true; if (!busy) Close(); }); done.Width = 78; done.HorizontalAlignment = HorizontalAlignment.Right; done.Margin = new Thickness(0, 12, 0, 0); p.Children.Add(done); Content = p;
        Closing += (_, e) => { running = false; if (busy) { e.Cancel = true; finishing = true; } };
        Closed += (_, _) => completion.TrySetResult(accepted);
    }
    public Task<bool> RunAsync() { Show(); return completion.Task; }
    private async Task<bool> AddFrame(bool scroll = false)
    {
        if (busy) return false;
        busy = true; add.IsEnabled = false;
        try
        {
            Hide(); await Task.Delay(180);
            if (scroll) { if (!CaptureService.ScrollAt(region, -240)) throw new InvalidOperationException("대상 앱이 자동 스크롤에 응답하지 않습니다. 직접 스크롤해 주세요."); await Task.Delay(500); }
            var next = CaptureService.CaptureRectangle(region);
            var overlap = -1;
            if (manual.IsChecked == true && (!int.TryParse(manualOverlap.Text, out overlap) || overlap < 0 || overlap > Math.Min(Result.PixelHeight, next.PixelHeight))) throw new InvalidOperationException("겹침 픽셀 수가 올바르지 않습니다.");
            var stitched = ScrollStitcher.Append(Result, next, out var matched, overlap);
            if (!matched) { state.Text = "연결점을 찾지 못했습니다. 스크롤 양을 줄이거나 겹침을 직접 지정하세요."; return false; }
            if (stitched.PixelHeight == Result.PixelHeight) { state.Text = "화면 변화가 없습니다. 끝에 도달했거나 직접 스크롤이 필요합니다."; return false; }
            Result = stitched; frames++; state.Text = $"{frames}개 구간 · {Result.PixelWidth} × {Result.PixelHeight} px"; return true;
        }
        catch (Exception ex) { state.Text = ex.Message; return false; }
        finally { busy = false; add.IsEnabled = true; if (finishing) Close(); else Show(); }
    }
    private async Task AutoScroll()
    {
        if (running) { running = false; return; }
        running = true; auto.Content = "자동 캡처 중지";
        try
        {
            for (var i = 0; i < 40 && running && !finishing; i++)
            {
                if (!await AddFrame(true)) break;
                await Task.Delay(350);
            }
        }
        finally { running = false; auto.Content = "자동 스크롤"; }
    }
}
