using System;
using System.Windows.Media.Imaging;
using DamaCapture.Imaging;

namespace DamaCapture.Services;

/// <summary>
/// Keeps the clipboard on the latest composed render of one document, so masking after a capture also
/// changes what gets pasted. It lets go as soon as anything else is copied or another image is opened.
/// </summary>
internal sealed class ClipboardFollower(Func<uint> sequence, Action<BitmapSource> write)
{
    private ImageDocument? target;
    private uint owned;

    public bool IsFollowing(ImageDocument? document) => target != null && ReferenceEquals(target, document);

    /// <summary>Copies the document now and follows its edits. Throws if the clipboard cannot be written.</summary>
    public void Start(ImageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        target = null;
        write(document.Render());
        target = document; owned = sequence();
    }

    /// <summary>Rewrites the clipboard for the current document; false when following has ended.</summary>
    public bool Update(ImageDocument? document)
    {
        if (target == null) return false;
        // Another image is open, or someone copied something else: the clipboard is no longer ours to change.
        if (!ReferenceEquals(target, document) || sequence() != owned) { target = null; return false; }
        try { write(target.Render()); owned = sequence(); return true; }
        catch { target = null; throw; }
    }

    /// <summary>After an explicit copy of the followed document, keep following from that copy.</summary>
    public void Adopt(ImageDocument? document) { if (IsFollowing(document)) owned = sequence(); }

    public void Stop() => target = null;
}
