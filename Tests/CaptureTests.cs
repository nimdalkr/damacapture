using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using DamaCapture.Capture;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;

namespace DamaCapture.Tests;

public static class CaptureTests
{
    /// <summary>STA-only native smoke tests. Captures only an opaque test fixture, never the user's desktop.</summary>
    public static List<string> Run()
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Native capture tests require STA.");
        var results = new List<string>();
        var color = Color.FromArgb(23, 121, 205);
        using (var fixture = new Forms.Form
        {
            AutoScaleMode = Forms.AutoScaleMode.None,
            FormBorderStyle = Forms.FormBorderStyle.None,
            StartPosition = Forms.FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = color,
            Text = "담아 캡처 검사"
        })
        using (var child = new Forms.Panel { BackColor = Color.FromArgb(205, 73, 23), Bounds = new Rectangle(20, 20, 30, 24) })
        {
            fixture.Controls.Add(child);
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var area = screen.WorkingArea;
                var bounds = new Rectangle(area.Left + 12, area.Top + 12, 80, 64);
                fixture.Bounds = bounds;
                if (!fixture.Visible) fixture.Show();
                NativeMethods.SetWindowPos(fixture.Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
                // DPI changes may affect child layout; reapply physical dimensions after window placement.
                child.Bounds = new Rectangle(20, 20, 30, 24);
                fixture.Refresh();
                child.Refresh();
                Forms.Application.DoEvents();
                NativeMethods.DwmFlush();
                Assert(NativeMethods.GetWindowRect(fixture.Handle, out var actual) && actual.Rectangle == bounds,
                    "Test fixture did not retain requested physical dimensions.");

                var image = CaptureService.CaptureRectangle(bounds);
                Assert(image.PixelWidth == 80 && image.PixelHeight == 64 && image.IsFrozen,
                    "Native capture changed physical size or returned a mutable bitmap.");
                AssertColor(image, 8, 8, color);
                AssertColor(image, 30, 30, child.BackColor);

                var nativeWindow = NativeMethods.FindWindowBounds(new Point(bounds.Left + 8, bounds.Top + 8), IntPtr.Zero, false);
                Assert(nativeWindow == bounds, "Window selection did not find the topmost fixture bounds.");
                var nativeChild = NativeMethods.FindWindowBounds(new Point(bounds.Left + 30, bounds.Top + 30), IntPtr.Zero, true);
                Assert(nativeChild == new Rectangle(bounds.Left + 20, bounds.Top + 20, 30, 24),
                    "Element selection did not return the native child bounds.");
            }
            fixture.Close();
        }
        results.Add($"캡처: {Forms.Screen.AllScreens.Length}개 모니터의 실제 픽셀 크기·색상·창 및 요소 좌표 통과");

        CheckCaptureContext();
        results.Add("캡처 정보: 지연 후 활성 창 제목·프로세스·일시, 정보 없음 대체값, 취소 후 이전 정보 제거 통과");

        var firstWindow = new Window { ShowInTaskbar = false };
        var secondWindow = new Window { ShowInTaskbar = false };
        try
        {
            using var first = new GlobalHotkeys(firstWindow);
            using var second = new GlobalHotkeys(secondWindow);
            const uint modifiers = GlobalHotkeys.Control | GlobalHotkeys.Alt | GlobalHotkeys.Shift;
            const uint f24 = 0x87;
            if (first.Register(231, modifiers, f24))
            {
                Assert(!second.Register(232, modifiers, f24), "An occupied global shortcut was not rejected.");
                first.Dispose();
                Assert(second.Register(232, modifiers, f24), "Disposing hotkeys did not release the shortcut.");
                second.Unregister(232);
                Assert(NativeMethods.RegisterHotKey(new System.Windows.Interop.WindowInteropHelper(firstWindow).Handle,
                    233, modifiers | GlobalHotkeys.NoRepeat, f24), "Explicit unregister did not release the shortcut.");
                NativeMethods.UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(firstWindow).Handle, 233);
                results.Add("전역 단축키: 충돌 감지·해제·재등록 통과");
            }
            else results.Add("전역 단축키 검사 생략: 검사 전용 Ctrl+Alt+Shift+F24가 이미 사용 중");
        }
        finally { firstWindow.Close(); secondWindow.Close(); }
        return results;
    }

    private static void CheckCaptureContext()
    {
        var desktop = CaptureService.VirtualBounds();
        // Cover every captured pixel with a fixture before exercising the public full-screen path.
        using var fixture = new Forms.Form
        {
            AutoScaleMode = Forms.AutoScaleMode.None, FormBorderStyle = Forms.FormBorderStyle.None,
            StartPosition = Forms.FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true,
            BackColor = Color.FromArgb(23, 121, 205), Bounds = desktop, Text = "담아 캡처 정보 검사 · 지연 전"
        };
        fixture.Show();
        NativeMethods.SetWindowPos(fixture.Handle, new IntPtr(-1), desktop.X, desktop.Y, desktop.Width, desktop.Height, 0x0040);
        fixture.Activate(); fixture.Refresh(); Forms.Application.DoEvents(); NativeMethods.DwmFlush();
        Assert(NativeMethods.GetWindowRect(fixture.Handle, out var actual) && actual.Rectangle == desktop,
            "Capture metadata fixture did not cover the virtual desktop.");

        using var changeTitle = new Forms.Timer { Interval = 200 };
        var changedAt = DateTimeOffset.MaxValue;
        var foregroundAtChange = IntPtr.Zero;
        CaptureContext? expectedContext = null;
        const string capturedTitle = "담아 캡처 정보 검사 · 지연 후";
        changeTitle.Tick += (_, _) =>
        {
            fixture.Text = capturedTitle;
            changedAt = DateTimeOffset.Now;
            // Windows may decline normal activation. Observe the actual foreground without forcing focus.
            foregroundAtChange = NativeMethods.GetForegroundWindow();
            expectedContext = CaptureContext.ReadWindow(foregroundAtChange, changedAt, CaptureMode.AllScreens);
            changeTitle.Stop();
        };
        changeTitle.Start();
        var service = new CaptureService();
        var image = CompleteOnFormsThread(service.CaptureAsync(CaptureMode.AllScreens, delaySeconds: 1));
        Assert(image != null && image.PixelWidth == desktop.Width && image.PixelHeight == desktop.Height,
            "Full-screen capture did not return the opaque fixture.");
        var context = service.LastContext ?? throw new InvalidOperationException("Successful capture did not publish its context.");
        var expected = expectedContext ?? throw new InvalidOperationException("Capture delay did not run the metadata observation.");
        Assert(context.CapturedAt >= changedAt && context.CapturedAt <= DateTimeOffset.Now,
            "Capture context was recorded before the capture delay or after the frame was returned.");
        Assert(context.WindowTitle == expected.WindowTitle && context.ApplicationName == expected.ApplicationName &&
            context.Mode == nameof(CaptureMode.AllScreens),
            "Capture context did not preserve the observed foreground title, process, or capture mode.");
        var fixtureContext = CaptureContext.ReadWindow(fixture.Handle, context.CapturedAt, CaptureMode.AllScreens);
        using var currentProcess = Process.GetCurrentProcess();
        Assert(fixtureContext.WindowTitle == capturedTitle && fixtureContext.ApplicationName == currentProcess.ProcessName,
            "Reading a known fixture did not preserve its changed title and process.");
        if (foregroundAtChange == fixture.Handle)
            Assert(context.WindowTitle == capturedTitle, "Capture context did not record the delayed foreground title change.");
        var monitor = Forms.Screen.AllScreens[0].Bounds;
        AssertColor(image!, monitor.Left - desktop.Left + 8, monitor.Top - desktop.Top + 8, fixture.BackColor);

        var missing = CaptureContext.ReadWindow(IntPtr.Zero, context.CapturedAt, CaptureMode.Region);
        Assert(missing.WindowTitle == "제목 없는 창" && missing.ApplicationName == "알 수 없는 앱" &&
            missing.CapturedAt == context.CapturedAt && missing.Mode == nameof(CaptureMode.Region),
            "Missing foreground information did not keep a usable context.");

        // Close only the capture service's own selection window; this exercises the real cancellation branch.
        var overlaySeen = false;
        using var cancelSelection = new Forms.Timer { Interval = 100 };
        cancelSelection.Tick += (_, _) =>
        {
            foreach (Forms.Form form in Forms.Application.OpenForms)
            {
                if (form is not SelectionOverlay) continue;
                overlaySeen = true; cancelSelection.Stop(); form.DialogResult = Forms.DialogResult.Cancel; break;
            }
        };
        cancelSelection.Start();
        var canceled = CompleteOnFormsThread(service.CaptureAsync(CaptureMode.Region));
        Assert(overlaySeen && canceled == null && service.LastContext == null && service.SelectedRegion == null,
            "Canceling a selection retained the previous capture context or selected region.");
        fixture.Close();
    }

    private static T CompleteOnFormsThread<T>(Task<T> task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Capture metadata test did not complete.");
            Forms.Application.DoEvents(); Thread.Sleep(5);
        }
        return task.GetAwaiter().GetResult();
    }

    private static void AssertColor(BitmapSource image, int x, int y, Color expected)
    {
        var bytes = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        Assert(Math.Abs(bytes[0] - expected.B) <= 4 && Math.Abs(bytes[1] - expected.G) <= 4 &&
            Math.Abs(bytes[2] - expected.R) <= 4 && bytes[3] == 255,
            "Native capture pixels do not match the opaque test fixture.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
