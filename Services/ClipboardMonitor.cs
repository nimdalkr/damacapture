using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DamaCapture.Capture;

namespace DamaCapture.Services;

internal sealed record ClipboardCapture(HistoryKind Kind, DateTimeOffset CopiedAt, string WindowTitle,
    string ApplicationName, string Text, string[] FilePaths, BitmapSource? Image);
internal sealed record ClipboardPayload(HistoryKind Kind, string Text, string[] FilePaths, BitmapSource? Image);
internal sealed record ClipboardOrigin(DateTimeOffset CopiedAt, string WindowTitle, string ApplicationName);
internal enum ClipboardReadStatus { Ready, Ignore, Busy, Changed }
internal sealed record ClipboardReadResult(ClipboardReadStatus Status, ClipboardPayload? Payload = null);

internal interface IClipboardSource
{
    uint Sequence { get; }
    bool StartListening();
    void StopListening();
    ClipboardReadResult Read(uint expectedSequence);
}

internal interface IClipboardRetryScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>Opt-in, read-only observer. Clipboard access and delivery run on the owning UI dispatcher.</summary>
internal sealed class ClipboardMonitor : IDisposable
{
    private const int MaxAttempts = 5;
    private readonly IClipboardSource source;
    private readonly IClipboardRetryScheduler scheduler;
    private readonly Action<ClipboardCapture> callback;
    private readonly Func<ClipboardOrigin> origin;
    private readonly Action verifyAccess;
    private readonly HwndSource? windowSource;
    private IDisposable? retry;
    private bool enabled, disposed;
    private uint observedSequence;
    private long generation;

    public ClipboardMonitor(Window window, Action<ClipboardCapture> callback)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(callback);
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        windowSource = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("클립보드 감시 창을 초기화하지 못했습니다.");
        source = new NativeClipboardSource(handle);
        scheduler = new DispatcherClipboardScheduler(window.Dispatcher);
        this.callback = callback;
        verifyAccess = window.Dispatcher.VerifyAccess;
        origin = () =>
        {
            var context = CaptureContext.ReadForeground(CaptureMode.Region);
            return new ClipboardOrigin(context.CapturedAt, context.WindowTitle, context.ApplicationName);
        };
        windowSource.AddHook(WindowMessage);
    }

    // Native clipboard access is replaceable so tests never inspect or alter the user's clipboard.
    internal ClipboardMonitor(IClipboardSource source, IClipboardRetryScheduler scheduler,
        Action<ClipboardCapture> callback, Func<ClipboardOrigin> origin)
    {
        this.source = source; this.scheduler = scheduler; this.callback = callback; this.origin = origin;
        verifyAccess = () => { };
    }

    public void SetEnabled(bool value)
    {
        verifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (enabled == value) return;
        generation++;
        retry?.Dispose(); retry = null;
        if (value)
        {
            if (!source.StartListening()) throw new Win32Exception(Marshal.GetLastWin32Error(), "클립보드 변경 감시를 시작하지 못했습니다.");
            // Enabling records only a sequence baseline. Existing clipboard contents are never imported.
            observedSequence = source.Sequence;
            enabled = true;
        }
        else
        {
            enabled = false;
            source.StopListening();
        }
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x031D) OnClipboardChanged();
        return IntPtr.Zero;
    }

    internal void OnClipboardChanged()
    {
        verifyAccess();
        if (!enabled || disposed) return;
        var sequence = source.Sequence;
        if (sequence == observedSequence) return;
        observedSequence = sequence;
        var current = ++generation;
        retry?.Dispose(); retry = null;
        Read(current, sequence, 0, origin());
    }

    private void Read(long expectedGeneration, uint sequence, int attempt, ClipboardOrigin context)
    {
        if (!enabled || disposed || generation != expectedGeneration) return;
        retry = null;
        if (source.Sequence != sequence) { RetryLatest(attempt); return; }
        var result = source.Read(sequence);
        // Delayed rendering or a newer copy may have changed the sequence during the read.
        if (!enabled || disposed || generation != expectedGeneration) return;
        if (source.Sequence != sequence) { RetryLatest(attempt); return; }
        if (result.Status == ClipboardReadStatus.Busy && attempt + 1 < MaxAttempts)
        {
            retry = scheduler.Schedule(TimeSpan.FromMilliseconds(60 * (1 << attempt)),
                () => Read(expectedGeneration, sequence, attempt + 1, context));
            return;
        }
        if (result.Status != ClipboardReadStatus.Ready || result.Payload is not { } payload || !ClipboardPolicy.IsValid(payload)) return;
        callback(new ClipboardCapture(payload.Kind, context.CopiedAt, context.WindowTitle, context.ApplicationName,
            payload.Text, (string[])payload.FilePaths.Clone(), payload.Image));
    }

    private void RetryLatest(int attempt)
    {
        if (attempt + 1 >= MaxAttempts) return;
        // Delayed rendering may advance the sequence without another usable notification. Acquire
        // the latest generation once, still bounded by the same retry budget and cancellation rules.
        var latest = source.Sequence;
        observedSequence = latest;
        var current = ++generation;
        var context = origin();
        retry = scheduler.Schedule(TimeSpan.FromMilliseconds(60 * (1 << attempt)),
            () => Read(current, latest, attempt + 1, context));
    }

    public void Dispose()
    {
        verifyAccess();
        if (disposed) return;
        if (enabled) SetEnabled(false);
        disposed = true; generation++;
        retry?.Dispose(); retry = null;
        if (windowSource is { IsDisposed: false }) windowSource.RemoveHook(WindowMessage);
    }
}

internal static class ClipboardPolicy
{
    internal const int MaxTextCharacters = 524_288, MaxFiles = 1_000;
    internal const long MaxImagePixels = 64_000_000;
    internal const string OwnMarker = "DamaCapture.ClipboardOrigin.v1";
    internal const string ExclusionFormat = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string HistoryPermissionFormat = "CanIncludeInClipboardHistory";

    internal static bool ShouldIgnore(bool ownedByProcess, bool ownMarker, bool exclusion,
        bool permissionPresent, uint? permission) => ownedByProcess || ownMarker || exclusion ||
        (permissionPresent && permission != 1);

    internal static bool IsValid(ClipboardPayload payload) => payload.Kind switch
    {
        HistoryKind.ClipboardText => payload.Text.Length is > 0 and <= MaxTextCharacters,
        HistoryKind.ClipboardFiles => ValidFiles(payload.FilePaths),
        HistoryKind.ClipboardImage => payload.Image is { PixelWidth: > 0, PixelHeight: > 0, IsFrozen: true } image &&
            (long)image.PixelWidth * image.PixelHeight <= MaxImagePixels,
        _ => false
    };

    private static bool ValidFiles(string[] paths)
    {
        if (paths.Length is 0 or > MaxFiles) return false;
        var total = 0;
        foreach (var path in paths)
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || (total += path.Length) > MaxTextCharacters) return false;
        return true;
    }
}

internal sealed class DispatcherClipboardScheduler(Dispatcher dispatcher) : IClipboardRetryScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action callback) => new Pending(dispatcher, delay, callback);
    private sealed class Pending : IDisposable
    {
        private readonly DispatcherTimer timer;
        private readonly Action callback;
        public Pending(Dispatcher dispatcher, TimeSpan delay, Action callback)
        {
            this.callback = callback;
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
            timer.Tick += Tick;
            timer.Start();
        }
        private void Tick(object? sender, EventArgs e) { Dispose(); callback(); }
        public void Dispose() { timer.Stop(); timer.Tick -= Tick; }
    }
}
