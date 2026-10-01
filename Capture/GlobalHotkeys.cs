using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;

namespace DamaCapture.Capture;

public sealed class GlobalHotkeys : IDisposable
{
    public const uint Alt = 0x0001, Control = 0x0002, Shift = 0x0004, Windows = 0x0008, NoRepeat = 0x4000;
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly HashSet<int> _registered = [];
    private bool _disposed;
    public event Action<int>? Pressed;

    public GlobalHotkeys(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("앱 창을 초기화할 수 없습니다.");
        _source.AddHook(WindowMessage);
    }

    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered.Remove(id)) NativeMethods.UnregisterHotKey(_handle, id);
        if (!NativeMethods.RegisterHotKey(_handle, id, modifiers | NoRepeat, virtualKey)) return false;
        _registered.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id)) NativeMethods.UnregisterHotKey(_handle, id);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && _registered.Contains(wParam.ToInt32()))
        {
            handled = true;
            Pressed?.Invoke(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _registered) NativeMethods.UnregisterHotKey(_handle, id);
        _registered.Clear();
        if (!_source.IsDisposed) _source.RemoveHook(WindowMessage);
        GC.SuppressFinalize(this);
    }
}
