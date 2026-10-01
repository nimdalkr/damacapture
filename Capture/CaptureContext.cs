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
        var window = ForegroundForCapture();
        var capturedAt = DateTimeOffset.Now;
        return ReadWindow(window, capturedAt, mode);
    }

    /// <summary>
    /// The foreground window, unless it is no longer visible (this app hides its own window for the capture
    /// without handing activation on); then the next visible window in Z-order, which is the one Windows
    /// would have activated.
    /// </summary>
    internal static IntPtr ForegroundForCapture()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero || NativeMethods.IsWindowVisible(window)) return window;
        return NextVisible(window);
    }

    internal static IntPtr NextVisible(IntPtr from)
    {
        var current = from;
        for (var i = 0; i < 1024 && current != IntPtr.Zero; i++)
        {
            current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
            if (current == IntPtr.Zero) break;
            if (!NativeMethods.IsWindowVisible(current) || NativeMethods.GetWindowTextLength(current) == 0) continue;
            if (NativeMethods.GetCloaked(current, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            return current;
        }
        return IntPtr.Zero;
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
