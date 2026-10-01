using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DamaCapture.Capture;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture;

internal sealed partial class MainWindow
{
    private readonly HistorySnapshotWriter historyWriter;
    private readonly DispatcherTimer historySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private Guid? currentHistoryId;
    private bool closingHistory;
    private TextBox? historySearch;
    private DateTime? historyDay;
    private string historyPeriod = "all", historyApplication = "";
    private string historyQuery = "", historyKind = "all";
    private readonly Dictionary<string, Button> historyKindButtons = new();
    private Button? historyDateButton, historyExpandButton;
    private Button? historyAppPicker;
    private bool historyExpanded;
    private TextBlock? historyCount;
    private Button? historyPrevious, historyNext;
    private int historyPage;
    private const int HistoryPageSize = 25;
    private bool historyStale;
    private readonly DispatcherTimer historySearchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    // Decoded thumbnails keyed by entry; a row reuses one while its PNG is unchanged.
    private readonly Dictionary<Guid, (string Path, DateTime Written, int Width, BitmapSource Image)> thumbnails = new();
    private const int ThumbnailCacheLimit = 300;
    // Selection mode: rows toggle instead of opening, and 모두 선택 covers every record matching the current filters.
    private bool historySelecting;
    private readonly HashSet<Guid> historySelection = new();
    private List<HistoryEntry> historyMatches = new();
    private readonly Dictionary<Guid, (CheckBox Check, Button Row, bool Current)> historyRows = new();
    private Button? historySelectButton, historySelectAll, historyDeleteSelected;
    private TextBlock? historySelectionCount;
    private FrameworkElement? historySelectionBar;
    private double HistoryPanelWidth => historyExpanded ? Math.Clamp(ActualWidth - 340, 400, 660) : 334;

    private void RecordCaptureHistory(CaptureContext? context)
    {
        if (document == null) return;
        context ??= new CaptureContext(DateTimeOffset.Now, "확인할 수 없는 창", "알 수 없는 앱", "Region");
        var entry = history.RecordCapture(context.CapturedAt, context.WindowTitle, context.ApplicationName,
            context.Mode, document.Width, document.Height);
        currentHistoryId = entry.Id; historyPage = 0;
        RememberCurrentDocument();
        PreserveHistoryImage();
        if (history.LastError != null) Notify("기록을 저장하지 못했습니다. 이번 실행 동안은 유지됩니다.");
        ScheduleHistoryTrim();
    }

    /// <summary>Runs the automatic cleanup once the current work has settled, so a capture never waits for it.</summary>
    private void ScheduleHistoryTrim(bool announce = false)
    {
        if (settings.HistoryRetentionDays <= 0 && settings.HistoryRetentionCount <= 0) return;
        Dispatcher.BeginInvoke(() => _ = TrimHistoryAsync(announce), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Deletes records past the retention the user chose, captures and copies together, through the same path as
    /// a manual delete: only app-managed PNGs and records go, never exported files. The record being edited stays.
    /// </summary>
    private async Task<int> TrimHistoryAsync(bool announce)
    {
        if (removingHistory || closingHistory || exiting) return 0;
        var expired = HistoryStore.Expired(history.Entries.Concat(clipboardHistory.Entries), settings.HistoryRetentionDays, settings.HistoryRetentionCount, DateTimeOffset.Now, currentHistoryId);
        if (expired.Count == 0) return 0;
        var deleted = await DeleteHistoryEntriesAsync(expired);
        if (deleted > 0 && announce) Notify($"오래된 기록 {deleted:N0}개를 정리했습니다.");
        return deleted;
    }

    private void ScheduleHistoryImage()
    {
        if (currentHistoryId is not Guid id || document == null || !AutoPreservesCurrent) return;
        if (queuedDocuments.TryGetValue(id, out var queued) && IsCurrentRevision(queued)) return;
        historySaveTimer.Stop(); historySaveTimer.Start();
    }

    private void PreserveHistoryImage()
    {
        historySaveTimer.Stop();
        if (currentHistoryId is not Guid id || document == null || !AutoPreservesCurrent) return;
        if (queuedDocuments.TryGetValue(id, out var queued) && IsCurrentRevision(queued)) return;
        // Only composed pixels reach disk; live editing documents remain in the bounded session cache.
        var image = document.Render();
        if (!image.IsFrozen) image.Freeze();
        queuedDocuments[id] = (new WeakReference<ImageDocument>(document), document.Revision);
        WriterFor(id).Enqueue(id, image);
        RefreshPreservationState();
    }

    private void OpenHistory(HistoryEntry entry)
    {
        if (!entry.HasImage) { OpenClipboardPreview(entry); return; }
        if (currentHistoryId == entry.Id) { SetHistoryExpanded(false); ShowPage("editor"); return; }
        if (!ConfirmReplace()) return;
        Try(() =>
        {
            RememberCurrentDocument();
            if (sessionDocuments.TryGet(entry.Id, out var session))
            {
                historySaveTimer.Stop(); currentHistoryId = entry.Id;
                document = session.Document; documentName = session.Name; dirty = session.ExportDirty;
                surface.SetDocument(document); editorPage = null; fitView = true;
                historyExpanded = false;
                ShowPage("editor", true); RefreshEditor();
                Toast("편집을 이어갑니다");
                return;
            }
            var image = WriterFor(entry.Id).PendingOrFailed(entry.Id);
            var latest = FindHistory(entry.Id) ?? entry;
            if (image == null && File.Exists(latest.ImagePath)) image = ImageFiles.Load(latest.ImagePath);
            if (image == null) { Notify("이미지가 없는 기록입니다."); return; }
            SetImage(image); currentHistoryId = entry.Id;
            documentName = entry.IsClipboard ? "복사 이미지 " + CaptureTime(entry).ToString("MM.dd HH:mm:ss") : entry.CapturedAt != null ? "캡처 " + CaptureTime(entry).ToString("MM.dd HH:mm:ss") : entry.Name;
            flattenedHistory.Add(entry.Id);
            savedDocuments[entry.Id] = (new WeakReference<ImageDocument>(document!), document!.Revision);
            queuedDocuments[entry.Id] = (new WeakReference<ImageDocument>(document!), document!.Revision);
            dirty = false; historyExpanded = false; ShowPage("editor", true);
            Toast("기록에서 열었습니다. 이전 편집은 되돌릴 수 없습니다");
        });
    }

    private void SetHistoryExpanded(bool expanded)
    {
        historyExpanded = expanded;
        if (detailsColumn != null && detailsOpen) detailsColumn.Width = new GridLength(HistoryPanelWidth);
        UpdateHistoryExpandButton();
        RefreshHistory();
        if (fitView) Dispatcher.BeginInvoke(Fit);
    }

    private void UpdateHistoryExpandButton()
    {
        if (historyExpandButton == null) return;
        var label = historyExpanded ? "좁게 보기" : "넓게 보기";
        historyExpandButton.Content = Ui.Icon(historyExpanded ? "narrow" : "widen", 16);
        historyExpandButton.ToolTip = label; AutomationProperties.SetName(historyExpandButton, "기록 " + label);
    }

    // Dropdown-style filter: current value on the left, arrow on the right.
    private static FrameworkElement FilterContent(string value)
    {
        var row = new DockPanel { LastChildFill = true };
        var arrow = Ui.Icon("chevron-down", 14, Ui.Muted); arrow.Margin = new Thickness(6, 1, 0, 0); DockPanel.SetDock(arrow, Dock.Right); row.Children.Add(arrow);
        var text = Ui.Label(value, 12); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(text);
        return row;
    }

    private FrameworkElement HistoryPanel()
    {
        var panel = new DockPanel { Margin = new Thickness(14, 12, 14, 8) };
        var filters = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        historyExpandButton = Ui.IconButton("넓게 보기", "widen", () => SetHistoryExpanded(!historyExpanded), compact: true);
        historyExpandButton.Width = 30; historyExpandButton.Height = 30; historyExpandButton.Padding = new Thickness(7);
        UpdateHistoryExpandButton();
        DockPanel.SetDock(historyExpandButton, Dock.Right); header.Children.Add(historyExpandButton);
        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        historyKindButtons.Clear();
        // Captures and each kind of copied content get their own view; 전체 keeps the combined timeline.
        foreach (var (key, label, name) in new[] { ("all", "전체", "전체 기록"), ("capture", "캡처", "캡처한 이미지"), ("text", "텍스트", "복사한 텍스트"), ("image", "이미지", "복사한 이미지"), ("files", "파일", "복사한 파일 목록") })
        {
            var button = Ui.Button(label, () => SetHistoryKind(key));
            button.MinHeight = 26; button.Height = 26; button.Padding = new Thickness(10, 0, 10, 0); button.Margin = new Thickness(0);
            Ui.SetSegment(button, historyKind == key, false);
            button.ToolTip = name; AutomationProperties.SetName(button, name); historyKindButtons[key] = button; kinds.Children.Add(button);
        }
        header.Children.Add(new Border { Child = kinds, Background = Ui.Track, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left });
        filters.Children.Add(header);
        historySearch = new TextBox { Text = historyQuery, MinHeight = 32, Padding = new Thickness(30, 0, 8, 0), ToolTip = "복사 내용, 창 제목, 앱 이름, 파일명 검색" };
        AutomationProperties.SetName(historySearch, "기록 검색");
        var searchGrid = new Grid(); searchGrid.Children.Add(historySearch);
        var searchIcon = Ui.Icon("search", 15, Ui.Muted); searchIcon.HorizontalAlignment = HorizontalAlignment.Left; searchIcon.Margin = new Thickness(10, 0, 0, 0); searchGrid.Children.Add(searchIcon);
        var placeholder = Ui.Label("내용, 창 이름, 앱 검색", 12, Ui.Muted); placeholder.TextWrapping = TextWrapping.NoWrap; placeholder.TextTrimming = TextTrimming.CharacterEllipsis; placeholder.Margin = new Thickness(32, 0, 0, 0); placeholder.IsHitTestVisible = false; placeholder.Visibility = historyQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed; searchGrid.Children.Add(placeholder);
        // Typing waits briefly before filtering so each keystroke does not rebuild the list.
        historySearchTimer.Tick += (_, _) => { historySearchTimer.Stop(); RefreshHistory(); };
        historySearch.TextChanged += (_, _) => { historyQuery = historySearch.Text; placeholder.Visibility = historyQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed; historyPage = 0; historySearchTimer.Stop(); historySearchTimer.Start(); };
        filters.Children.Add(searchGrid);
        var dateRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        dateRow.ColumnDefinitions.Add(new ColumnDefinition()); dateRow.ColumnDefinitions.Add(new ColumnDefinition());
        historyDateButton = Ui.Button(HistoryPeriodLabel(), ShowHistoryDateMenu);
        historyDateButton.Content = FilterContent(HistoryPeriodLabel());
        historyDateButton.Margin = new Thickness(0, 0, 6, 0); historyDateButton.MinHeight = 30; historyDateButton.Height = 30; historyDateButton.Padding = new Thickness(10, 0, 8, 0); historyDateButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(historyDateButton, "기록 기간"); dateRow.Children.Add(historyDateButton);
        historyAppPicker = Ui.Button("모든 앱", ShowHistoryAppMenu); historyAppPicker.MinHeight = 30; historyAppPicker.Height = 30; historyAppPicker.Margin = new Thickness(0); historyAppPicker.Padding = new Thickness(10, 0, 8, 0); historyAppPicker.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(historyAppPicker, "기록 앱 필터");
        Grid.SetColumn(historyAppPicker, 1); dateRow.Children.Add(historyAppPicker); filters.Children.Add(dateRow);
        DockPanel.SetDock(filters, Dock.Top); panel.Children.Add(filters);

        var pager = new DockPanel { Margin = new Thickness(2, 6, 0, 0), Height = 28 };
        var arrows = new StackPanel { Orientation = Orientation.Horizontal };
        historySelectButton = SmallHistoryButton(historySelecting ? "완료" : "선택", () => SetHistorySelecting(!historySelecting));
        historySelectButton.Margin = new Thickness(0, 0, 2, 0); arrows.Children.Add(historySelectButton);
        historyPrevious = Ui.IconButton("이전 기록 페이지", "chevron-left", () => { historyPage--; RefreshHistory(); }, compact: true);
        historyNext = Ui.IconButton("다음 기록 페이지", "chevron-right", () => { historyPage++; RefreshHistory(); }, compact: true);
        foreach (var button in new[] { historyPrevious, historyNext }) { button.Width = 28; button.Height = 28; button.Padding = new Thickness(6); arrows.Children.Add(button); }
        DockPanel.SetDock(arrows, Dock.Right); pager.Children.Add(arrows);
        historyCount = Ui.Label("", 12, Ui.Muted); historyCount.VerticalAlignment = VerticalAlignment.Center; pager.Children.Add(historyCount);
        DockPanel.SetDock(pager, Dock.Bottom); panel.Children.Add(pager);
        // Sits just above the pager while selecting.
        var selectionRow = new DockPanel { Height = 36 };
        var selectionActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        historySelectAll = SmallHistoryButton("모두 선택", ToggleSelectAllHistory); historySelectAll.Margin = new Thickness(0, 0, 6, 0); selectionActions.Children.Add(historySelectAll);
        historyDeleteSelected = SmallHistoryButton("삭제…", DeleteSelectedHistory); Ui.SetSelected(historyDeleteSelected, true, false); historyDeleteSelected.Padding = new Thickness(12, 0, 12, 0); selectionActions.Children.Add(historyDeleteSelected);
        DockPanel.SetDock(selectionActions, Dock.Right); selectionRow.Children.Add(selectionActions);
        historySelectionCount = Ui.Label("", 12, Ui.Text, FontWeights.SemiBold); historySelectionCount.VerticalAlignment = VerticalAlignment.Center; historySelectionCount.Margin = new Thickness(2, 0, 0, 0); selectionRow.Children.Add(historySelectionCount);
        historySelectionBar = new Border { Child = selectionRow, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 0), Visibility = historySelecting ? Visibility.Visible : Visibility.Collapsed };
        DockPanel.SetDock(historySelectionBar, Dock.Bottom); panel.Children.Add(historySelectionBar);
        recentFiles = new StackPanel();
        panel.Children.Add(new ScrollViewer { Content = recentFiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(-4, 0, -6, 0), Padding = new Thickness(4, 0, 2, 0) });
        return panel;
    }

    private static Button SmallHistoryButton(string label, Action action)
    {
        var button = Ui.Button(label, action); Ui.SetGhost(button, false, false);
        button.Height = 28; button.MinHeight = 28; button.FontSize = 12; button.Padding = new Thickness(8, 0, 8, 0);
        return button;
    }

    private void SetHistorySelecting(bool selecting)
    {
        historySelecting = selecting; historySelection.Clear();
        if (historySelectButton != null) { historySelectButton.Content = selecting ? "완료" : "선택"; AutomationProperties.SetName(historySelectButton, selecting ? "선택 끝내기" : "기록 선택"); }
        if (historySelectionBar != null) historySelectionBar.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        RefreshHistory();
    }

    private void ToggleHistorySelection(Guid id)
    {
        if (!historySelection.Remove(id)) historySelection.Add(id);
        if (historyRows.TryGetValue(id, out var row)) PaintHistoryRow(row.Check, row.Row, historySelection.Contains(id), row.Current);
        UpdateHistorySelectionBar();
    }

    private static void PaintHistoryRow(CheckBox check, Button row, bool selected, bool current)
    {
        check.IsChecked = selected;
        row.Background = selected ? Ui.RedTint : current ? Ui.Background : Brushes.Transparent;
    }

    private bool AllHistoryMatchesSelected => historyMatches.Count > 0 && historyMatches.All(entry => historySelection.Contains(entry.Id));

    private void ToggleSelectAllHistory()
    {
        if (AllHistoryMatchesSelected) historySelection.Clear();
        else historySelection.UnionWith(historyMatches.Select(entry => entry.Id));
        RefreshHistory();
    }

    private void UpdateHistorySelectionBar()
    {
        if (historySelectionCount == null || historySelectAll == null || historyDeleteSelected == null) return;
        historySelectionCount.Text = $"{historySelection.Count:N0}개 선택";
        historySelectionCount.Foreground = historySelection.Count == 0 ? Ui.Muted : Ui.Text;
        historySelectAll.Content = AllHistoryMatchesSelected ? "선택 해제" : "모두 선택";
        historySelectAll.IsEnabled = historyMatches.Count > 0;
        historyDeleteSelected.IsEnabled = historySelection.Count > 0;
    }

    private async void DeleteSelectedHistory()
    {
        if (removingHistory) return;
        var targets = historyMatches.Where(entry => historySelection.Contains(entry.Id)).ToList();
        if (targets.Count == 0) return;
        var message = $"기록 {targets.Count:N0}개를 삭제할까요?" +
            (targets.Any(entry => entry.HasImage) ? "\n기록 이미지도 함께 삭제합니다. 직접 저장한 파일은 유지됩니다." :
             targets.Any(entry => entry.Kind == HistoryKind.ClipboardFiles) ? "\n원본 파일은 유지됩니다." : "");
        if (MessageBox.Show(this, message, "기록 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var deleted = await DeleteHistoryEntriesAsync(targets.Select(entry => entry.Id).ToList());
        if (deleted == targets.Count) { SetHistorySelecting(false); Toast($"기록 {deleted:N0}개를 삭제했습니다"); }
    }

    /// <summary>
    /// Deletes each record through its writer, exactly as a single delete does: only the app-managed PNG and the
    /// record itself are removed. Returns how many were deleted; failures stay listed and selected.
    /// </summary>
    private async Task<int> DeleteHistoryEntriesAsync(IReadOnlyList<Guid> ids)
    {
        if (removingHistory || ids.Count == 0) return 0;
        removingHistory = true; IsEnabled = false; historySaveTimer.Stop();
        var deleted = 0;
        try
        {
            var jobs = ids.Select(id => (Id: id, Done: WriterFor(id).DeleteAsync(id))).ToList();
            await Task.WhenAll(jobs.Select(job => job.Done));
            foreach (var (id, done) in jobs)
                if (done.Result) { ForgetHistoryState(id); historySelection.Remove(id); deleted++; }
            if (deleted < ids.Count) Notify($"{ids.Count - deleted:N0}개는 삭제하지 못했습니다. " + (history.LastError ?? clipboardHistory.LastError));
        }
        catch (Exception ex) { Notify("삭제하지 못했습니다. " + ex.Message); }
        finally
        {
            removingHistory = false; IsEnabled = true;
            PreserveHistoryImage(); RefreshHistory(); RefreshPreservationState();
        }
        return deleted;
    }

    private void SetHistoryKind(string kind)
    {
        historyKind = kind; historyPage = 0;
        foreach (var (key, button) in historyKindButtons) Ui.SetSegment(button, key == kind);
        RefreshHistory();
    }

    private static bool MatchesKind(HistoryEntry entry, string kind) => kind switch
    {
        "capture" => !entry.IsClipboard,
        "text" => entry.Kind == HistoryKind.ClipboardText,
        "image" => entry.Kind == HistoryKind.ClipboardImage,
        "files" => entry.Kind == HistoryKind.ClipboardFiles,
        _ => true
    };

    // Leads each row's detail line so captures and copied images are distinguishable in the combined view.
    private static string KindLabel(HistoryEntry entry) => entry.Kind switch
    {
        HistoryKind.ClipboardText => "복사한 텍스트",
        HistoryKind.ClipboardImage => "복사한 이미지",
        HistoryKind.ClipboardFiles => $"복사한 파일 {entry.FilePaths.Length}개",
        _ => entry.CaptureMode is "Region" or "Window" or "FullScreen" or "AllScreens" or "FixedSize" or "Freehand" or "Element" or "LastRegion" or "Scroll"
            ? CaptureModeName(entry.CaptureMode) + " 캡처" : "캡처"
    };

    private string HistoryPeriodLabel() => historyPeriod switch { "today" => "오늘", "yesterday" => "어제", "week" => "최근 7일", "day" => historyDay?.ToString("yyyy.MM.dd") ?? "날짜 선택", _ => "전체 기간" };

    private void ShowHistoryAppMenu()
    {
        if (historyAppPicker == null) return;
        var menu = NewMenu();
        var apps = new[] { "" }.Concat(AllHistoryEntries.Select(e => e.ApplicationName).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().OrderBy(a => a));
        foreach (var app in apps)
        {
            var item = MenuAction(app.Length == 0 ? "모든 앱" : app, () => { historyApplication = app; historyPage = 0; RefreshHistory(); });
            item.IsCheckable = true; item.IsChecked = historyApplication == app; menu.Items.Add(item);
        }
        OpenMenu(historyAppPicker, menu);
    }

    private void ShowHistoryDateMenu()
    {
        if (historyDateButton == null) return;
        var menu = NewMenu();
        foreach (var (key, label) in new[] { ("all", "전체 기간"), ("today", "오늘"), ("yesterday", "어제"), ("week", "최근 7일") })
        {
            var item = MenuAction(label, () => SetHistoryPeriod(key)); item.IsCheckable = true; item.IsChecked = historyPeriod == key; menu.Items.Add(item);
        }
        menu.Items.Add(new Separator()); menu.Items.Add(MenuAction("날짜 선택…", () => Dispatcher.BeginInvoke(ShowHistoryCalendar)));
        OpenMenu(historyDateButton, menu);
    }

    private void SetHistoryPeriod(string period, DateTime? day = null)
    {
        historyPeriod = period; historyDay = day; historyPage = 0;
        if (historyDateButton != null) historyDateButton.Content = FilterContent(HistoryPeriodLabel());
        RefreshHistory();
    }

    private void ShowHistoryCalendar()
    {
        var calendar = new System.Windows.Controls.Calendar { DisplayDate = historyDay ?? DateTime.Today, SelectedDate = historyDay, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(calendar, "기록 날짜 선택");
        var popup = new Popup { PlacementTarget = historyDateButton, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Child = calendar, Background = Ui.Panel, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Padding = new Thickness(4) } };
        calendar.SelectedDatesChanged += (_, _) => { if (calendar.SelectedDate is DateTime day) { SetHistoryPeriod("day", day.Date); popup.IsOpen = false; } };
        calendar.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        popup.Closed += (_, _) => historyDateButton?.Focus();
        popup.IsOpen = true; calendar.Focus();
    }

    private static DateTime CaptureTime(HistoryEntry entry) => entry.CapturedAt?.LocalDateTime ?? entry.SavedAt.ToLocalTime();
    private static string CaptureModeName(string mode) => mode switch
    {
        "Region" => "영역", "Window" => "창", "FullScreen" => "전체 화면", "AllScreens" => "모든 모니터",
        "FixedSize" => "크기 지정", "Freehand" => "자유형", "Element" => "단위 영역", "LastRegion" => "이전 영역", "Scroll" => "스크롤", _ => "이미지"
    };
    /// <summary>Background events refresh a hidden panel lazily; it catches up when opened.</summary>
    private void RefreshHistoryIfVisible()
    {
        if (detailsOpen) RefreshHistory(); else historyStale = true;
    }

    private BitmapSource? Thumbnail(HistoryEntry entry, int decodeWidth)
    {
        if (!entry.HasImage || !File.Exists(entry.ImagePath)) return null;
        var written = File.GetLastWriteTimeUtc(entry.ImagePath);
        if (thumbnails.TryGetValue(entry.Id, out var cached) && cached.Path == entry.ImagePath && cached.Written == written && cached.Width == decodeWidth) return cached.Image;
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(entry.ImagePath); bitmap.EndInit(); bitmap.Freeze();
            if (thumbnails.Count >= ThumbnailCacheLimit) thumbnails.Clear();
            thumbnails[entry.Id] = (entry.ImagePath, written, decodeWidth, bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException or UriFormatException) { return null; }
    }

    private void RefreshHistory()
    {
        if (recentFiles == null || historySearch == null || historyCount == null) return;
        historySearchTimer.Stop(); historyStale = false;
        var query = historySearch.Text.Trim();
        var all = AllHistoryEntries;
        if (historyAppPicker != null)
        {
            if (!all.Any(e => e.ApplicationName == historyApplication)) historyApplication = "";
            historyAppPicker.Content = FilterContent(historyApplication.Length == 0 ? "모든 앱" : historyApplication);
            historyAppPicker.ToolTip = historyApplication.Length == 0 ? "앱으로 찾기" : historyApplication;
        }
        bool WithinPeriod(DateTime time) => historyPeriod switch
        {
            "today" => time.Date == DateTime.Today,
            "yesterday" => time.Date == DateTime.Today.AddDays(-1),
            "week" => time.Date >= DateTime.Today.AddDays(-6) && time.Date <= DateTime.Today,
            "day" => time.Date == historyDay?.Date,
            _ => true
        };
        var matches = all.Where(entry => MatchesKind(entry, historyKind) && WithinPeriod(CaptureTime(entry)) && (historyApplication.Length == 0 || entry.ApplicationName == historyApplication) &&
            (query.Length == 0 || (entry.WindowTitle + " " + entry.ApplicationName + " " + entry.Name + " " + entry.Text + " " + string.Join(" ", entry.FilePaths)).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        // A selection only ever covers what the current filters show, so 삭제 never reaches hidden records.
        historyMatches = matches; historyRows.Clear();
        if (historySelecting) historySelection.IntersectWith(matches.Select(entry => entry.Id));
        UpdateHistorySelectionBar();
        var pages = Math.Max(1, (matches.Count + HistoryPageSize - 1) / HistoryPageSize);
        historyPage = Math.Clamp(historyPage, 0, pages - 1);
        historyCount.Text = $"{matches.Count:N0}개" + (pages > 1 ? $"  ·  {historyPage + 1} / {pages} 페이지" : matches.Count < all.Count ? $"  ·  전체 {all.Count:N0}개" : "");
        historyCount.ToolTip = $"전체 {all.Count:N0}개";
        historyPrevious!.IsEnabled = historyPage > 0; historyNext!.IsEnabled = historyPage < pages - 1;
        historyPrevious.Visibility = historyNext.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        recentFiles.Children.Clear();
        if (matches.Count == 0)
        {
            var copyTab = historyKind is "text" or "image" or "files";
            var empty = Ui.Label(
                copyTab && !settings.ArchiveClipboard && !all.Any(e => MatchesKind(e, historyKind)) ? "복사 기록이 꺼져 있습니다.\n환경 설정 → 일반에서 켤 수 있습니다." :
                all.Count > 0 ? "조건에 맞는 기록이 없습니다." : settings.ArchiveClipboard ? "캡처하거나 복사한 내용이 여기에 모입니다." : "캡처한 이미지가 여기에 모입니다.", 12, Ui.Muted);
            empty.TextAlignment = TextAlignment.Center; empty.LineHeight = 19;
            empty.Margin = new Thickness(4, 24, 4, 0); empty.HorizontalAlignment = HorizontalAlignment.Center; recentFiles.Children.Add(empty); return;
        }
        DateTime? previousDate = null;
        foreach (var entry in matches.Skip(historyPage * HistoryPageSize).Take(HistoryPageSize))
        {
            var time = CaptureTime(entry);
            if (previousDate != time.Date)
            {
                var prefix = time.Date == DateTime.Today ? "오늘 · " : time.Date == DateTime.Today.AddDays(-1) ? "어제 · " : "";
                var heading = Ui.Label(prefix + time.ToString("yyyy.MM.dd (ddd)", CultureInfo.GetCultureInfo("ko-KR")), 12, Ui.Muted, FontWeights.SemiBold);
                heading.Margin = new Thickness(6, previousDate == null ? 2 : 14, 0, 6); recentFiles.Children.Add(heading); previousDate = time.Date;
            }
            var current = currentHistoryId == entry.Id;
            // The last column leaves room for the overlaid actions button; selection adds a check column first.
            var row = new Grid(); var first = historySelecting ? 1 : 0;
            CheckBox? check = null;
            if (historySelecting)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                check = new CheckBox { IsHitTestVisible = false, Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
                row.Children.Add(check);
            }
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(historyExpanded ? 146 : 76) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(historySelecting ? 4 : 26) });
            var thumbnail = WriterFor(entry.Id).PendingOrFailed(entry.Id) ?? Thumbnail(entry, historyExpanded ? 288 : 160);
            var preview = new Border { Width = historyExpanded ? 134 : 64, Height = historyExpanded ? 84 : 48, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Background, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true,
                Child = thumbnail != null ? new Image { Source = thumbnail, Stretch = Stretch.Uniform } : ToolbarIcons.Create(entry.Kind == HistoryKind.ClipboardText ? "text" : entry.Kind == HistoryKind.ClipboardFiles ? "open" : "image", 18, Ui.Muted) };
            Grid.SetColumn(preview, first); row.Children.Add(preview);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = entry.DisplayTitle;
            var caption = Ui.Label(title, 13, Ui.Text, FontWeights.SemiBold); caption.TextWrapping = TextWrapping.NoWrap; caption.TextTrimming = TextTrimming.CharacterEllipsis; text.Children.Add(caption);
            var app = Ui.Label(entry.CapturedAt != null ? KindLabel(entry) + " · " + entry.ApplicationName : "저장한 파일", 12, Ui.Muted);
            app.TextWrapping = TextWrapping.NoWrap; app.TextTrimming = TextTrimming.CharacterEllipsis; app.Margin = new Thickness(0, 2, 0, 1); text.Children.Add(app);
            var when = new TextBlock { FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            when.Inlines.Add(time.ToString("HH:mm:ss") + (historyExpanded && entry.Width > 0 ? $"  ·  {entry.Width} × {entry.Height}" : ""));
            if (current && !historyExpanded) when.Inlines.Add(new System.Windows.Documents.Run("  ·  편집 중") { Foreground = Ui.RedText, FontWeight = FontWeights.SemiBold });
            text.Children.Add(when);
            if (historyExpanded)
            {
                var state = Ui.Label(!entry.HasImage ? entry.WindowTitle : current ? "편집 중" : sessionDocuments.Contains(entry.Id) ? "편집 이어가기" : "기록 이미지", 12, current ? Ui.RedText : Ui.Muted, current ? FontWeights.SemiBold : null);
                state.TextWrapping = TextWrapping.NoWrap; state.TextTrimming = TextTrimming.CharacterEllipsis; state.Margin = new Thickness(0, 4, 0, 0); text.Children.Add(state);
            }
            text.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(text, first + 1); row.Children.Add(text);
            var item = Ui.Button("", () => { if (historySelecting) ToggleHistorySelection(entry.Id); else OpenHistory(entry); }); item.Content = row; item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            item.Padding = new Thickness(6, 7, 4, 7); item.Margin = new Thickness(0, 0, 0, 2); Ui.SetGhost(item, false, false);
            if (current) item.Background = Ui.Background;
            if (check != null)
            {
                historyRows[entry.Id] = (check, item, current);
                PaintHistoryRow(check, item, historySelection.Contains(entry.Id), current);
                recentFiles.Children.Add(item);
                AutomationProperties.SetName(item, $"{time:yyyy.MM.dd HH:mm:ss} {title} 선택");
                continue;
            }
            item.ToolTip = $"{time:yyyy.MM.dd HH:mm:ss}\n{entry.ApplicationName}\n{title}" + (string.IsNullOrEmpty(entry.Path) ? "" : "\n" + entry.Path);
            AutomationProperties.SetName(item, $"{time:yyyy.MM.dd HH:mm:ss} {title}");
            ContextMenu EntryMenu()
            {
                var menu = NewMenu(); menu.Items.Add(MenuAction("열기", () => OpenHistory(entry)));
                menu.Items.Add(MenuAction("다시 복사", () => CopyHistoryEntry(entry)));
                if (WriterFor(entry.Id).IsFailed(entry.Id)) menu.Items.Add(MenuAction("다시 기록", async () => await WriterFor(entry.Id).FlushAsync()));
                menu.Items.Add(new Separator());
                if (!entry.IsClipboard) menu.Items.Add(MenuAction("숨기기", () => HideHistoryEntry(entry)));
                menu.Items.Add(MenuAction("삭제…", () => DeleteHistoryEntry(entry)));
                return menu;
            }
            item.ContextMenu = EntryMenu();
            var container = new Grid();
            var more = MenuButton("기록 작업", "more", EntryMenu); more.VerticalAlignment = VerticalAlignment.Center; more.HorizontalAlignment = HorizontalAlignment.Right;
            more.Width = 26; more.Height = 28; more.Padding = new Thickness(5); more.Margin = new Thickness(0, 0, 4, 2); more.Foreground = Ui.Muted;
            container.Children.Add(item); container.Children.Add(more);
            recentFiles.Children.Add(container);
        }
    }
}
