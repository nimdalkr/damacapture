using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DamaCapture.Capture;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO { [MarshalAs(UnmanagedType.Bool)] public bool fIcon; public uint xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] internal static extern bool GetCursorInfo(ref CURSORINFO cursor);
    [DllImport("user32.dll")] internal static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] internal static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maximumLength);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern IntPtr ChildWindowFromPointEx(IntPtr hwnd, POINT point, uint flags);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    internal const uint GW_HWNDNEXT = 2;
    internal const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_HIDEWINDOW = 0x0080;
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] internal static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int GetFrameBounds(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int GetCloaked(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    /// <summary>Turns the compositor's open/close animation on or off for one window.</summary>
    internal static void SetWindowTransitions(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero) return;
        var disabled = enabled ? 0 : 1;
        DwmSetWindowAttribute(hwnd, 3, ref disabled, sizeof(int)); // DWMWA_TRANSITIONS_FORCEDISABLED
    }
    /// <summary>Returns once a window hidden just before is no longer part of the composed desktop.</summary>
    internal static void WaitForComposition() { DwmFlush(); DwmFlush(); }

    internal static Rectangle? FindWindowBounds(Point location, IntPtr excluded, bool findChild)
    {
        var found = IntPtr.Zero;
        var result = Rectangle.Empty;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == excluded || !IsWindowVisible(hwnd)) return true;
            if (GetCloaked(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (GetFrameBounds(hwnd, 9, out var frame, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(hwnd, out frame))
                return true;
            var bounds = frame.Rectangle;
            if (bounds.Width < 2 || bounds.Height < 2 || !bounds.Contains(location)) return true;
            found = hwnd;
            result = bounds;
            return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return null;
        if (findChild)
        {
            for (var depth = 0; depth < 24; depth++)
            {
                var client = new POINT(location.X, location.Y);
                if (!ScreenToClient(found, ref client)) break;
                var child = ChildWindowFromPointEx(found, client, 1 | 2 | 4);
                if (child == IntPtr.Zero || child == found || child == excluded) break;
                if (!GetWindowRect(child, out var childBounds)) break;
                var clipped = Rectangle.Intersect(result, childBounds.Rectangle);
                if (clipped.Width < 2 || clipped.Height < 2 || !clipped.Contains(location)) break;
                result = clipped;
                found = child;
            }
        }
        return result;
    }
}
