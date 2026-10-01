using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DamaCapture.Capture;

/// <summary>The foreground application at the instant the desktop pixels were captured.</summary>
public sealed record CaptureContext(DateTimeOffset CapturedAt, string WindowTitle, string ApplicationName, string Mode)
{
    internal static CaptureContext ReadForeground(CaptureMode mode)
    {
        var window = NativeMethods.GetForegroundWindow();
        var capturedAt = DateTimeOffset.Now;
        return ReadWindow(window, capturedAt, mode);
    }

    internal static CaptureContext ReadWindow(IntPtr window, DateTimeOffset capturedAt, CaptureMode mode)
    {
        var title = "제목 없는 창";
        var application = "알 수 없는 앱";
        if (window != IntPtr.Zero)
        {
            // Window titles can change or disappear while another app is closing.
            var length = NativeMethods.GetWindowTextLength(window);
            if (length > 0)
            {
                var buffer = new StringBuilder(Math.Min(length, 4096) + 1);
                if (NativeMethods.GetWindowText(window, buffer, buffer.Capacity) > 0 && !string.IsNullOrWhiteSpace(buffer.ToString()))
                    title = buffer.ToString();
            }
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId is > 0 and <= int.MaxValue)
            {
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    if (!string.IsNullOrWhiteSpace(process.ProcessName)) application = process.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Protected or exited applications must not prevent a screenshot.
                }
            }
        }
        return new CaptureContext(capturedAt, title, application, mode.ToString());
    }
}
