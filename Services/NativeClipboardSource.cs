using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace DamaCapture.Services;

/// <summary>Reads only Unicode text, HDROP paths, PNG and DIB. Never deserializes managed clipboard objects.</summary>
internal sealed class NativeClipboardSource : IClipboardSource
{
    private const uint UnicodeText = 13, FileDrop = 15, Dib = 8, DibV5 = 17, Bitmap = 2;
    private readonly IntPtr window;
    private readonly uint ownMarker, excluded, permission, png;

    internal NativeClipboardSource(IntPtr window)
    {
        this.window = window;
        ownMarker = Native.RegisterClipboardFormat(ClipboardPolicy.OwnMarker);
        excluded = Native.RegisterClipboardFormat(ClipboardPolicy.ExclusionFormat);
        permission = Native.RegisterClipboardFormat(ClipboardPolicy.HistoryPermissionFormat);
        png = Native.RegisterClipboardFormat("PNG");
        if (ownMarker == 0 || excluded == 0 || permission == 0 || png == 0)
            throw new InvalidOperationException("클립보드 형식을 초기화하지 못했습니다.");
    }

    public uint Sequence => Native.GetClipboardSequenceNumber();
    public bool StartListening() => Native.AddClipboardFormatListener(window);
    public void StopListening() => Native.RemoveClipboardFormatListener(window);

    public ClipboardReadResult Read(uint expectedSequence)
    {
        if (Sequence != expectedSequence) return new(ClipboardReadStatus.Changed);
        if (!Native.OpenClipboard(window)) return new(ClipboardReadStatus.Busy);
        try
        {
            if (Sequence != expectedSequence) return new(ClipboardReadStatus.Changed);
            var owner = Native.GetClipboardOwner();
            Native.GetWindowThreadProcessId(owner, out var ownerProcess);
            var own = ownerProcess != 0 && ownerProcess == (uint)Environment.ProcessId;
            var markerPresent = Native.IsClipboardFormatAvailable(ownMarker);
            var excludedPresent = Native.IsClipboardFormatAvailable(excluded);
            var permissionPresent = Native.IsClipboardFormatAvailable(permission);
            // Exclusion and app marker need only presence checks. Do not retrieve even their data.
            if (own || markerPresent || excludedPresent) return new(ClipboardReadStatus.Ignore);
            uint? permissionValue = null;
            if (permissionPresent)
            {
                permissionValue = ReadDword(permission);
            }
            if (ClipboardPolicy.ShouldIgnore(own, markerPresent, excludedPresent, permissionPresent, permissionValue))
                return new(ClipboardReadStatus.Ignore);

            ClipboardPayload? payload = null;
            // A single copy may advertise many formats. Archive one semantic item, in this priority.
            if (Native.IsClipboardFormatAvailable(FileDrop))
            {
                var paths = ReadFiles();
                if (paths is not null) payload = new(HistoryKind.ClipboardFiles, "", paths, null);
            }
            else if (Native.IsClipboardFormatAvailable(png) || Native.IsClipboardFormatAvailable(DibV5) ||
                Native.IsClipboardFormatAvailable(Dib) || Native.IsClipboardFormatAvailable(Bitmap))
            {
                var image = ReadImage();
                if (image is not null) payload = new(HistoryKind.ClipboardImage, "", [], image);
            }
            else if (Native.IsClipboardFormatAvailable(UnicodeText))
            {
                var text = ReadText();
                if (text is not null) payload = new(HistoryKind.ClipboardText, text, [], null);
            }
            if (Sequence != expectedSequence) return new(ClipboardReadStatus.Changed);
            return payload is not null ? new(ClipboardReadStatus.Ready, payload) : new(ClipboardReadStatus.Ignore);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or FormatException or
            ExternalException or OverflowException or OutOfMemoryException)
        {
            // Malformed, unsupported or oversized data must not interfere with the source application.
            return new(ClipboardReadStatus.Ignore);
        }
        finally { Native.CloseClipboard(); }
    }

    private static string? ReadText()
    {
        var bytes = ReadGlobal(UnicodeText, (ClipboardPolicy.MaxTextCharacters + 1) * 2);
        if (bytes is null || (bytes.Length & 1) != 0) return null;
        var text = Encoding.Unicode.GetString(bytes);
        var terminator = text.IndexOf('\0');
        if (terminator >= 0) text = text[..terminator];
        return text.Length <= ClipboardPolicy.MaxTextCharacters ? text : null;
    }

    private static string[]? ReadFiles()
    {
        var handle = Native.GetClipboardData(FileDrop);
        if (handle == IntPtr.Zero) return null;
        var count = Native.DragQueryFile(handle, uint.MaxValue, null, 0);
        if (count == 0 || count > ClipboardPolicy.MaxFiles) return null;
        var result = new string[count];
        var total = 0;
        for (uint i = 0; i < count; i++)
        {
            var length = Native.DragQueryFile(handle, i, null, 0);
            if (length == 0 || length > 32767 || (total += (int)length) > ClipboardPolicy.MaxTextCharacters) return null;
            var path = new StringBuilder((int)length + 1);
            if (Native.DragQueryFile(handle, i, path, (uint)path.Capacity) != length) return null;
            result[i] = path.ToString();
        }
        return result;
    }

    private BitmapSource? ReadImage()
    {
        if (Native.IsClipboardFormatAvailable(png))
        {
            var encoded = ReadGlobal(png, 64 * 1024 * 1024);
            if (encoded is not null && ClipboardImageDecoder.TryPng(encoded) is { } image) return image;
        }
        if (Native.IsClipboardFormatAvailable(DibV5))
        {
            var v5 = ReadGlobal(DibV5, checked((int)ClipboardPolicy.MaxImagePixels * 4 + 4096));
            if (v5 is not null && ClipboardImageDecoder.TryDib(v5) is { } image) return image;
        }
        // Windows can synthesize CF_DIB from CF_BITMAP. It also provides a fallback for an
        // application's malformed DIBV5/PNG representation without deserializing custom objects.
        if (Native.IsClipboardFormatAvailable(Dib) || Native.IsClipboardFormatAvailable(Bitmap))
        {
            var dib = ReadGlobal(Dib, checked((int)ClipboardPolicy.MaxImagePixels * 4 + 4096));
            if (dib is not null) return ClipboardImageDecoder.TryDib(dib);
        }
        return null;
    }

    private static byte[]? ReadGlobal(uint format, int maximumBytes)
    {
        var handle = Native.GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = Native.GlobalSize(handle).ToUInt64();
        if (size == 0 || size > (ulong)maximumBytes) return null;
        var pointer = Native.GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Native.GlobalUnlock(handle); }
    }

    private static uint? ReadDword(uint format)
    {
        var handle = Native.GetClipboardData(format);
        if (handle == IntPtr.Zero || Native.GlobalSize(handle).ToUInt64() < 4) return null;
        var pointer = Native.GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try { return unchecked((uint)Marshal.ReadInt32(pointer)); }
        finally { Native.GlobalUnlock(handle); }
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool CloseClipboard();
        [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] internal static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint RegisterClipboardFormat(string format);
        [DllImport("kernel32.dll")] internal static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint size);
    }
}

/// <summary>Bounded, explicit image codecs; dimensions are checked before asking WIC to decode.</summary>
internal static class ClipboardImageDecoder
{
    internal static bool ValidDimensions(long width, long height) => width > 0 && height > 0 &&
        width <= int.MaxValue && height <= int.MaxValue && width * height <= ClipboardPolicy.MaxImagePixels;

    internal static BitmapSource? TryPng(byte[] data) => SafeDecode(() => DecodePng(data));

    private static BitmapSource? DecodePng(byte[] data)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)) != 13 ||
            !data.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
        if (!ValidDimensions(width, height)) return null;
        using var stream = new MemoryStream(data, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return FreezeFrame(decoder);
    }

    internal static BitmapSource? TryDib(byte[] data) => SafeDecode(() => DecodeDib(data));

    private static BitmapSource? DecodeDib(byte[] data)
    {
        if (data.Length < 40) return null;
        var header = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (header is not (40 or 52 or 56 or 108 or 124) || header > data.Length) return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        var height = Math.Abs((long)signedHeight);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var usedColors = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32));
        if (!ValidDimensions(width, height) || planes != 1 || bits is not (1 or 4 or 8 or 16 or 24 or 32) ||
            compression is not (0 or 3 or 6) || (compression != 0 && bits is not (16 or 32)) || usedColors > 256) return null;
        var palette = bits <= 8 && usedColors == 0 ? 1u << bits : usedColors;
        var masks = header == 40 ? compression == 3 ? 12 : compression == 6 ? 16 : 0 : 0;
        var offset = (long)header + masks + palette * 4;
        var stride = ((long)width * bits + 31) / 32 * 4;
        if (offset + stride * height > data.Length) return null;
        // DIB omits the 14-byte BMP file header. Add only this structural header, not arbitrary codecs.
        using var stream = new MemoryStream(data.Length + 14);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0x4D42); writer.Write(data.Length + 14); writer.Write(0); writer.Write((int)offset + 14);
            writer.Write(data);
        }
        stream.Position = 0;
        var decoder = new BmpBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return FreezeFrame(decoder);
    }

    private static BitmapSource? FreezeFrame(BitmapDecoder decoder)
    {
        if (decoder.Frames.Count == 0) return null;
        var frame = decoder.Frames[0];
        if (!ValidDimensions(frame.PixelWidth, frame.PixelHeight)) return null;
        var image = frame.Clone(); image.Freeze();
        return image;
    }

    private static BitmapSource? SafeDecode(Func<BitmapSource?> decode)
    {
        try { return decode(); }
        // WIC reports a damaged image body as FileFormatException, which derives from FormatException.
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or FormatException or
            ExternalException or OverflowException or OutOfMemoryException) { return null; }
    }
}
