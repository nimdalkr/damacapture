using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DamaCapture.Services;

/// <summary>Explicit user copy actions, marked so restoring history never creates another history item.</summary>
internal static class ClipboardTransfer
{
    public static void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        Write(data);
    }

    public static void SetImage(BitmapSource image, bool keepOutOfHistory = false) => Write(ImageData(image, keepOutOfHistory));

    /// <summary>
    /// Automatic copies (a fresh capture and its later edits) ask Windows not to keep them in clipboard history
    /// or sync them, so an image from before masking does not linger there.
    /// </summary>
    internal static DataObject ImageData(BitmapSource image, bool keepOutOfHistory)
    {
        ArgumentNullException.ThrowIfNull(image);
        var data = new DataObject();
        data.SetImage(image);
        if (keepOutOfHistory)
        {
            data.SetData(ClipboardPolicy.HistoryPermissionFormat, new MemoryStream(new byte[4], writable: false), autoConvert: false);
            data.SetData(CloudPermissionFormat, new MemoryStream(new byte[4], writable: false), autoConvert: false);
        }
        return data;
    }
    internal const string CloudPermissionFormat = "CanUploadToCloudClipboard";
    /// <summary>Changes whenever anything is copied, by any application.</summary>
    public static uint Sequence => GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    public static void SetFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var values = paths.ToArray();
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("복사할 파일 경로가 없습니다.", nameof(paths));
        var files = new StringCollection();
        files.AddRange(values);
        var data = new DataObject();
        // CF_HDROP contains file references. No file is opened or duplicated by this operation.
        data.SetFileDropList(files);
        Write(data);
    }

    private static void Write(DataObject data)
    {
        using var marker = new MemoryStream([1], writable: false);
        data.SetData(ClipboardPolicy.OwnMarker, marker, autoConvert: false);
        Clipboard.SetDataObject(data, copy: true);
    }
}
