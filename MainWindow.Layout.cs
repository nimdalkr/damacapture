using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DamaCapture.Capture;
using DamaCapture.Imaging;
using CaptureMode = DamaCapture.Capture.CaptureMode;

namespace DamaCapture;

internal sealed partial class MainWindow
{
    private string documentName = "캡처 이미지";
    private StackPanel? recentFiles, footerTools;
    private TabControl? sideTabs;
    private Size editorSize = new(1160, 780);
    private Rect? editorBounds;
    private bool editorMaximized;
    private ComboBox? captureDelay;
    private readonly List<ToolGroupView> toolGroups = new();
    private readonly List<(Button Swatch, Color Ink)> inkSwatches = new();
    private TextBlock? strengthValue;
    private Button? zoomButton;
    // Secondary command labels that give way to the preservation status on narrow windows.
    private readonly List<TextBlock> compactLabels = new();
    private void UpdateCompactLabels()
    {
        var compact = ActualWidth > 0 && ActualWidth < 1040;
        foreach (var label in compactLabels) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
    private sealed class ToolGroupView(Border surface, Button main, Button? dropdown, string[] tools)
    {
        public Border Surface { get; } = surface;
        public Button Main { get; } = main;
        public Button? Dropdown { get; } = dropdown;
        public string[] Tools { get; } = tools;
        public string Current { get; set; } = tools[0];
    }
    private FrameworkElement? detailsPanel;
    private Border? canvasHost;
    // The tint behind the active tool is one surface that glides between tool groups.
    private Border? toolPill; private Grid? toolHost;
    private ColumnDefinition? detailsColumn;
    private Button? detailsButton;
    private bool detailsOpen;
    private Rect CurrentWorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    // Single-letter shortcuts work only while no text field has focus.
    private static readonly (string Id, string Label, string Icon, Key Key)[] EditTools =
    {
        ("Select", "선택", "select", Key.V), ("Pen", "펜", "pen", Key.P), ("Highlight", "형광펜", "highlight", Key.H),
        ("Arrow", "화살표", "arrow", Key.A), ("Line", "직선", "line", Key.L), ("Rectangle", "사각형", "rectangle", Key.R),
        ("Ellipse", "원", "ellipse", Key.E), ("Text", "텍스트", "text", Key.T), ("Number", "번호", "number", Key.N),
        ("Mosaic", "모자이크", "mosaic", Key.M), ("Blur", "블러", "blur", Key.B), ("Solid", "가리기", "solid", Key.None), ("Crop", "자르기", "crop", Key.C)
    };
    private static string ToolName(string tool) => EditTools.FirstOrDefault(x => x.Id == tool).Label ?? "편집";
    private static string ToolShortcut(string tool) { var key = EditTools.FirstOrDefault(x => x.Id == tool).Key; return key == Key.None ? "" : key.ToString(); }
    private static string ToolTipText(string tool) { var key = ToolShortcut(tool); return ToolName(tool) + (key.Length > 0 ? $" ({key})" : ""); }
    private static string HotkeyText(uint key) => "Ctrl+Shift+" + (key is >= 0x70 and <= 0x7B ? "F" + (key - 0x70 + 1) : ((char)key).ToString());
    private string HotkeyHint() => $"{HotkeyText(settings.RegionHotkey)} 영역  ·  {HotkeyText(settings.WindowHotkey)} 창  ·  {HotkeyText(settings.ScrollHotkey)} 스크롤";
    private static Border Divider() => new() { Width = 1, Height = 20, Background = Ui.Line, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private static MenuItem MenuAction(string text, Action action, string? shortcut = null)
    {
        var item = new MenuItem { Header = text, InputGestureText = shortcut ?? "" };
        item.Click += (_, _) => action(); return item;
    }
    private UIElement Shell()
    {
        var root = new DockPanel();
        var footer = new DockPanel { LastChildFill = true, Height = 32, Background = Ui.Panel };
        footerTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        imageInfo = Ui.Label("", 12, Ui.Muted); imageInfo.Margin = new Thickness(8, 0, 12, 0); footerTools.Children.Add(imageInfo);
        Button FooterIcon(string label, string icon, Action action)
        {
            var button = Ui.IconButton(label, icon, action, compact: true); button.Width = 28; button.Height = 28; button.Padding = new Thickness(5);
            footerTools.Children.Add(button); return button;
        }
        FooterIcon("축소 (Ctrl+-)", "zoom-out", () => SetZoom(zoom / 1.25));
        zoomButton = Ui.Button("100%", () => SetZoom(1)); zoomButton.ToolTip = "원본 크기로 보기 (Ctrl+1)"; zoomButton.Margin = new Thickness(1, 0, 1, 0);
        zoomButton.Padding = new Thickness(4, 0, 4, 0); zoomButton.MinWidth = 50; zoomButton.MinHeight = 26; zoomButton.Height = 26; Ui.SetGhost(zoomButton, false, false);
        AutomationProperties.SetName(zoomButton, "원본 크기로 보기"); footerTools.Children.Add(zoomButton);
        FooterIcon("확대 (Ctrl++)", "zoom-in", () => SetZoom(zoom * 1.25));
        FooterIcon("화면에 맞춤 (Ctrl+0)", "fit", FitView);
        DockPanel.SetDock(footerTools, Dock.Right); footer.Children.Add(footerTools);
        status.FontSize = 12; status.Margin = new Thickness(14, 0, 8, 0); status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(status);
        var border = new Border { Child = footer, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(border, Dock.Bottom); root.Children.Add(border); root.Children.Add(content);
        return root;
    }
    private void ShowPage(string page, bool refresh = false)
    {
        if (page == "settings") { ShowSettings(); return; }
        if (page == "history") { ShowPage("editor"); if (!detailsOpen) ToggleDetails(); if (sideTabs != null) sideTabs.SelectedIndex = 0; return; }
        if (currentPage == page && content.Content != null && !refresh) return;
        if (currentPage == "editor") surface.CompletePendingEdit();
        if (page == "home" && currentPage == "editor")
        {
            editorMaximized = WindowState == WindowState.Maximized;
            editorBounds = editorMaximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            editorSize = editorBounds.Value.Size;
        }
        var wasHome = currentPage == "home";
        currentPage = page;
        if (page == "home")
        {
            // The launcher sizes itself so its rows are never clipped by the title bar height.
            // Width follows the buttons too, so the last one (더보기) is never cut off.
            WindowState = WindowState.Normal; MinWidth = 0; MinHeight = 0; SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.CanMinimize; Title = "담아 캡처";
            content.Content = Home(); footerTools!.Visibility = Visibility.Collapsed; Notify(HotkeyHint());
            Motion.WhenLoadedEnter((FrameworkElement)content.Content, y: 4, duration: 160);
        }
        else
        {
            var area = CurrentWorkArea();
            SizeToContent = SizeToContent.Manual;
            ResizeMode = ResizeMode.CanResize; MinWidth = Math.Min(900, area.Width); MinHeight = Math.Min(580, area.Height);
            if (wasHome)
            {
                Width = Math.Min(editorSize.Width, area.Width); Height = Math.Min(editorSize.Height, area.Height);
                Left = Math.Clamp(editorBounds?.Left ?? area.Left + (area.Width - Width) / 2, area.Left, area.Right - Width);
                Top = Math.Clamp(editorBounds?.Top ?? area.Top + (area.Height - Height) / 2, area.Top, area.Bottom - Height);
                if (editorMaximized) WindowState = WindowState.Maximized;
            }
            var reused = editorPage != null;
            content.Content = editorPage ??= (FrameworkElement)Editor(); footerTools!.Visibility = Visibility.Visible;
            if (reused && refresh)
            {
                // Same controls, new image: bring their state up to date instead of rebuilding the page.
                HideToast(); RefreshToolButtons(false); UpdateToolProperties(false); RefreshEditor(); RefreshHistoryIfVisible();
            }
            Notify(document == null ? "이미지를 열거나 캡처하세요." : toolHint?.Text ?? "준비");
            RefreshPreservationState();
            ScanSensitiveIfNeeded();
            if (editorToolbar != null) Motion.WhenLoadedEnter(editorToolbar, y: -4, duration: 160);
            if (editorProperties != null) Motion.WhenLoadedEnter(editorProperties, x: 5, y: 0, duration: 180);
            Dispatcher.BeginInvoke(() => { if (currentPage != "editor") return; if (fitView) Fit(); LandImage(); });
        }
    }
    private UIElement Home()
    {
        var root = new DockPanel { Background = Ui.Panel };
        var options = new DockPanel { Height = 46, Margin = new Thickness(16, 0, 10, 0) };
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        delayRow.Children.Add(Ui.Label("지연", 12, Ui.Muted));
        var delays = new[] { 0, 3, 5, 10, settings.DelaySeconds }.Distinct().Order().ToArray();
        captureDelay = Ui.Choice(delays.Select(x => x == 0 ? "없음" : x + "초").ToArray(), Array.IndexOf(delays, settings.DelaySeconds));
        captureDelay.Width = 78; captureDelay.MinHeight = 28; captureDelay.Height = 28; captureDelay.FontSize = 12; captureDelay.Margin = new Thickness(8, 0, 20, 0);
        AutomationProperties.SetName(captureDelay, "캡처 지연 시간");
        captureDelay.SelectionChanged += (_, _) => settings.DelaySeconds = delays[Math.Max(0, captureDelay.SelectedIndex)]; delayRow.Children.Add(captureDelay);
        var cursor = new CheckBox { Content = "커서 포함", FontSize = 12, IsChecked = settings.IncludeCursor, Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        cursor.Click += (_, _) => settings.IncludeCursor = cursor.IsChecked == true; delayRow.Children.Add(cursor);
        var links = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        links.Children.Add(Ui.IconButton("이미지 열기 (Ctrl+O)", "open", OpenDialog, compact: true));
        var historyLink = Ui.CommandButton("기록", "history", () => ShowPage("history")); Ui.SetGhost(historyLink, false, false); historyLink.Margin = new Thickness(2, 0, 2, 0); links.Children.Add(historyLink);
        links.Children.Add(Ui.IconButton("환경 설정", "settings", ShowSettings, compact: true));
        DockPanel.SetDock(links, Dock.Right); options.Children.Add(links); options.Children.Add(delayRow);
        var optionsBar = new Border { Child = options, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(optionsBar, Dock.Bottom); root.Children.Add(optionsBar);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 10) };
        // Each launcher button captures immediately. Region capture is the one most people want, so it leads.
        void CaptureButton(string label, string icon, Action action, uint? hotkey = null, bool primary = false)
        {
            var b = Ui.IconButton(label, icon, action, primary); b.Width = primary ? 104 : 66; b.Height = 62;
            if (primary)
            {
                b.Margin = new Thickness(0, 0, 8, 0);
                // The lead button carries the mark: the dot drops into the frame each time the launcher opens.
                var mark = new BrandMark(24) { Frame = Ui.OnPrimary };
                if (b.Content is StackPanel stack) { stack.Children.RemoveAt(0); stack.Children.Insert(0, mark); }
                mark.Loaded += (_, _) => mark.Play();
            }
            b.ToolTip = hotkey is uint key ? $"{label} ({HotkeyText(key)})" : label;
            bar.Children.Add(b);
        }
        CaptureButton("영역 캡처", "region", async () => await StartCapture(CaptureMode.Region), settings.RegionHotkey, primary: true);
        CaptureButton("자유형", "freehand", async () => await StartCapture(CaptureMode.Freehand));
        CaptureButton("창", "window", async () => await StartCapture(CaptureMode.Window), settings.WindowHotkey);
        CaptureButton("단위 영역", "element", async () => await StartCapture(CaptureMode.Element));
        CaptureButton("전체 화면", "screen", async () => await StartCapture(CaptureMode.FullScreen));
        CaptureButton("스크롤", "scroll", async () => await StartScroll(), settings.ScrollHotkey);
        CaptureButton("크기 지정", "fixed", CaptureFixedSize);
        var more = Ui.IconButton("더보기", "more", () => { }); more.Width = 66; more.Height = 62;
        more.Click += (_, _) =>
        {
            var menu = NewMenu();
            menu.Items.Add(MenuAction("이전 영역 다시 캡처", async () => await StartCapture(CaptureMode.LastRegion)));
            menu.Items.Add(MenuAction("모든 모니터 캡처", async () => await StartCapture(CaptureMode.AllScreens)));
            menu.Items.Add(new Separator()); menu.Items.Add(MenuAction("이미지 붙여넣기", PasteImage, "Ctrl+V"));
            menu.Items.Add(MenuAction("예시 이미지 열기", () => { if (ConfirmReplace()) { SetImage(ImageFactory.Sample()); dirty = false; ShowPage("editor", true); } }));
            menu.Items.Add(new Separator()); menu.Items.Add(MenuAction("담아 정보", ShowAbout)); menu.Items.Add(MenuAction("종료", Exit)); OpenMenu(more, menu);
        };
        bar.Children.Add(more); root.Children.Add(bar); return root;
    }
    private void RefreshCaptureOptions()
    {
        if (currentPage == "home") ShowPage("home", true);
    }
    private void CaptureFixedSize()
    {
        var dialog = new Window { Owner = this, Icon = Icon, Title = "캡처 크기 지정", Width = 320, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        var caption = Ui.Label("가로 × 세로 (px)", 12, Ui.Muted); caption.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(caption);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var width = new TextBox { Text = fixedWidth.ToString(), Width = 96 }; var height = new TextBox { Text = fixedHeight.ToString(), Width = 96 };
        AutomationProperties.SetName(width, "캡처 가로 px"); AutomationProperties.SetName(height, "캡처 세로 px");
        row.Children.Add(width); var times = Ui.Label("×", 13, Ui.Muted); times.Margin = new Thickness(10, 0, 10, 0); row.Children.Add(times); row.Children.Add(height); panel.Children.Add(row);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = Ui.Button("취소", () => dialog.DialogResult = false); cancel.Width = 76; cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0);
        var button = Ui.Button("캡처", () =>
        {
            if (!int.TryParse(width.Text, out var w) || !int.TryParse(height.Text, out var h) || w < 1 || h < 1 || w > 16000 || h > 20000 || (long)w * h > 64_000_000)
            { MessageBox.Show(dialog, "가로 1~16,000px, 세로 1~20,000px, 총 6,400만 픽셀 이하로 입력하세요.", "캡처 크기", MessageBoxButton.OK, MessageBoxImage.Warning); width.Focus(); width.SelectAll(); return; }
            fixedWidth = w; fixedHeight = h; dialog.DialogResult = true;
        }, true);
        button.Margin = new Thickness(0); button.Width = 76; button.IsDefault = true;
        footer.Children.Add(cancel); footer.Children.Add(button); panel.Children.Add(footer); dialog.Content = panel;
        dialog.Loaded += (_, _) => { width.Focus(); width.SelectAll(); };
        if (dialog.ShowDialog() == true) _ = StartCapture(CaptureMode.FixedSize);
    }
    private static ContextMenu NewMenu() => new() { MinWidth = 180 };
    private static void OpenMenu(Button anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Bottom; menu.VerticalOffset = 5;
        anchor.ContextMenu = menu; menu.IsOpen = true;
    }
    // A labelled command whose only action is to open its menu.
    private Button CommandMenuButton(string label, string icon, Func<ContextMenu> menu)
    {
        var button = Ui.CommandButton(label, icon, () => { }); Ui.SetGhost(button, false, false);
        var content = (StackPanel)button.Content;
        var chevron = Ui.Icon("chevron-down", 14); chevron.Margin = new Thickness(4, 1, 0, 0); content.Children.Add(chevron);
        button.ContextMenu = NewMenu();
        button.Click += (_, _) => OpenMenu(button, menu());
        button.ContextMenuOpening += (_, e) => { e.Handled = true; OpenMenu(button, menu()); };
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Down) { e.Handled = true; OpenMenu(button, menu()); } };
        return button;
    }
    private Button MenuButton(string name, string icon, Func<ContextMenu> menu)
    {
        var button = Ui.IconButton(name, icon, () => { }, compact: true);
        button.ContextMenu = NewMenu();
        button.Click += (_, _) => OpenMenu(button, menu());
        button.ContextMenuOpening += (_, e) => { e.Handled = true; OpenMenu(button, menu()); };
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Down) { e.Handled = true; OpenMenu(button, menu()); } };
        return button;
    }
    private ContextMenu CaptureMenu()
    {
        var menu = NewMenu();
        foreach (var (mode, label) in new[] { (CaptureMode.Region, "영역 캡처"), (CaptureMode.Freehand, "자유형"), (CaptureMode.Window, "창"), (CaptureMode.Element, "단위 영역"), (CaptureMode.FullScreen, "전체 화면"), (CaptureMode.AllScreens, "모든 모니터"), (CaptureMode.LastRegion, "이전 영역") })
            menu.Items.Add(MenuAction(label, async () => await StartCapture(mode), mode == CaptureMode.Region ? HotkeyText(settings.RegionHotkey) : mode == CaptureMode.Window ? HotkeyText(settings.WindowHotkey) : null));
        menu.Items.Add(MenuAction("크기 지정", CaptureFixedSize)); menu.Items.Add(MenuAction("스크롤", async () => await StartScroll(), HotkeyText(settings.ScrollHotkey)));
        menu.Items.Add(new Separator()); menu.Items.Add(MenuAction("캡처 실행창", () => ShowPage("home"))); return menu;
    }
    private ContextMenu EditorMenu()
    {
        var menu = NewMenu();
        var extract = MenuAction("텍스트 추출", ExtractText); extract.Icon = Ui.Icon("ocr", 16); extract.IsEnabled = document != null; menu.Items.Add(extract);
        menu.Items.Add(MenuAction("이미지 붙여넣기", PasteImage, "Ctrl+V"));
        menu.Items.Add(new Separator());
        void ImageAction(string title, Action action) { var item = MenuAction(title, action); item.IsEnabled = document != null; menu.Items.Add(item); }
        ImageAction("이미지 크기 변경", ResizeImage);
        ImageAction("오른쪽으로 90° 회전", () => Try(() => { surface.CompletePendingEdit(); document!.Rotate90(); surface.SelectedId = null; dirty = true; RefreshEditor(); Fit(); }));
        ImageAction("화면 위에 고정", Pin); ImageAction("인쇄", Print); ImageAction("그림판에서 열기", ExternalEditor);
        menu.Items.Add(new Separator());
        var remove = MenuAction("선택한 편집 삭제", () => surface.DeleteSelection(), "Del"); remove.IsEnabled = surface.SelectedId != null; menu.Items.Add(remove);
        menu.Items.Add(new Separator()); menu.Items.Add(MenuAction("환경 설정", ShowSettings)); menu.Items.Add(MenuAction("캡처 실행창", () => ShowPage("home"))); menu.Items.Add(MenuAction("담아 정보", ShowAbout)); menu.Items.Add(MenuAction("종료", Exit));
        return menu;
    }
    private UIElement Editor()
    {
        editorPageHasImage = document != null;
        toolGroups.Clear();
        var root = new DockPanel { Background = Ui.Panel };
        var top = new StackPanel(); editorToolbar = top;
        var commands = new Grid { Height = 54, Margin = new Thickness(12, 0, 12, 0) };
        commands.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        commands.ColumnDefinitions.Add(new ColumnDefinition());
        commands.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // One primary action (복사). 저장 and AI stay visible as secondary; the rest lives in 더보기.
        var exports = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var share = CommandMenuButton("AI에서 편집하기", "send", () =>
        {
            var menu = NewMenu();
            foreach (var service in ChatServices) menu.Items.Add(MenuAction(service.Name, () => SendImageToChat(service)));
            return menu;
        });
        share.IsEnabled = document != null; share.Margin = new Thickness(0, 0, 10, 0); share.ToolTip = "AI에서 편집하기"; exports.Children.Add(share);
        compactLabels.Clear();
        if (share.Content is StackPanel shareRow && shareRow.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock shareLabel) compactLabels.Add(shareLabel);
        UpdateCompactLabels();
        var save = Ui.CommandButton("저장", "save", Save); Ui.SetGhost(save, false, false); save.IsEnabled = document != null; save.Margin = new Thickness(0, 0, 6, 0); save.ToolTip = "파일로 저장 (Ctrl+S)"; exports.Children.Add(save);
        var copy = Ui.CommandButton("복사", "copy", Copy, true); copy.IsEnabled = document != null; copy.Padding = new Thickness(16, 0, 18, 0); copy.ToolTip = "이미지 복사 (Ctrl+C)"; exports.Children.Add(copy);
        var more = MenuButton("더보기", "more", EditorMenu); more.Margin = new Thickness(6, 0, 0, 0); exports.Children.Add(more);
        Grid.SetColumn(exports, 2);
        preservationLabel = Ui.Label("", 12, Ui.Muted);
        preservationLabel.Margin = new Thickness(16, 0, 14, 0); preservationLabel.HorizontalAlignment = HorizontalAlignment.Right;
        preservationLabel.TextWrapping = TextWrapping.NoWrap; preservationLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(preservationLabel, "기록 상태"); AutomationProperties.SetLiveSetting(preservationLabel, AutomationLiveSetting.Polite);
        Grid.SetColumn(preservationLabel, 1);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var newCapture = Ui.CommandButton("새 캡처", "capture", async () => await StartCapture(CaptureMode.Region)); newCapture.ToolTip = $"새 캡처 ({HotkeyText(settings.RegionHotkey)})";
        var captureModes = MenuButton("캡처 방식", "chevron-down", CaptureMenu);
        var captureSplit = Ui.Split(newCapture, captureModes); captureSplit.Margin = new Thickness(0, 0, 6, 0); actions.Children.Add(captureSplit);
        var openImage = Ui.CommandButton("열기", "open", OpenDialog); Ui.SetGhost(openImage, false, false); openImage.ToolTip = "이미지 열기 (Ctrl+O)";
        AutomationProperties.SetName(openImage, "이미지 열기"); actions.Children.Add(openImage);
        commands.Children.Add(actions); commands.Children.Add(preservationLabel); commands.Children.Add(exports); top.Children.Add(commands);
        var toolRow = new DockPanel { Margin = new Thickness(10, 0, 12, 0), MinHeight = 46 };
        // Undo and redo belong with the tools they revert.
        var toolRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        undoButton = Ui.IconButton("실행 취소 (Ctrl+Z)", "undo", Undo, compact: true); redoButton = Ui.IconButton("다시 실행 (Ctrl+Y)", "redo", Redo, compact: true);
        toolRight.Children.Add(undoButton); toolRight.Children.Add(redoButton); toolRight.Children.Add(Divider());
        detailsButton = Ui.CommandButton("기록", "history", ToggleDetails); detailsButton.VerticalAlignment = VerticalAlignment.Center;
        detailsButton.ToolTip = detailsOpen ? "기록 접기" : "기록 열기";
        SetDetailsButton(false); toolRight.Children.Add(detailsButton);
        DockPanel.SetDock(toolRight, Dock.Right); toolRow.Children.Add(toolRight);
        var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        AddToolGroup(tools, new[] { "Select" }); tools.Children.Add(Divider());
        AddToolGroup(tools, new[] { "Mosaic", "Blur", "Solid" });
        AddToolGroup(tools, new[] { "Pen", "Highlight" });
        AddToolGroup(tools, new[] { "Arrow" });
        AddToolGroup(tools, new[] { "Rectangle", "Ellipse", "Line" });
        AddToolGroup(tools, new[] { "Text", "Number" });
        AddToolGroup(tools, new[] { "Crop" });
        toolPill = new Border { CornerRadius = new CornerRadius(7), Background = Ui.RedTint, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(), Opacity = 0, IsHitTestVisible = false };
        toolHost = new Grid(); toolHost.Children.Add(toolPill); toolHost.Children.Add(tools);
        toolHost.Loaded += (_, _) => MoveToolPill(false); toolHost.SizeChanged += (_, _) => MoveToolPill(false);
        toolRow.Children.Add(toolHost);
        top.Children.Add(new Border { Child = toolRow, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) });
        top.Children.Add(ToolOptions()); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition());
        detailsColumn = new ColumnDefinition { Width = new GridLength(detailsOpen ? HistoryPanelWidth : 0) }; body.ColumnDefinitions.Add(detailsColumn);
        if (surface.Parent is Border oldBorder) oldBorder.Child = null;
        var canvasBorder = new Border { Child = document == null ? null : surface, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28), Background = Brushes.Transparent };
        canvasHost = canvasBorder;
        canvasScroll = new ScrollViewer { Content = canvasBorder, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.Canvas };
        canvasScroll.SizeChanged += (_, _) => { if (currentPage == "editor" && fitView) Fit(); }; body.Children.Add(canvasScroll);
        // Overlays on the canvas area: QR bubbles beside their codes, the detection decision card at the top, transient feedback at the bottom.
        body.Children.Add(BuildCodeLayer()); body.Children.Add(BuildSensitiveCard()); body.Children.Add(BuildToastHost());
        canvasScroll.PreviewMouseWheel += (_, e) =>
        {
            if (document == null || Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true; SetZoom(e.Delta > 0 ? zoom * 1.25 : zoom / 1.25);
        };
        if (document == null)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(new EmptyIllustration(300, 150) { Margin = new Thickness(0, 0, 0, 12) });
            var label = Ui.Label("캡처하거나 이미지를 열어 편집을 시작하세요.", 14, Ui.Text); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 0, 0, 16); empty.Children.Add(label);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var start = Ui.CommandButton("새 캡처", "capture", async () => await StartCapture(CaptureMode.Region), true); start.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(start);
            buttons.Children.Add(Ui.CommandButton("이미지 열기", "open", OpenDialog)); empty.Children.Add(buttons);
            body.Children.Add(empty);
        }
        detailsPanel = RecentPanel(); detailsPanel.Visibility = detailsOpen ? Visibility.Visible : Visibility.Collapsed; Grid.SetColumn(detailsPanel, 1); body.Children.Add(detailsPanel); editorProperties = detailsPanel; root.Children.Add(body);
        undoButton.IsEnabled = document?.CanUndo == true; redoButton.IsEnabled = document?.CanRedo == true;
        RefreshToolButtons(false); RefreshEditor(); UpdateToolProperties(false); UpdateZoomInfo(); return root;
    }
    private void AddToolGroup(Panel panel, string[] ids)
    {
        ToolGroupView? group = null;
        var main = Ui.ToolButton(ToolName(ids[0]), EditTools.First(x => x.Id == ids[0]).Icon, () => SelectTool(group!.Current));
        if (ids.Length > 1) main.Padding = new Thickness(9, 0, 3, 0);
        Button? dropdown = null;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(main);
        if (ids.Length > 1)
        {
            dropdown = MenuButton(ToolName(ids[0]) + " 종류", "chevron-down", () =>
            {
                var menu = NewMenu();
                foreach (var id in ids)
                {
                    var shortcut = ToolShortcut(id);
                    var item = MenuAction(ToolName(id), () => SelectTool(id), shortcut.Length > 0 ? shortcut : null);
                    item.IsCheckable = true; item.IsChecked = surface.Tool == id; menu.Items.Add(item);
                }
                return menu;
            });
            dropdown.Width = 20; dropdown.Height = 32; dropdown.Padding = new Thickness(2, 0, 4, 0); dropdown.IsEnabled = document != null;
            row.Children.Add(dropdown);
        }
        // One surface per family keeps the tool and its variant arrow visually joined and wrapping together.
        var surfaceBorder = new Border { Child = row, CornerRadius = new CornerRadius(7), Margin = new Thickness(1, 6, 1, 6), Background = Brushes.Transparent };
        group = new ToolGroupView(surfaceBorder, main, dropdown, ids); main.IsEnabled = document != null; toolGroups.Add(group);
        panel.Children.Add(surfaceBorder);
    }
    private void RefreshToolButtons(bool animate = true)
    {
        foreach (var group in toolGroups)
        {
            var selected = group.Tools.Contains(surface.Tool);
            if (selected) group.Current = surface.Tool;
            var (_, label, icon, _) = EditTools.First(x => x.Id == group.Current);
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(Ui.Icon(icon, 18));
            var text = new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(7, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
            text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = group.Main }); row.Children.Add(text);
            group.Main.Content = row; AutomationProperties.SetName(group.Main, label);
            group.Main.ToolTip = ToolTipText(group.Current);
            // Without an image nothing can be edited, so no tool is shown as active.
            var active = selected && document != null;
            Ui.SetGhost(group.Main, active, animate);
            if (group.Dropdown != null) Ui.SetGhost(group.Dropdown, active, animate);
        }
        MoveToolPill(animate);
    }
    private void MoveToolPill(bool animate)
    {
        if (toolPill == null || toolHost == null) return;
        var group = document == null ? null : toolGroups.FirstOrDefault(x => x.Tools.Contains(surface.Tool));
        if (group == null || !group.Surface.IsLoaded || group.Surface.ActualWidth <= 0) { Motion.Animate(toolPill, OpacityProperty, 0, animate ? 120 : 0); return; }
        var origin = group.Surface.TransformToAncestor(toolHost).Transform(new Point());
        var slide = (TranslateTransform)toolPill.RenderTransform;
        // The first placement and layout changes jump; only a tool change glides.
        var duration = animate && toolPill.Opacity > 0 ? 200 : 0;
        Motion.Animate(slide, TranslateTransform.XProperty, origin.X, duration);
        Motion.Animate(slide, TranslateTransform.YProperty, origin.Y, duration);
        Motion.Animate(toolPill, WidthProperty, group.Surface.ActualWidth, duration);
        Motion.Animate(toolPill, HeightProperty, group.Surface.ActualHeight, duration);
        Motion.Animate(toolPill, OpacityProperty, 1, animate ? 120 : 0);
    }
    private void SetDetailsButton(bool animate)
    {
        if (detailsButton == null) return;
        if (detailsOpen) Ui.SetSelected(detailsButton, true, animate); else Ui.SetGhost(detailsButton, false, animate);
        detailsButton.ToolTip = detailsOpen ? "기록 접기" : "기록 열기";
    }
    private void ToggleDetails()
    {
        if (detailsPanel == null || detailsColumn == null || canvasScroll == null) return;
        surface.CompletePendingEdit();
        var viewer = canvasScroll; var horizontal = viewer.HorizontalOffset; var vertical = viewer.VerticalOffset;
        detailsOpen = !detailsOpen; detailsColumn.Width = new GridLength(detailsOpen ? HistoryPanelWidth : 0);
        detailsPanel.Visibility = detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (detailsOpen) { if (historyStale) RefreshHistory(); Motion.Enter(detailsPanel, x: 8, y: 0, duration: 160); }
        SetDetailsButton(true);
        if (!fitView) Dispatcher.BeginInvoke(() => { viewer.ScrollToHorizontalOffset(horizontal); viewer.ScrollToVerticalOffset(vertical); });
    }
    private UIElement ToolOptions()
    {
        toolProperties = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        toolTitle = Ui.Label(ToolName(surface.Tool), 12, Ui.Text, FontWeights.SemiBold);
        toolHint = Ui.Label("", 12, Ui.Muted); toolHint.TextWrapping = TextWrapping.NoWrap;
        TextBlock Caption(string text, double right = 8) { var caption = Ui.Label(text, 12, Ui.Muted); caption.TextWrapping = TextWrapping.NoWrap; caption.Margin = new Thickness(0, 0, right, 0); return caption; }
        maskProperties = new StackPanel { Orientation = Orientation.Horizontal }; toolProperties.Children.Add(maskProperties);
        strengthText = Caption("", 10); maskProperties.Children.Add(strengthText);
        strengthSlider = new Slider { Minimum = 4, Maximum = 48, Value = surface.Strength, TickFrequency = 2, IsSnapToTickEnabled = true, Width = 150, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(strengthSlider, "모자이크 및 블러 크기");
        strengthSlider.ValueChanged += (_, _) => { if (updating) return; surface.Strength = (int)strengthSlider.Value; UpdateStrengthLabel(); };
        strengthSlider.PreviewMouseLeftButtonUp += (_, _) => ApplyStrength(); strengthSlider.PreviewKeyUp += (_, _) => ApplyStrength(); maskProperties.Children.Add(strengthSlider);
        strengthValue = Ui.Label("", 12, Ui.Text); strengthValue.Width = 44; strengthValue.TextWrapping = TextWrapping.NoWrap; maskProperties.Children.Add(strengthValue);
        strokeProperties = new StackPanel { Orientation = Orientation.Horizontal }; toolProperties.Children.Add(strokeProperties);
        strokeCaption = Caption("두께"); strokeProperties.Children.Add(strokeCaption);
        var stroke = Ui.Choice(StrokeLabels, Math.Max(0, Array.IndexOf(new[] { 2d, 4d, 8d, 12d }, surface.Stroke))); stroke.Width = 92; stroke.MinHeight = 28; stroke.Height = 28; stroke.FontSize = 12; stroke.Margin = new Thickness(0, 0, 18, 0);
        AutomationProperties.SetName(stroke, "주석 두께");
        stroke.SelectionChanged += (_, _) => { if (stroke.SelectedIndex >= 0) surface.Stroke = new[] { 2, 4, 8, 12 }[stroke.SelectedIndex]; }; strokeProperties.Children.Add(stroke);
        strokeChoice = stroke;
        strokeProperties.Children.Add(Caption("색", 6));
        inkSwatches.Clear();
        foreach (var (hex, name) in new[] { ("#EE202E", "빨강"), ("#FFCE32", "노랑"), ("#288FFF", "파랑"), ("#FFFFFF", "흰색"), ("#080809", "검정") })
        {
            var ink = (Color)ColorConverter.ConvertFromString(hex);
            var b = Ui.Button("", () => { surface.Ink = ink; RefreshInkSwatches(); });
            b.Width = 26; b.Height = 26; b.MinHeight = 26; b.Padding = new Thickness(3); b.Margin = new Thickness(0, 0, 2, 0);
            b.Background = Brushes.Transparent; b.BorderThickness = new Thickness(1.5);
            b.Content = new Border { Background = Ui.Brush(hex), CornerRadius = new CornerRadius(4), BorderBrush = Ui.Brush("#33080809"), BorderThickness = new Thickness(1), Width = 17, Height = 17 };
            b.ToolTip = name; AutomationProperties.SetName(b, "주석 색상 " + name);
            inkSwatches.Add((b, ink)); strokeProperties.Children.Add(b);
        }
        RefreshInkSwatches();
        // Rectangles and ellipses: outline only, or painted inside with the same color.
        var fillSegments = new StackPanel { Orientation = Orientation.Horizontal };
        Button FillSegment(string label, bool fill)
        {
            var button = Ui.Button(label, () => { surface.Fill = fill; RefreshFillSegments(); });
            button.MinHeight = 24; button.Height = 24; button.FontSize = 12; button.Padding = new Thickness(10, 0, 10, 0); button.Margin = new Thickness(0);
            AutomationProperties.SetName(button, "도형 " + label); fillSegments.Children.Add(button); return button;
        }
        fillOutline = FillSegment("테두리", false); fillSolid = FillSegment("채우기", true);
        fillProperties = new Border { Child = fillSegments, Background = Ui.Track, CornerRadius = new CornerRadius(7), Padding = new Thickness(2), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        strokeProperties.Children.Add(fillProperties); RefreshFillSegments(false);
        textProperties = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) }; toolProperties.Children.Add(textProperties);
        textProperties.Children.Add(Caption("내용"));
        var text = new TextBox { Text = surface.AnnotationText, Width = 180, MinHeight = 28, Height = 28, FontSize = 12, Padding = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center }; text.TextChanged += (_, _) => surface.AnnotationText = text.Text; AutomationProperties.SetName(text, "텍스트 내용"); textProperties.Children.Add(text);
        var fontCaption = Caption("글꼴"); fontCaption.Margin = new Thickness(14, 0, 8, 0); textProperties.Children.Add(fontCaption);
        var fonts = FontChoices();
        var font = Ui.Choice(fonts.Select(choice => choice.Name).ToArray(), Math.Max(0, Array.FindIndex(fonts, choice => choice.Source == surface.Font)));
        font.Width = 128; font.MinHeight = 28; font.Height = 28; font.FontSize = 12; AutomationProperties.SetName(font, "텍스트 글꼴");
        font.SelectionChanged += (_, _) => { if (font.SelectedIndex >= 0) surface.Font = fonts[font.SelectedIndex].Source; };
        textProperties.Children.Add(font);
        boldButton = Ui.Button("굵게", () => { surface.Bold = !surface.Bold; RefreshBoldButton(); });
        boldButton.MinHeight = 28; boldButton.Height = 28; boldButton.FontSize = 12; boldButton.Padding = new Thickness(10, 0, 10, 0); boldButton.Margin = new Thickness(6, 0, 0, 0);
        AutomationProperties.SetName(boldButton, "굵게"); textProperties.Children.Add(boldButton); RefreshBoldButton(false);
        toolProperties.Children.Add(toolHint);
        return new Border { Child = toolProperties, Height = 40, Background = Ui.Background, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 1), ClipToBounds = true };
    }
    private TextBlock? strokeCaption;
    private ComboBox? strokeChoice;
    // One setting, named for what it changes: line width for strokes, letter size for text and numbers.
    private static readonly string[] StrokeLabels = { "2 px", "4 px", "8 px", "12 px" };
    private static readonly string[] SizeLabels = { "작게", "보통", "크게", "아주 크게" };
    private void ShowStrokeAsSize(bool size)
    {
        if (strokeCaption != null) strokeCaption.Text = size ? "크기" : "두께";
        if (strokeChoice == null || ReferenceEquals(strokeChoice.ItemsSource, size ? SizeLabels : StrokeLabels)) return;
        var index = strokeChoice.SelectedIndex;
        strokeChoice.ItemsSource = size ? SizeLabels : StrokeLabels; strokeChoice.SelectedIndex = index;
        AutomationProperties.SetName(strokeChoice, size ? "글자 크기" : "주석 두께");
    }
    private Border? fillProperties;
    private Button? fillOutline, fillSolid, boldButton;
    private void RefreshFillSegments(bool animate = true)
    {
        if (fillOutline == null || fillSolid == null) return;
        Ui.SetSegment(fillOutline, !surface.Fill, animate); Ui.SetSegment(fillSolid, surface.Fill, animate);
    }
    private void RefreshBoldButton(bool animate = true)
    {
        if (boldButton == null) return;
        if (surface.Bold) Ui.SetSelected(boldButton, true, animate); else Ui.SetPrimary(boldButton, false, animate);
        boldButton.FontWeight = FontWeights.Bold;
        AutomationProperties.SetItemStatus(boldButton, surface.Bold ? "켜짐" : "꺼짐");
    }
    // Common Windows fonts first, then every other installed family by its Korean name where it has one.
    private static (string Source, string Name)[]? fontChoices;
    private static (string Source, string Name)[] FontChoices()
    {
        if (fontChoices != null) return fontChoices;
        var korean = System.Windows.Markup.XmlLanguage.GetLanguage("ko-kr");
        string Name(FontFamily family) => family.FamilyNames.TryGetValue(korean, out var name) ? name : family.Source;
        var installed = Fonts.SystemFontFamilies.Where(family => !family.Source.StartsWith('@')).GroupBy(family => family.Source, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        var preferred = new[] { "Malgun Gothic", "Gulim", "Dotum", "Batang", "Gungsuh", "Segoe UI", "Arial", "Times New Roman", "Consolas" };
        var top = preferred.Select(source => installed.FirstOrDefault(family => string.Equals(family.Source, source, StringComparison.OrdinalIgnoreCase))).OfType<FontFamily>().Select(family => (family.Source, Name(family)));
        var rest = installed.Where(family => !preferred.Contains(family.Source, StringComparer.OrdinalIgnoreCase)).Select(family => (family.Source, Name(family))).OrderBy(choice => choice.Item2, StringComparer.CurrentCulture);
        fontChoices = top.Concat(rest).ToArray();
        if (fontChoices.Length == 0) fontChoices = [("Segoe UI", "Segoe UI")];
        return fontChoices;
    }
    private void RefreshInkSwatches()
    {
        foreach (var (swatch, ink) in inkSwatches)
        {
            var selected = surface.Ink == ink;
            swatch.BorderBrush = selected ? Ui.Primary : Brushes.Transparent;
            AutomationProperties.SetItemStatus(swatch, selected ? "선택됨" : "");
        }
    }
    private FrameworkElement RecentPanel()
    {
        sideTabs = new TabControl { BorderThickness = new Thickness(0), Background = Ui.Panel, Padding = new Thickness(0) };
        AutomationProperties.SetName(sideTabs, "기록과 편집 목록");
        var recent = new TabItem { Header = "기록", Content = HistoryPanel() }; sideTabs.Items.Add(recent);
        var edits = new DockPanel { Margin = new Thickness(12, 12, 12, 10) };
        var delete = Ui.CommandButton("선택한 편집 삭제", "delete", () => surface.DeleteSelection()); delete.Margin = new Thickness(0, 10, 0, 0); delete.ToolTip = "선택한 편집 삭제 (Del)"; DockPanel.SetDock(delete, Dock.Bottom); edits.Children.Add(delete);
        operations = new ListBox(); operations.SelectionChanged += (_, _) => { if (!updating && operations.SelectedItem is ListBoxItem item && item.Tag is Guid id) surface.Select(id); }; edits.Children.Add(operations);
        sideTabs.Items.Add(new TabItem { Header = "편집 목록", Content = edits }); RefreshHistory();
        return new Border { Child = sideTabs, Background = Ui.Panel, BorderBrush = Ui.Line, BorderThickness = new Thickness(1, 0, 0, 0) };
    }
}
