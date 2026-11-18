using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture;

/// <summary>
/// Dragging a result into a chat, a document or a folder hands over what a copy would: the composed image, never the
/// one from before masking. Apps that take files get a PNG from this app's temporary folder; the rest get the pixels.
/// </summary>
internal sealed partial class MainWindow
{
    private static string DragFolder => Path.Combine(Path.GetTempPath(), "DamaCapture", "drag");
    private bool draggingOut;

    // A drop target may still be reading a recent file (an upload can take a while), so only files older than an hour
    // are removed: at start and before every new drag, which keeps a long-running tray app from piling them up.
    private static void CleanDragFolder()
    {
        try
        {
            if (!Directory.Exists(DragFolder)) return;
            foreach (var file in Directory.EnumerateFiles(DragFolder, "*.png"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1)) File.Delete(file);
        }
        catch (Exception) { }
    }

    private static DataObject ImageDragData(BitmapSource pixels, DateTime when)
    {
        CleanDragFolder();
        Directory.CreateDirectory(DragFolder);
        var name = "담아_" + when.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(DragFolder, name + ".png");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(DragFolder, $"{name}_{n}.png");
        ImageFiles.Save(pixels, path);
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        data.SetImage(pixels);
        return data;
    }

    /// <summary>A press that moves starts a drag; a press that does not stays an ordinary click.</summary>
    private void MakeDragSource(UIElement element, Func<DataObject?> data)
    {
        Point? pressed = null;
        element.PreviewMouseLeftButtonDown += (_, e) => pressed = e.GetPosition(element);
        element.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
        element.PreviewMouseMove += (_, e) =>
        {
            if (pressed is not Point from || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(element);
            if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = null;
            DataObject? payload = null;
            try { payload = data(); }
            catch (Exception ex) { Notify("끌어 놓을 내용을 준비하지 못했습니다. " + ex.Message); }
            if (payload == null) return;
            // The pressed button holds the mouse; the drag needs it.
            if (Mouse.Captured != null) Mouse.Capture(null);
            e.Handled = true;
            // Dropped back on this window it is not a file to open.
            draggingOut = true;
            try { DragDrop.DoDragDrop(element, payload, DragDropEffects.Copy); }
            finally { draggingOut = false; }
        };
    }

    private DataObject? CurrentDragData()
    {
        if (document == null) return null;
        surface.CompletePendingEdit();
        return ImageDragData(document.Render(), DateTime.Now);
    }

    private DataObject? HistoryDragData(HistoryEntry entry)
    {
        if (entry.Kind == HistoryKind.ClipboardText)
        {
            var text = new DataObject(); text.SetText(entry.Text, TextDataFormat.UnicodeText); return text;
        }
        if (entry.Kind == HistoryKind.ClipboardFiles)
        {
            var existing = entry.FilePaths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
            if (existing.Length == 0) return null;
            var list = new StringCollection(); list.AddRange(existing);
            var files = new DataObject(); files.SetFileDropList(list); return files;
        }
        var pixels = HistoryPixels(entry);
        return pixels == null ? null : ImageDragData(pixels, CaptureTime(entry));
    }
}
