using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace DamaCapture;

/// <summary>Seconds left before a delayed capture, shown over the screen under the cursor without taking focus.</summary>
internal sealed class CountdownWindow : Window
{
    private readonly TextBlock number;
    private readonly Ring ring;
    private readonly ScaleTransform tick = new(1, 1);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public CountdownWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Focusable = false; IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.Manual;
        number = new TextBlock { FontSize = 40, FontWeight = FontWeights.SemiBold, Foreground = Ui.OnPrimary, FontFamily = new FontFamily("Segoe UI, Malgun Gothic"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = tick, RenderTransformOrigin = new Point(.5, .5) };
        // The ring drains once per second so the remaining time reads without counting.
        ring = new Ring(74) { Thickness = 3.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var layers = new Grid(); layers.Children.Add(ring); layers.Children.Add(number);
        Content = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(20), Background = Ui.Brush("#F2080809"), Child = layers };
        // Never activate, never take clicks, never show in Alt+Tab.
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x00000020 | 0x00000080);
            // No fade on hide: the capture is taken right after it disappears.
            Capture.NativeMethods.SetWindowTransitions(handle, false);
        };
    }

    public void Show(int remaining)
    {
        number.Text = remaining.ToString();
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = (screen.Left + screen.Width / 2.0) / dpi.DpiScaleX - 48;
        Top = (screen.Top + screen.Height / 2.0) / dpi.DpiScaleY - 48;
        if (!IsVisible) Show();
        ring.Drain(1000);
        Motion.Play(tick, ScaleTransform.ScaleXProperty, 1.18, 1, 260);
        Motion.Play(tick, ScaleTransform.ScaleYProperty, 1.18, 1, 260);
    }
}
