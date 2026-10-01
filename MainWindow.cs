using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Capture;
using DamaCapture.Imaging;
using DamaCapture.Services;
using Forms = System.Windows.Forms;
using CaptureMode = DamaCapture.Capture.CaptureMode;

namespace DamaCapture;

internal sealed partial class MainWindow : Window
{
    private readonly SettingsStore settingsStore;
    private readonly AppSettings settings;
    private readonly HistoryStore history;
    private readonly CaptureService capture = new();
    private readonly ContentControl content = new() { ClipToBounds = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock status = Ui.Label("준비", 12, Ui.Muted);
    private TextBlock? preservationLabel;

    private readonly EditorSurface surface = new();

    private FrameworkElement? editorPage, editorToolbar, editorProperties;
    private StackPanel? toolProperties, maskProperties, strokeProperties, textProperties;
    private TextBlock? toolTitle, toolHint;
    private ImageDocument? document;
    private ScrollViewer? canvasScroll;
    private ListBox? operations;
    private Slider? strengthSlider;
    private TextBlock? strengthText;
    private TextBlock? imageInfo;
    private Button? undoButton, redoButton;
    private string currentPage = "home";
    private bool updating, dirty, capturing, exiting;
    private bool fitView = true;
    private double zoom = 1;
    private GlobalHotkeys? hotkeys;
    private Forms.NotifyIcon? tray;
    private int fixedWidth = 800, fixedHeight = 600;
    private readonly bool demo;
    private readonly List<Window> pinWindows = new();

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public MainWindow(bool demo, bool clipboardDemo = false)
    {
        // A GPU device costs this resident tray app 90–170 MB of driver memory that is never returned, even
        // while hidden. The editor redraws only what changes, so CPU rendering keeps it light without lag.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        this.demo = demo;
        var previewDirectory = demo ? Path.Combine(Path.GetTempPath(), "DamaCapture-preview-" + Guid.NewGuid().ToString("N")) : null;
        settingsStore = new SettingsStore(previewDirectory);
        Background = Ui.Background; Foreground = Ui.Text; FontFamily = new FontFamily("Segoe UI, Malgun Gothic"); FontSize = 13;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/dama.ico"));
        settings = settingsStore.Load();
        if (settings.HistoryVersion < 1)
        {
            settings.KeepHistory = true; settings.KeepCaptureImages = true; settings.HistoryVersion = 1;
            if (!demo) { try { settingsStore.Save(settings); } catch (Exception) { } }
        }
        Motion.ReduceMotion = settings.ReduceMotion;
        history = new HistoryStore(settings.KeepHistory, directory: previewDirectory);
        clipboardHistory = new HistoryStore(true, Path.Combine(previewDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamaCapture"), "clipboard"));
        clipboardWriter = new HistorySnapshotWriter(clipboardHistory);
        clipboardWriter.Completed += (id, _) => Dispatcher.BeginInvoke(() => HistoryWriteCompleted(id));
        historyWriter = new HistorySnapshotWriter(history);
        historyWriter.Completed += (id, _) => Dispatcher.BeginInvoke(() => HistoryWriteCompleted(id));
        historySaveTimer.Tick += (_, _) => { historySaveTimer.Stop(); PreserveHistoryImage(); };
        clipboardSyncTimer.Tick += (_, _) => SyncClipboard();
        Deactivated += (_, _) => FlushClipboardSync();
        idleTimer.Tick += (_, _) => ReleaseIdleMemory();
        IsVisibleChanged += (_, _) => { idleTimer.Stop(); if (!IsVisible && !demo) idleTimer.Start(); };
        Title = "담아 캡처"; Width = 576; Height = 178; MinWidth = 576; MinHeight = 178; ResizeMode = ResizeMode.CanMinimize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Shell();
        surface.Changed += () => { dirty = true; RefreshEditor(); };
        surface.SelectionChanged += RefreshSelection;
        surface.RequestBringIntoView += (_, e) => e.Handled = true;
        surface.ExtractRequested += ExtractTextFrom;
        surface.CropRequested += bounds => { Try(() => { document?.Crop(bounds); surface.SelectedId = null; dirty = true; RefreshEditor(); Fit(); Toast("잘랐습니다", "되돌리기", Undo); }); };
        Loaded += (_, _) =>
        {
            var dark = 0; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);
            if (!demo) { SetupTray(); RegisterHotkeys(); ApplyClipboardSetting(); }
            ScheduleHistoryTrim(announce: true);
            if (demo) { if (clipboardDemo) AddClipboardDemoEntries(); SetImage(ImageFactory.Sample()); RecordCaptureHistory(new CaptureContext(DateTimeOffset.Now, "디자인 검토 · 예시 창", "예시 앱", "Region")); ShowPage("editor", true); Notify("예시 이미지"); }
        };
        Closing += OnClosing;
        if (Application.Current != null) Application.Current.SessionEnding += OnSessionEnding;
        Closed += (_, _) => { FlushClipboardSync(); if (Application.Current != null) Application.Current.SessionEnding -= OnSessionEnding; historySearchTimer.Stop(); toastTimer.Stop(); countdownWindow?.Close(); clipboardMonitor?.Dispose(); hotkeys?.Dispose(); tray?.Dispose(); foreach (var window in pinWindows.ToArray()) window.Close(); };
        PreviewKeyDown += KeyDownHandler;
        SizeChanged += (_, _) => { if (historyExpanded && detailsOpen && detailsColumn != null) detailsColumn.Width = new GridLength(HistoryPanelWidth); UpdateCompactLabels(); };
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenFile(files[0]); };
        ShowPage("home");
    }
    private void SelectTool(string tool)
    {
        surface.CompletePendingEdit();
        if (surface.Tool == tool) return;
        surface.Tool = tool; surface.Cursor = tool == "Select" ? Cursors.Arrow : Cursors.Cross;
        RefreshToolButtons();
        UpdateToolProperties(true);
        Notify(toolHint?.Text ?? "");
    }
    private void UpdateStrengthLabel()
    {
        var blur = surface.Tool == "Blur" || (surface.Tool == "Select" && document?.Operations.FirstOrDefault(x => x.Id == surface.SelectedId)?.Kind == EditKind.Blur);
        if (strengthText != null) strengthText.Text = blur ? "흐림 크기" : "모자이크 크기";
        if (strengthValue != null) strengthValue.Text = surface.Strength + " px";
    }
    private void UpdateToolProperties(bool animate)
    {
        if (toolProperties == null || toolTitle == null || toolHint == null) return;
        var tool = surface.Tool;
        toolTitle.Text = ToolName(tool);
        toolHint.Text = tool switch
        {
            "Mosaic" or "Blur" or "Solid" => "가릴 영역을 드래그하세요.",
            "Select" => "편집을 선택해 이동하거나 크기를 조절하세요.",
            "Crop" => "남길 영역을 드래그하세요.",
            "Extract" => "추출할 영역을 드래그하세요 (더블클릭: 전체)",
            "Text" => "내용을 입력한 뒤 놓을 곳을 클릭하세요.",
            "Number" => "놓을 곳을 클릭하세요.",
            _ => "이미지 위에서 드래그하세요."
        };
        var selectedKind = document?.Operations.FirstOrDefault(x => x.Id == surface.SelectedId)?.Kind;
        maskProperties!.Visibility = tool is "Mosaic" or "Blur" || (tool == "Select" && selectedKind is EditKind.Mosaic or EditKind.Blur) ? Visibility.Visible : Visibility.Collapsed;
        strokeProperties!.Visibility = tool is "Pen" or "Arrow" or "Rectangle" or "Ellipse" or "Line" or "Highlight" or "Text" or "Number" ? Visibility.Visible : Visibility.Collapsed;
        textProperties!.Visibility = tool == "Text" ? Visibility.Visible : Visibility.Collapsed;
        if (fillProperties != null) fillProperties.Visibility = tool is "Rectangle" or "Ellipse" ? Visibility.Visible : Visibility.Collapsed;
        ShowStrokeAsSize(tool is "Text" or "Number");
        toolHint.Visibility = maskProperties.Visibility == Visibility.Collapsed && strokeProperties.Visibility == Visibility.Collapsed && textProperties.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        toolProperties.Visibility = document == null ? Visibility.Hidden : Visibility.Visible;
        UpdateStrengthLabel();
        if (animate) Motion.Enter(toolProperties, y: 5, duration: 150);
    }
    private void ApplyStrength()
    {
        if (document == null || surface.SelectedId is not Guid id) return;
        var op = document.Operations.FirstOrDefault(x => x.Id == id);
        if (op == null || op.Kind is not (EditKind.Mosaic or EditKind.Blur) || op.Strength == surface.Strength) return;
        document.Update(op with { Strength = surface.Strength }); dirty = true; RefreshEditor();
    }
    private void RefreshEditor()
    {
        if (document == null) return;
        surface.Refresh();
        if (operations != null)
        {
            updating = true; operations.Items.Clear(); var i = 1;
            foreach (var op in document.Operations) { var row = new ListBoxItem { Content = $"{i++:00}  {KindName(op.Kind)}", Tag = op.Id }; operations.Items.Add(row); if (surface.SelectedId == op.Id) operations.SelectedItem = row; }
            updating = false;
        }
        if (undoButton != null) undoButton.IsEnabled = document.CanUndo;
        if (redoButton != null) redoButton.IsEnabled = document.CanRedo;
        UpdateZoomInfo();
        RefreshSelection();
        ScheduleHistoryImage();
        ScheduleClipboardSync();
        RefreshPreservationState();
    }
    private void RefreshSelection()
    {
        if (document == null) return;
        var op = document.Operations.FirstOrDefault(x => x.Id == surface.SelectedId);
        updating = true;
        if (op != null && EditorSurface.IsRedaction(op.Kind)) { surface.Strength = op.Strength; if (strengthSlider != null) strengthSlider.Value = op.Strength; UpdateStrengthLabel(); }
        if (operations != null) foreach (ListBoxItem item in operations.Items) item.IsSelected = item.Tag is Guid id && id == surface.SelectedId;
        updating = false;
        UpdateToolProperties(false);
    }
    private static string KindName(EditKind kind) => kind switch { EditKind.Mosaic => "모자이크", EditKind.Blur => "블러", EditKind.Solid => "완전 가리기", EditKind.Pen => "펜", EditKind.Arrow => "화살표", EditKind.Rectangle => "사각형", EditKind.Ellipse => "원", EditKind.Line => "직선", EditKind.Text => "텍스트", EditKind.Number => "번호", _ => "형광펜" };
    private void Fit()
    {
        if (document == null || canvasScroll == null || canvasScroll.ActualWidth < 50 || canvasScroll.ActualHeight < 50) return;
        zoom = Math.Clamp(Math.Min((canvasScroll.ActualWidth - 60) / document.Width, (canvasScroll.ActualHeight - 60) / document.Height), .02, 1);
        surface.LayoutTransform = new ScaleTransform(zoom, zoom);
        UpdateZoomInfo();
    }
    private void FitView() { if (document == null) return; fitView = true; Fit(); }
    private void SetZoom(double value) { if (document == null) return; fitView = false; zoom = Math.Clamp(value, .02, 8); surface.LayoutTransform = new ScaleTransform(zoom, zoom); UpdateZoomInfo(); }
    private void UpdateZoomInfo()
    {
        if (imageInfo != null) imageInfo.Text = document == null ? "" : $"{document.Width:N0} × {document.Height:N0} px";
        if (zoomButton != null) { zoomButton.Content = document == null ? "–" : zoom.ToString("P0"); zoomButton.IsEnabled = document != null; }
    }
    private void Undo() => Try(() => { surface.CancelPendingEdit(); document?.Undo(); surface.SelectedId = null; dirty = true; RefreshEditor(); if (fitView) Fit(); });
    private void Redo() => Try(() => { surface.CancelPendingEdit(); document?.Redo(); surface.SelectedId = null; dirty = true; RefreshEditor(); if (fitView) Fit(); });
    private void SetImage(BitmapSource image) { RememberCurrentDocument(); historySaveTimer.Stop(); FlushClipboardSync(); captureClipboard.Stop(); currentHistoryId = null; documentName = "캡처 이미지"; document = new ImageDocument(image); surface.SetDocument(document); surface.Tool = "Mosaic"; surface.Cursor = Cursors.Cross; if (!editorPageHasImage) editorPage = null; dirty = true; fitView = true; landPending = true; }
    // An editor built around an image is reused for the next one; only the empty-state page has to be rebuilt.
    private bool editorPageHasImage;
    // A freshly arrived image settles onto the canvas instead of appearing in place.
    private bool landPending;
    private void LandImage()
    {
        if (!landPending || canvasHost == null) return;
        landPending = false;
        if (canvasHost.RenderTransform is not ScaleTransform scale) { scale = new ScaleTransform(1, 1); canvasHost.RenderTransform = scale; canvasHost.RenderTransformOrigin = new Point(.5, .5); }
        Motion.Play(scale, ScaleTransform.ScaleXProperty, 1.05, 1, 300);
        Motion.Play(scale, ScaleTransform.ScaleYProperty, 1.05, 1, 300);
        Motion.Play(canvasHost, OpacityProperty, 0, 1, 220);
    }

    private async Task StartCapture(CaptureMode mode)
    {
        if (removingHistory || closingHistory || capturing || OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) return;
        // Claim the capture before any dialog so a repeated hotkey cannot stack confirmation windows.
        capturing = true;
        var wasVisible = IsVisible;
        try
        {
            if (!ConfirmReplace()) return;
            await HideForCaptureAsync();
            var image = await capture.CaptureAsync(mode, settings.IncludeCursor, settings.DelaySeconds, fixedWidth, fixedHeight, ShowCountdownAsync);
            if (image == null) { if (wasVisible) RestoreWindow(activate: true); Notify("캡처를 취소했습니다."); return; }
            SetImage(image); RecordCaptureHistory(capture.LastContext); ShowPage("editor", true); Notify("캡처 완료");
            FinishCapture(wasVisible);
        }
        catch (Exception ex) { RestoreWindow(activate: true); Notify("캡처 실패: " + ex.Message); }
        finally { capturing = false; NativeMethods.SetWindowTransitions(new WindowInteropHelper(this).Handle, true); }
    }
    // The window has to be off the screen before pixels are read. That takes a composed frame or two plus a
    // short settle so nothing of the window lingers; a window already in the tray or minimized is not on the
    // screen and needs no wait at all, which keeps the hotkey path instant.
    private async Task HideForCaptureAsync()
    {
        var onScreen = IsVisible && WindowState != WindowState.Minimized;
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowTransitions(handle, false);
        // Hiding the active window normally hands activation to the next app synchronously, which stalls for as
        // long as that app takes to answer (measured up to 1.5 s). Hide without activating anything; the
        // selection overlay takes the foreground a moment later anyway.
        if (onScreen && handle != IntPtr.Zero)
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SWP_HIDEWINDOW | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER);
        Hide();
        if (!onScreen) return;
        await Task.Yield();
        NativeMethods.WaitForComposition();
        await Task.Delay(HideSettleMilliseconds);
    }
    private const int HideSettleMilliseconds = 60;
    private async Task StartScroll()
    {
        if (removingHistory || closingHistory || capturing || OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) return;
        capturing = true;
        var wasVisible = IsVisible; var restore = wasVisible;
        try
        {
            if (!ConfirmReplace()) { restore = false; return; }
            await HideForCaptureAsync();
            var first = await capture.CaptureAsync(CaptureMode.Region, false, settings.DelaySeconds, countdown: ShowCountdownAsync);
            if (first == null || capture.SelectedRegion is not System.Drawing.Rectangle bounds) return;
            var captureWindow = new ScrollCaptureWindow(first, bounds);
            if (!await captureWindow.RunAsync()) return;
            SetImage(captureWindow.Result); RecordCaptureHistory(capture.LastContext is { } context ? context with { Mode = "Scroll" } : null); ShowPage("editor", true); Notify("스크롤 캡처 완료 · 연결 부분을 확인하세요.");
            restore = false; FinishCapture(wasVisible);
        }
        catch (Exception ex) { restore = true; Notify("스크롤 캡처 실패: " + ex.Message); }
        finally { capturing = false; NativeMethods.SetWindowTransitions(new WindowInteropHelper(this).Handle, true); if (restore) RestoreWindow(activate: true); }
    }
    private CountdownWindow? countdownWindow;
    private async Task ShowCountdownAsync(int remaining)
    {
        if (remaining > 0) { countdownWindow ??= new CountdownWindow(); countdownWindow.Show(remaining); return; }
        if (countdownWindow == null) return;
        countdownWindow.Hide();
        // Let the desktop repaint where the number was before the pixels are read.
        await Task.Yield();
        NativeMethods.WaitForComposition();
    }
    // A capture started from the tray or a minimized window must come back as a normal window.
    private void RestoreWindow(bool activate)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (activate) Activate();
    }
    // Captures are copied right away unless the user asked for a save dialog instead.
    // "복사만" leaves the user's focus where it was; the editor still holds the capture for later.
    private void FinishCapture(bool wasVisible)
    {
        if (settings.AfterCapture == "copy")
        {
            var copied = CopyCapture();
            if (wasVisible) RestoreWindow(activate: false);
            else if (copied) tray?.ShowBalloonTip(1500, "담아", "이미지를 복사했습니다.", Forms.ToolTipIcon.Info);
            return;
        }
        RestoreWindow(activate: true);
        if (settings.AfterCapture == "save") { Save(); return; }
        // The editor shows first; putting a large image on the clipboard can wait for the next idle moment.
        var target = document;
        Dispatcher.BeginInvoke(() => { if (ReferenceEquals(document, target) && CopyCapture()) Toast("복사했습니다"); }, System.Windows.Threading.DispatcherPriority.Background);
    }
    private void OpenDialog()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|모든 파일|*.*" };
        if (d.ShowDialog(this) == true) OpenFile(d.FileName);
    }
    private void OpenFile(string path) { if (!ConfirmReplace()) return; Try(() => { SetImage(ImageFiles.Load(path)); documentName = Path.GetFileName(path); dirty = false; ShowPage("editor", true); Notify(Path.GetFileName(path) + " 열기 완료"); }); }
    // A pasted image is still on the clipboard, so leaving it unedited needs no save prompt.
    private void PasteImage() { if (!ConfirmReplace()) return; Try(() => { var image = Clipboard.GetImage(); if (image == null) { Notify("클립보드에 이미지가 없습니다."); return; } SetImage(image); dirty = false; ShowPage("editor", true); Notify("클립보드 이미지를 열었습니다."); }); }
    private void Copy() { if (!RequireImage()) return; Try(() => { surface.CompletePendingEdit(); clipboardSyncTimer.Stop(); ClipboardTransfer.SetImage(document!.Render()); captureClipboard.Adopt(document); Toast("복사했습니다"); }); }
    private void Save() => SaveImage();
    private bool SaveImage()
    {
        if (!RequireImage()) return false;
        surface.CompletePendingEdit();
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 이미지|*.png|JPEG 이미지|*.jpg|BMP 이미지|*.bmp", FileName = "담아_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), DefaultExt = ".png", AddExtension = true, OverwritePrompt = true };
        // The default folder is only a suggestion, so a failure to create it must not block saving elsewhere.
        try { if (!string.IsNullOrWhiteSpace(settings.SaveFolder)) Directory.CreateDirectory(settings.SaveFolder); } catch (Exception) { }
        if (Directory.Exists(settings.SaveFolder)) d.InitialDirectory = settings.SaveFolder;
        if (d.ShowDialog(this) != true) return false;
        try { ImageFiles.Save(document!.Render(), d.FileName, settings.JpegQuality); if (currentHistoryId is Guid id) StoreFor(id).AttachExport(id, d.FileName); else history.Add(d.FileName); dirty = false; documentName = Path.GetFileName(d.FileName); RefreshEditor(); PreserveHistoryImage(); RefreshHistory(); Toast("저장했습니다 · " + Path.GetFileName(d.FileName)); return true; }
        catch (Exception ex) { Notify("저장 실패: " + ex.Message); MessageBox.Show(this, ex.Message, "저장하지 못했습니다", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    private bool RequireImage() { if (document != null) return true; Notify("먼저 캡처하거나 이미지 파일을 열어주세요."); return false; }
    private bool ConfirmReplace()
    {
        surface.CompletePendingEdit();
        if (!dirty || document == null) return true;
        if (AutoPreservesCurrent) { surface.CompletePendingEdit(); PreserveHistoryImage(); return true; }
        var result = MessageBox.Show(this, "현재 이미지를 저장하고 계속할까요?\n‘아니요’를 선택하면 저장하지 않고 계속합니다.", "편집 중인 이미지", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || (result == MessageBoxResult.Yes && SaveImage());
    }
    private void ResizeImage()
    {
        if (!RequireImage()) return;
        surface.CompletePendingEdit();
        var dialog = new Window { Title = "이미지 크기 변경", Owner = this, Icon = Icon, Width = 350, Height = 320, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var p = new StackPanel { Margin = new Thickness(24) }; var w = new TextBox { Text = document!.Width.ToString() }; var h = new TextBox { Text = document.Height.ToString() }; var keep = new CheckBox { Content = "가로세로 비율 유지", IsChecked = true }; var adjusting = false; var ratio = (double)document.Width / document.Height;
        w.TextChanged += (_, _) => { if (adjusting || keep.IsChecked != true || !int.TryParse(w.Text, out var value)) return; adjusting = true; h.Text = Math.Max(1, (int)Math.Round(value / ratio)).ToString(); adjusting = false; };
        h.TextChanged += (_, _) => { if (adjusting || keep.IsChecked != true || !int.TryParse(h.Text, out var value)) return; adjusting = true; w.Text = Math.Max(1, (int)Math.Round(value * ratio)).ToString(); adjusting = false; };
        p.Children.Add(Ui.Field("가로 px", w)); p.Children.Add(Ui.Field("세로 px", h)); p.Children.Add(keep);
        p.Children.Add(Ui.Button("크기 변경", () => { if (!int.TryParse(w.Text, out var width) || !int.TryParse(h.Text, out var height) || width < 1 || height < 1 || width > 16000 || height > 20000 || (long)width * height > 64_000_000) { MessageBox.Show(dialog, "가로 1~16,000, 세로 1~20,000px, 총 6,400만 픽셀 이하로 입력하세요."); return; } Try(() => { document.Resize(width, height); dirty = true; surface.SelectedId = null; RefreshEditor(); Fit(); dialog.Close(); }); }, true)); dialog.Content = p; dialog.ShowDialog();
    }
    private void Pin()
    {
        if (!RequireImage()) return;
        surface.CompletePendingEdit();
        var bitmap = document!.Render(); var w = new Window { Title = "담아 | 화면 위에 고정", Icon = Icon, Topmost = true, Width = Math.Clamp(bitmap.PixelWidth * .6, 240, 850), Height = Math.Clamp(bitmap.PixelHeight * .6 + 35, 180, 650), Background = Ui.Panel };
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform }; w.Content = image;
        var menu = new ContextMenu(); var copy = new MenuItem { Header = "이미지 복사" }; copy.Click += (_, _) => Try(() => ClipboardTransfer.SetImage(bitmap)); menu.Items.Add(copy); var close = new MenuItem { Header = "고정 창 닫기" }; close.Click += (_, _) => w.Close(); menu.Items.Add(close); image.ContextMenu = menu;
        pinWindows.Add(w); w.Closed += (_, _) => pinWindows.Remove(w); w.Show();
    }
    private void Print()
    {
        if (!RequireImage()) return;
        surface.CompletePendingEdit();
        Try(() => { var d = new PrintDialog(); if (d.ShowDialog() != true) return; var image = new Image { Source = document!.Render(), Width = d.PrintableAreaWidth, Height = d.PrintableAreaHeight, Stretch = Stretch.Uniform }; image.Measure(new Size(d.PrintableAreaWidth, d.PrintableAreaHeight)); image.Arrange(new Rect(0, 0, d.PrintableAreaWidth, d.PrintableAreaHeight)); d.PrintVisual(image, "담아 이미지"); Notify("편집된 이미지를 프린터에 전달했습니다."); });
    }
    private void ExternalEditor()
    {
        if (!RequireImage()) return;
        surface.CompletePendingEdit();
        Try(() => { var folder = Path.Combine(Path.GetTempPath(), "DamaCapture"); Directory.CreateDirectory(folder); var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png"); ImageFiles.Save(document!.Render(), path); var p = new ProcessStartInfo("mspaint.exe") { UseShellExecute = true }; p.ArgumentList.Add(path); Process.Start(p); Notify("편집된 PNG를 그림판에서 열었습니다. 임시 파일: " + path); });
    }

    private bool RegisterHotkeys()
    {
        hotkeys?.Dispose(); hotkeys = new GlobalHotkeys(this);
        hotkeys.Pressed += id => Dispatcher.BeginInvoke(async () => { if (capturing || OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) return; if (id == 3) await StartScroll(); else await StartCapture(id == 2 ? CaptureMode.Window : CaptureMode.Region); });
        var a = hotkeys.Register(1, settings.HotkeyModifiers, settings.RegionHotkey); var b = hotkeys.Register(2, settings.HotkeyModifiers, settings.WindowHotkey); var c = hotkeys.Register(3, settings.HotkeyModifiers, settings.ScrollHotkey);
        if (!(a && b && c)) Notify("일부 전역 단축키가 다른 앱과 겹칩니다. 설정에서 변경할 수 있습니다.");
        return a && b && c;
    }
    private void SetupTray()
    {
        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/dama.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(iconStream);
        tray = new Forms.NotifyIcon { Text = "담아 | 화면 캡처", Icon = (System.Drawing.Icon)icon.Clone(), Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("담아 열기", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
        menu.Items.Add("영역 캡처", null, (_, _) => Dispatcher.Invoke(async () => await StartCapture(CaptureMode.Region)));
        menu.Items.Add("창 캡처", null, (_, _) => Dispatcher.Invoke(async () => await StartCapture(CaptureMode.Window)));
        menu.Items.Add("스크롤 캡처", null, (_, _) => Dispatcher.Invoke(async () => await StartScroll()));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("담아 정보", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); ShowAbout(); }));
        menu.Items.Add("완전히 종료", null, (_, _) => Dispatcher.Invoke(Exit)); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (removingHistory || capturing) { e.Cancel = true; return; }
        if (closingHistory && !exiting) { e.Cancel = true; return; }
        if (!exiting && settings.CloseToTray && !demo) { e.Cancel = true; Hide(); tray?.ShowBalloonTip(1800, "담아", $"트레이에서 실행 중입니다. {HotkeyText(settings.RegionHotkey)}로 캡처하세요.", Forms.ToolTipIcon.Info); return; }
        if (exiting) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(Exit);
    }
    // Windows ignores a cancelled close at logoff, so flush what a tray-resident app still holds.
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        try
        {
            clipboardMonitor?.SetEnabled(false);
            surface.CompletePendingEdit(); PreserveHistoryImage();
            Task.Run(FlushHistoryWritersAsync).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { }
    }
    private async void Exit()
    {
        if (removingHistory || closingHistory) return;
        if (capturing) { Notify("캡처가 끝난 뒤 종료할 수 있습니다."); return; }
        Show(); Activate(); if (!ConfirmReplace()) return;
        closingHistory = true;
        IsEnabled = false;
        try
        {
            clipboardMonitor?.SetEnabled(false);
            PreserveHistoryImage(); await FlushHistoryWritersAsync();
            if ((historyWriter.HasFailures || clipboardWriter.HasFailures || history.HasPersistenceFailures || clipboardHistory.HasPersistenceFailures) && MessageBox.Show(this, "일부 기록을 남기지 못했습니다. 그냥 종료할까요?", "기록", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            exiting = true; Close();
        }
        finally { closingHistory = false; if (!exiting) { IsEnabled = true; ApplyClipboardSetting(); } }
    }
    private void KeyDownHandler(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && historySelecting) { SetHistorySelecting(false); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
        // With the Korean IME active, letter keys arrive as ImeProcessed; use the physical key.
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (key == Key.S) { Save(); e.Handled = true; } else if (key == Key.C) { Copy(); e.Handled = true; } else if (key == Key.V) { PasteImage(); e.Handled = true; } else if (key == Key.O) { OpenDialog(); e.Handled = true; } else if (key == Key.Z) { Undo(); e.Handled = true; } else if (key == Key.Y) { Redo(); e.Handled = true; }
            else if (currentPage == "editor" && key is Key.OemPlus or Key.Add) { SetZoom(zoom * 1.25); e.Handled = true; }
            else if (currentPage == "editor" && key is Key.OemMinus or Key.Subtract) { SetZoom(zoom / 1.25); e.Handled = true; }
            else if (currentPage == "editor" && key is Key.D0 or Key.NumPad0) { FitView(); e.Handled = true; }
            else if (currentPage == "editor" && key is Key.D1 or Key.NumPad1) { SetZoom(1); e.Handled = true; }
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && currentPage == "editor")
        {
            var tool = document == null || key == Key.None ? null : EditTools.FirstOrDefault(x => x.Key == key).Id;
            if (tool != null) { SelectTool(tool); e.Handled = true; }
            else if (key == Key.Delete) { surface.DeleteSelection(); e.Handled = true; }
            else if (key == Key.Escape) { surface.CancelPendingEdit(); surface.Select(null); if (surface.Tool == "Extract") LeaveExtractMode(); e.Handled = true; }
        }
    }
    // Waiting in the tray, the app keeps only what the next capture needs: the open image stays, older edit
    // sessions and thumbnails go, freed memory is returned to Windows and the idle working set is released.
    private readonly System.Windows.Threading.DispatcherTimer idleTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
    private void ReleaseIdleMemory()
    {
        idleTimer.Stop();
        if (IsVisible || capturing || exiting || demo) return;
        sessionDocuments.KeepNewest(1); thumbnails.Clear();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        SetProcessWorkingSetSize(process.Handle, -1, -1);
    }
    private void Notify(string message) { status.Text = message; status.ToolTip = message; Motion.Enter(status, y: 3, duration: 140); }

    // Completed actions are confirmed where the user is looking, briefly, with an optional way back.
    private Border? toastHost; private TextBlock? toastText; private Button? toastButton; private Border? toastDivider; private Ring? toastRing; private Action? toastAction;
    private readonly System.Windows.Threading.DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private Border BuildToastHost()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        toastText = new TextBlock { FontSize = 13, Foreground = Ui.OnPrimary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        row.Children.Add(toastText);
        toastDivider = new Border { Width = 1, Height = 16, Background = Ui.Brush("#4DFFFFFF"), Margin = new Thickness(12, 0, 4, 0) }; row.Children.Add(toastDivider);
        toastButton = Ui.Button("", () => { var action = toastAction; HideToast(); action?.Invoke(); });
        Ui.SetGhost(toastButton, false, false); toastButton.Foreground = Ui.OnPrimary; toastButton.FontWeight = FontWeights.SemiBold; toastButton.Height = 28; toastButton.Padding = new Thickness(8, 0, 6, 0);
        row.Children.Add(toastButton);
        // How long the way back stays open, drawn rather than counted.
        toastRing = new Ring(14) { Thickness = 2, Track = Ui.Brush("#4A494D"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) }; row.Children.Add(toastRing);
        toastHost = new Border
        {
            Child = row, Background = Ui.Brush("#1F1E21"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20), Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(toastHost, AutomationLiveSetting.Polite);
        toastTimer.Tick += (_, _) => HideToast();
        return toastHost;
    }
    private void Toast(string message, string? actionLabel = null, Action? action = null)
    {
        Notify(message);
        if (toastHost == null || toastText == null || toastButton == null || toastDivider == null || toastRing == null) return;
        toastText.Text = message; toastAction = action; toastButton.Content = actionLabel ?? "";
        toastButton.Visibility = toastDivider.Visibility = toastRing.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
        if (actionLabel != null) AutomationProperties.SetName(toastButton, actionLabel);
        var seconds = action == null ? 2.5 : 5;
        toastHost.Visibility = Visibility.Visible; Motion.Enter(toastHost, y: 14, duration: 220);
        if (action != null) toastRing.Drain((int)(seconds * 1000));
        toastTimer.Stop(); toastTimer.Interval = TimeSpan.FromSeconds(seconds); toastTimer.Start();
    }
    private void HideToast() { toastTimer.Stop(); toastAction = null; if (toastHost != null) toastHost.Visibility = Visibility.Collapsed; }
    private void Try(Action action) { try { action(); } catch (Exception ex) { Notify("작업 실패: " + ex.Message); MessageBox.Show(this, ex.Message, "담아", MessageBoxButton.OK, MessageBoxImage.Warning); } }
}
