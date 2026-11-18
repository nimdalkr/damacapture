using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace DamaCapture.Capture;

public enum CaptureMode { Region, Window, FullScreen, AllScreens, FixedSize, Freehand, Element, LastRegion }

/// <summary>All public rectangles are physical desktop pixels, including negative monitor coordinates.</summary>
public sealed class CaptureService
{
    /// <summary>The area 이전 영역 repeats; the app restores it from the settings after a restart.</summary>
    public DrawingRectangle? LastRegion { get; set; }
    public DrawingRectangle? SelectedRegion { get; private set; }
    public CaptureContext? LastContext { get; private set; }
    /// <summary>Parts of the last capture, in its own pixels, where a window asked Windows to keep it out of screenshots.</summary>
    public IReadOnlyList<DrawingRectangle> LastProtected { get; private set; } = [];

    /// <param name="countdown">Called once per remaining second, then with 0 right before the capture so the caller can hide its display.</param>
    /// <param name="remember">False leaves 이전 영역 as it was, for a capture that only reads text.</param>
    public async Task<BitmapSource?> CaptureAsync(CaptureMode mode, bool includeCursor = false,
        int delaySeconds = 0, int fixedWidth = 800, int fixedHeight = 600, Func<int, Task>? countdown = null, bool remember = true)
    {
        LastContext = null;
        SelectedRegion = null;
        LastProtected = [];
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("화면 선택은 앱의 UI 스레드에서 실행해야 합니다.");
        if (delaySeconds > 0)
        {
            for (var remaining = Math.Clamp(delaySeconds, 0, 60); remaining > 0; remaining--)
            {
                if (countdown != null) await countdown(remaining);
                await Task.Delay(1000);
            }
            if (countdown != null) await countdown(0);
        }

        var desktop = VirtualBounds();
        DrawingRectangle? direct = mode switch
        {
            CaptureMode.AllScreens => desktop,
            CaptureMode.FullScreen => Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds,
            CaptureMode.LastRegion when LastRegion is { } last => DrawingRectangle.Intersect(last, desktop),
            _ => null
        };
        if (direct is { Width: > 1, Height: > 1 } region)
        {
            using var bitmap = CaptureBitmap(region, includeCursor, mode, out var captureContext, out var blocked);
            var image = ToBitmapSource(bitmap);
            SelectedRegion = region;
            LastContext = captureContext;
            LastProtected = Within(blocked, region);
            return image;
        }

        // Take the image before showing any overlay. The modal selection never appears in the result.
        using var background = CaptureBitmap(desktop, includeCursor, mode, out var contextAtCapture, out var blockedAtCapture);
        using var overlay = new SelectionOverlay(background, desktop,
            mode == CaptureMode.LastRegion ? CaptureMode.Region : mode,
            Math.Clamp(fixedWidth, 2, desktop.Width), Math.Clamp(fixedHeight, 2, desktop.Height));
        if (overlay.ShowDialog() != Forms.DialogResult.OK || overlay.Selection is not { } choice)
            return null;

        var clipped = DrawingRectangle.Intersect(choice, desktop);
        if (clipped.Width < 2 || clipped.Height < 2) return null;
        // Only the selected pixels are copied out of the desktop bitmap.
        var crop = ToBitmapSource(background, new DrawingRectangle(clipped.X - desktop.X, clipped.Y - desktop.Y, clipped.Width, clipped.Height));
        LastProtected = Within(blockedAtCapture, clipped);
        if (mode != CaptureMode.Freehand || overlay.Polygon.Count < 3)
        {
            SelectedRegion = clipped; if (remember) LastRegion = clipped;
            LastContext = contextAtCapture;
            return crop;
        }

        // A freehand selection keeps transparent pixels outside its contour.
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new System.Windows.Point(overlay.Polygon[0].X + desktop.X - clipped.X,
                overlay.Polygon[0].Y + desktop.Y - clipped.Y), true, true);
            context.PolyLineTo(overlay.Polygon.Skip(1).Select(p => new System.Windows.Point(
                p.X + desktop.X - clipped.X, p.Y + desktop.Y - clipped.Y)).ToArray(), true, false);
        }
        geometry.Freeze();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(geometry);
            dc.DrawImage(crop, new Rect(0, 0, clipped.Width, clipped.Height));
            dc.Pop();
        }
        var transparent = new RenderTargetBitmap(clipped.Width, clipped.Height, 96, 96, PixelFormats.Pbgra32);
        transparent.Render(visual);
        transparent.Freeze();
        SelectedRegion = clipped; if (remember) LastRegion = clipped;
        LastContext = contextAtCapture;
        return transparent;
    }

    /// <summary>Screen rectangles that fall inside a capture, moved into the capture's own pixels.</summary>
    private static DrawingRectangle[] Within(IReadOnlyList<DrawingRectangle> screen, DrawingRectangle capture) => screen
        .Select(area => DrawingRectangle.Intersect(area, capture)).Where(area => area.Width > 0 && area.Height > 0)
        .Select(area => new DrawingRectangle(area.X - capture.X, area.Y - capture.Y, area.Width, area.Height)).ToArray();

    /// <summary>
    /// Visible parts of windows that asked Windows to keep them out of screenshots (protected viewers, some messengers
    /// and banking apps). Those parts come out black or missing; telling the user beats leaving them to guess.
    /// Windows are given top first; anything already covered by a window above does not count.
    /// </summary>
    internal static IReadOnlyList<DrawingRectangle> VisibleProtectedAreas(IEnumerable<(DrawingRectangle Bounds, bool Protected)> topFirst, DrawingRectangle capture)
    {
        var areas = new List<DrawingRectangle>();
        using var covered = new Region(DrawingRectangle.Empty);
        using var identity = new System.Drawing.Drawing2D.Matrix();
        foreach (var (bounds, isProtected) in topFirst)
        {
            var inside = DrawingRectangle.Intersect(bounds, capture);
            if (inside.Width <= 0 || inside.Height <= 0) continue;
            if (isProtected)
            {
                using var visible = new Region(inside);
                visible.Exclude(covered);
                var scans = visible.GetRegionScans(identity);
                if (scans.Length > 0)
                {
                    var box = scans.Select(scan => DrawingRectangle.Round(scan)).Aggregate(DrawingRectangle.Union);
                    if (box.Width >= 8 && box.Height >= 8) areas.Add(box);
                }
            }
            covered.Union(inside);
        }
        return areas;
    }

    private static IReadOnlyList<DrawingRectangle> ProtectedAreas(DrawingRectangle capture)
    {
        var windows = new List<(DrawingRectangle, bool)>();
        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.GetCloaked(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if (NativeMethods.GetFrameBounds(hwnd, 9, out var frame, Marshal.SizeOf<NativeMethods.RECT>()) != 0 && !NativeMethods.GetWindowRect(hwnd, out frame)) return true;
                var bounds = frame.Rectangle;
                if (bounds.Width < 2 || bounds.Height < 2) return true;
                windows.Add((bounds, NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity) && affinity != 0));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return []; }
        return windows.Any(window => window.Item2) ? VisibleProtectedAreas(windows, capture) : [];
    }

    public static DrawingRectangle VirtualBounds() => Forms.Screen.AllScreens
        .Select(screen => screen.Bounds).Aggregate(DrawingRectangle.Union);

    public static BitmapSource CaptureRectangle(DrawingRectangle bounds, bool includeCursor = false)
    {
        bounds = DrawingRectangle.Intersect(bounds, VirtualBounds());
        if (bounds.Width < 1 || bounds.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(bounds), "캡처 영역이 화면 밖에 있습니다.");
        using var bitmap = CaptureBitmap(bounds, includeCursor, null, out _);
        return ToBitmapSource(bitmap);
    }

    /// <summary>Send a wheel message only to the window under the selected area.</summary>
    public static bool ScrollAt(DrawingRectangle bounds, int wheelDelta)
    {
        var point = new DrawingPoint(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var target = NativeMethods.WindowFromPoint(new NativeMethods.POINT(point.X, point.Y));
        if (target == IntPtr.Zero) return false;
        var coordinate = new IntPtr(unchecked((point.Y & 0xffff) << 16 | (point.X & 0xffff)));
        var wheel = new IntPtr(unchecked((wheelDelta & 0xffff) << 16));
        return NativeMethods.SendMessageTimeout(target, 0x020A, wheel, coordinate,
            0x0002, 200, out _) != IntPtr.Zero;
    }

    private static Bitmap CaptureBitmap(DrawingRectangle bounds, bool includeCursor, CaptureMode? mode, out CaptureContext? context) =>
        CaptureBitmap(bounds, includeCursor, mode, out context, out _, readProtected: false);

    private static Bitmap CaptureBitmap(DrawingRectangle bounds, bool includeCursor, CaptureMode? mode, out CaptureContext? context, out IReadOnlyList<DrawingRectangle> blocked, bool readProtected = true)
    {
        // Hide/Show operations are asynchronous to the compositor. Flush before freezing the desktop.
        NativeMethods.DwmFlush();
        context = null;
        blocked = [];
        var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                // Collect after the optional delay and before the selection overlay changes foreground focus.
                if (mode is { } captureMode) context = CaptureContext.ReadForeground(captureMode);
                graphics.CopyFromScreen(bounds.Location, DrawingPoint.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                // Read right after the pixels, so the hotkey-to-freeze time does not grow.
                if (readProtected) blocked = ProtectedAreas(bounds);
                if (includeCursor) DrawCursor(graphics, bounds);
            }
            // GDI screen capture does not promise an alpha channel. Make the frozen desktop opaque.
            var data = bitmap.LockBits(new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bitmap.Width * 4];
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var address = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(address, row, 0, row.Length);
                    for (var x = 3; x < row.Length; x += 4) row[x] = 255;
                    Marshal.Copy(row, 0, address, row.Length);
                }
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap, DrawingRectangle? region = null)
    {
        var area = region ?? new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // The locked area's rows are copied straight into the frozen image; no managed copy in between.
            // The size stops at the last row's own pixels so a region at the bitmap's edge never reads past it.
            var source = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgra32,
                null, data.Scan0, checked(data.Stride * (area.Height - 1) + area.Width * 4), data.Stride);
            source.Freeze();
            return source;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static void DrawCursor(Graphics graphics, DrawingRectangle bounds)
    {
        var cursor = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref cursor) || cursor.flags != 1 ||
            !NativeMethods.GetIconInfo(cursor.hCursor, out var icon)) return;
        try
        {
            var dc = graphics.GetHdc();
            try
            {
                NativeMethods.DrawIconEx(dc, cursor.ptScreenPos.X - bounds.Left - (int)icon.xHotspot,
                    cursor.ptScreenPos.Y - bounds.Top - (int)icon.yHotspot, cursor.hCursor, 0, 0, 0, IntPtr.Zero, 3);
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(icon.hbmColor);
        }
    }
}
