using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DamaCapture.Services;
using Forms = System.Windows.Forms;

namespace DamaCapture;

internal sealed partial class MainWindow
{
    private void ShowAbout() => ShowSettings(openAbout: true);
    private void ShowSettings() => ShowSettings(openAbout: false);
    private void ShowSettings(bool openAbout)
    {
        surface.CompletePendingEdit();
        PreserveHistoryImage();
        var dialog = new Window
        {
            Title = "환경 설정", Owner = this, Icon = Icon, Width = 540, Height = 580,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Ui.Background, Foreground = Ui.Text, FontSize = 12
        };
        var layout = new Grid { Margin = new Thickness(14) };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tabs = new TabControl { Background = Ui.Panel, BorderBrush = Ui.Line, Padding = new Thickness(20, 16, 20, 16) };
        layout.Children.Add(tabs);

        StackPanel AddTab(string title)
        {
            var panel = new StackPanel();
            tabs.Items.Add(new TabItem { Header = title, Content = panel, Padding = new Thickness(14, 9, 14, 9) });
            return panel;
        }
        static CheckBox Check(string label, bool value) => new()
        {
            Content = label, IsChecked = value, Margin = new Thickness(0, 5, 0, 7),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        static Grid Row(string label, FrameworkElement input, double labelWidth = 110)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 14), MinHeight = 28 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Ui.Label(label, 12));
            Grid.SetColumn(input, 1); row.Children.Add(input);
            return row;
        }
        void Error(string message, int tabIndex, Control field)
        {
            tabs.SelectedIndex = tabIndex;
            MessageBox.Show(dialog, message, "환경 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
            field.Focus();
            if (field is TextBox textBox) textBox.SelectAll();
        }

        var general = AddTab("일반");
        general.Children.Add(Ui.Section("창", first: true));
        var trayCheck = Check("닫으면 트레이로", settings.CloseToTray);
        general.Children.Add(trayCheck);
        general.Children.Add(Ui.Section("기록"));
        var keep = Check("기록 유지", settings.KeepHistory);
        var keepImages = Check("이미지도 기록", settings.KeepCaptureImages);
        keepImages.Margin = new Thickness(26, 2, 0, 7);
        keepImages.IsEnabled = keep.IsChecked == true;
        keep.Click += (_, _) => keepImages.IsEnabled = keep.IsChecked == true;
        general.Children.Add(keep); general.Children.Add(keepImages);
        var archiveClipboard = Check("복사 기록", settings.ArchiveClipboard);
        archiveClipboard.Margin = new Thickness(0, 8, 0, 3);
        System.Windows.Automation.AutomationProperties.SetHelpText(archiveClipboard, "켜둔 동안 복사한 텍스트·이미지·파일 목록을 그대로 기록합니다. 끄면 새 기록만 멈춥니다.");
        general.Children.Add(archiveClipboard);
        var clipboardHelp = Ui.Label("켜둔 동안 복사한 텍스트·이미지·파일 목록을 그대로 기록합니다(비밀번호 포함).", 12, Ui.Muted);
        clipboardHelp.Margin = new Thickness(26, 0, 0, 0); clipboardHelp.LineHeight = 19; general.Children.Add(clipboardHelp);
        var cleanup = Ui.Button("남은 기록 이미지 정리…", () =>
        {
            var orphans = history.OrphanImages().Count + clipboardHistory.OrphanImages().Count;
            if (orphans == 0) { MessageBox.Show(dialog, "정리할 이미지가 없습니다.", "기록 이미지 정리", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (MessageBox.Show(dialog, $"숨기거나 기록을 끈 뒤 남은 이미지 {orphans:N0}개를 삭제할까요?\n직접 저장한 파일은 삭제하지 않습니다.", "기록 이미지 정리", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var deleted = history.DeleteOrphanImages(out var failed) + clipboardHistory.DeleteOrphanImages(out var failedClipboard);
            failed += failedClipboard;
            MessageBox.Show(dialog, $"이미지 {deleted:N0}개를 삭제했습니다." + (failed > 0 ? $"\n{failed:N0}개는 삭제하지 못했습니다. " + (history.LastError ?? clipboardHistory.LastError) : ""), "기록 이미지 정리", MessageBoxButton.OK, failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        });
        cleanup.HorizontalAlignment = HorizontalAlignment.Left; cleanup.Margin = new Thickness(0, 8, 0, 0); cleanup.Height = 30; cleanup.Padding = new Thickness(10, 0, 10, 0);
        general.Children.Add(cleanup);
        general.Children.Add(Ui.Section("화면"));
        var reduceMotion = Check("모션 줄이기", settings.ReduceMotion);
        general.Children.Add(reduceMotion);

        var captureTab = AddTab("캡처");
        var cursor = Check("커서 포함", settings.IncludeCursor);
        cursor.Margin = new Thickness(0, 0, 0, 16);
        captureTab.Children.Add(cursor);
        var delay = new TextBox { Text = settings.DelaySeconds.ToString(), Width = 64, MaxLength = 2, HorizontalContentAlignment = HorizontalAlignment.Right };
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal };
        delayRow.Children.Add(delay);
        var seconds = Ui.Label("초", 12); seconds.Margin = new Thickness(7, 0, 0, 0); delayRow.Children.Add(seconds);
        captureTab.Children.Add(Row("캡처 지연", delayRow));
        var afterValues = new[] { "editor", "copy", "save" };
        var after = Ui.Choice(new[] { "복사하고 편집창 열기", "복사만", "저장 창 열기" }, Math.Max(0, Array.IndexOf(afterValues, settings.AfterCapture)));
        after.Width = 174; after.HorizontalAlignment = HorizontalAlignment.Left;
        captureTab.Children.Add(Row("캡처 후 동작", after));
        var detect = Check("민감 정보 감지", settings.DetectSensitive);
        captureTab.Children.Add(detect);

        var saveTab = AddTab("저장");
        var folderLabel = Ui.Label("저장 폴더 *필수", 12); folderLabel.Margin = new Thickness(0, 0, 0, 8); saveTab.Children.Add(folderLabel);
        var folder = new TextBox { Text = settings.SaveFolder };
        var folderRow = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition());
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(folder);
        var browse = Ui.Button("찾아보기…", () =>
        {
            using var picker = new Forms.FolderBrowserDialog
            {
                Description = "기본 저장 폴더", UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : settings.SaveFolder
            };
            if (picker.ShowDialog(new SettingsFolderOwner(new WindowInteropHelper(dialog).Handle)) == Forms.DialogResult.OK)
                folder.Text = picker.SelectedPath;
        });
        browse.Margin = new Thickness(7, 0, 0, 0); browse.Padding = new Thickness(10, 4, 10, 4);
        Grid.SetColumn(browse, 1); folderRow.Children.Add(browse); saveTab.Children.Add(folderRow);
        var quality = new Slider { Minimum = 1, Maximum = 100, Value = settings.JpegQuality, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0) };
        var qualityText = Ui.Label(settings.JpegQuality + "%", 12); qualityText.Width = 38; qualityText.TextAlignment = TextAlignment.Right;
        quality.ValueChanged += (_, _) => qualityText.Text = (int)quality.Value + "%";
        var qualityRow = new Grid(); qualityRow.ColumnDefinitions.Add(new ColumnDefinition()); qualityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        qualityRow.Children.Add(quality); Grid.SetColumn(qualityText, 1); qualityRow.Children.Add(qualityText);
        saveTab.Children.Add(Row("JPEG 품질", qualityRow));

        var hotkeyTab = AddTab("단축키");
        var keys = Enumerable.Range('0', 10).Select(value => ((char)value).ToString())
            .Concat(Enumerable.Range('A', 26).Select(value => ((char)value).ToString()))
            .Concat(Enumerable.Range(1, 12).Select(value => "F" + value)).ToArray();
        static string DisplayKey(uint key) => key is >= 0x70 and <= 0x7B ? "F" + (key - 0x70 + 1) : ((char)key).ToString();
        static uint VirtualKey(string key) => key.Length > 1 ? (uint)(0x70 + int.Parse(key[1..]) - 1) : key[0];
        ComboBox HotkeyRow(string label, uint key)
        {
            var choice = Ui.Choice(keys, Math.Max(0, Array.IndexOf(keys, DisplayKey(key))));
            choice.Width = 82;
            var combination = new StackPanel { Orientation = Orientation.Horizontal };
            var prefix = Ui.Label("Ctrl + Shift +", 12); prefix.Width = 92; combination.Children.Add(prefix); combination.Children.Add(choice);
            hotkeyTab.Children.Add(Row(label, combination));
            return choice;
        }
        var regionKey = HotkeyRow("영역 캡처", settings.RegionHotkey);
        var windowKey = HotkeyRow("창 캡처", settings.WindowHotkey);
        var scrollKey = HotkeyRow("스크롤 캡처", settings.ScrollHotkey);

        var aboutTab = AddTab("정보");
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var product = Ui.Label($"담아 {version?.Major}.{version?.Minor}.{version?.Build}", 16, Ui.Text, FontWeights.Bold);
        product.Margin = new Thickness(0, 0, 0, 18); aboutTab.Children.Add(product);
        aboutTab.Children.Add(Row("만든 사람", Ui.Label("Nimdal", 12)));
        FrameworkElement Link(string text, string target)
        {
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text)) { Foreground = Ui.Text };
            link.Click += (_, _) => OpenBrowser(target);
            System.Windows.Automation.AutomationProperties.SetName(link, text);
            return new TextBlock(link) { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        }
        aboutTab.Children.Add(Row("이메일", Link("0xnimdal@gmail.com", "mailto:0xnimdal@gmail.com")));
        aboutTab.Children.Add(Row("X", Link("@0xnimdal", "https://x.com/0xnimdal")));
        aboutTab.Children.Add(Row("Telegram", Link("@nimdal", "https://t.me/nimdal")));
        aboutTab.Children.Add(Row("소스", Link("github.com/nimdalkr/damacapture", "https://github.com/nimdalkr/damacapture")));
        aboutTab.Children.Add(Row("라이선스", Ui.Label("GPL-3.0", 12)));
        if (openAbout) tabs.SelectedItem = tabs.Items[^1];

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(footer, 1); layout.Children.Add(footer);
        var confirm = Ui.Button("확인", async () =>
        {
            if (!int.TryParse(delay.Text, out var delaySeconds) || delaySeconds is < 0 or > 30)
            {
                Error("캡처 지연은 0부터 30초까지 입력하세요.", 1, delay); return;
            }
            string fullFolder;
            try
            {
                if (string.IsNullOrWhiteSpace(folder.Text) || !Path.IsPathFullyQualified(folder.Text))
                {
                    Error("기본 저장 폴더의 전체 경로를 입력하세요.", 2, folder); return;
                }
                fullFolder = Path.GetFullPath(folder.Text);
                if (File.Exists(fullFolder)) { Error("파일 대신 폴더 경로를 입력하세요.", 2, folder); return; }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Error("올바른 폴더 경로를 입력하세요.", 2, folder); return;
            }
            var r = VirtualKey((string)regionKey.SelectedItem);
            var w = VirtualKey((string)windowKey.SelectedItem);
            var s = VirtualKey((string)scrollKey.SelectedItem);
            if (new[] { r, w, s }.Distinct().Count() != 3)
            {
                Error("영역·창·스크롤 캡처에 서로 다른 단축키를 지정하세요.", 3, r == w ? windowKey : scrollKey); return;
            }

            var previous = AppSettings.Validated(settings);
            var candidate = AppSettings.Validated(settings);
            var deleteImages = false;
            if (previous.KeepHistory && keep.IsChecked != true)
            {
                // Turning retention off tombstones every stored record; say so before it happens.
                var stored = history.Entries.Count; var images = history.ManagedImageCount();
                var answer = MessageBox.Show(dialog,
                    $"기록 유지를 끄면 저장된 캡처 기록 {stored:N0}개가 목록에서 사라지고, 다시 켜도 돌아오지 않습니다.\n" +
                    $"기록 이미지 파일 {images:N0}개도 함께 삭제할까요?\n\n예: 기록과 이미지를 삭제합니다.\n아니요: 기록만 제외하고 이미지 파일은 남깁니다.",
                    "기록 유지 끄기", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (answer == MessageBoxResult.Cancel) { keep.Focus(); return; }
                deleteImages = answer == MessageBoxResult.Yes;
            }
            candidate.CloseToTray = trayCheck.IsChecked == true;
            candidate.KeepHistory = keep.IsChecked == true;
            candidate.KeepCaptureImages = keepImages.IsChecked == true;
            candidate.ArchiveClipboard = archiveClipboard.IsChecked == true;
            candidate.HistoryVersion = 1;
            candidate.ReduceMotion = reduceMotion.IsChecked == true;
            candidate.IncludeCursor = cursor.IsChecked == true;
            candidate.DelaySeconds = delaySeconds;
            candidate.AfterCapture = afterValues[Math.Max(0, after.SelectedIndex)];
            candidate.DetectSensitive = detect.IsChecked == true;
            candidate.SaveFolder = fullFolder;
            candidate.JpegQuality = (int)quality.Value;
            candidate.RegionHotkey = r; candidate.WindowHotkey = w; candidate.ScrollHotkey = s; candidate.HotkeyModifiers = 6;
            var keysChanged = previous.RegionHotkey != r || previous.WindowHotkey != w || previous.ScrollHotkey != s || previous.HotkeyModifiers != 6;
            var saved = false;
            dialog.IsEnabled = false;
            try
            {
                // Finish writes under the old policy before changing image retention.
                historySaveTimer.Stop();
                await FlushHistoryWritersAsync();
                if (historyWriter.HasFailures && previous.KeepHistory && !candidate.KeepHistory)
                {
                    MessageBox.Show(dialog, "기록하지 못한 이미지가 있습니다. 저장한 뒤 다시 변경하세요.", "기록", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (deleteImages)
                {
                    // Same path as deleting one record: the writer serializes it with any queued PNG.
                    foreach (var entry in history.Entries.Where(entry => entry.HasImage))
                        if (await historyWriter.DeleteAsync(entry.Id)) ForgetHistoryState(entry.Id);
                }
                CopySettings(candidate, settings);
                if (keysChanged && !demo && !RegisterHotkeys())
                {
                    CopySettings(previous, settings);
                    var restored = RegisterHotkeys();
                    Error("다른 앱에서 사용 중인 단축키가 있습니다. 조합을 바꿔주세요." +
                        (restored ? "" : "\n이전 단축키도 등록하지 못했습니다. 사용 중인 앱을 확인하세요."), 3, regionKey);
                    return;
                }
                settingsStore.Save(candidate); saved = true;
                history.SetPersistence(candidate.KeepHistory);
                if (history.LastError != null) throw new IOException(history.LastError);
                Motion.ReduceMotion = candidate.ReduceMotion;
                Notify("설정을 저장했습니다.");
                dialog.DialogResult = true;
            }
            catch (Exception ex)
            {
                CopySettings(previous, settings);
                var rollbackErrors = new List<string>();
                if (keysChanged && !demo)
                {
                    try { if (!RegisterHotkeys()) rollbackErrors.Add("이전 단축키를 등록하지 못했습니다."); }
                    catch { rollbackErrors.Add("이전 단축키를 등록하지 못했습니다."); }
                }
                if (saved)
                {
                    try { settingsStore.Save(previous); }
                    catch { rollbackErrors.Add("이전 설정 파일을 복원하지 못했습니다."); }
                }
                try { history.SetPersistence(previous.KeepHistory); if (history.LastError != null) rollbackErrors.Add("기록 상태를 완전히 복원하지 못했습니다."); }
                catch { rollbackErrors.Add("기록 설정을 복원하지 못했습니다."); }
                Motion.ReduceMotion = previous.ReduceMotion;
                MessageBox.Show(dialog, "설정을 저장하지 못했습니다.\n" + ex.Message +
                    (rollbackErrors.Count > 0 ? "\n\n" + string.Join("\n", rollbackErrors) : ""),
                    "환경 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { dialog.IsEnabled = true; }
        });
        confirm.Width = 80; confirm.Height = 32; confirm.IsDefault = true; confirm.Margin = new Thickness(0, 0, 8, 0); Ui.SetPrimary(confirm, true, false);
        var cancel = Ui.Button("취소", () => dialog.DialogResult = false);
        cancel.Width = 80; cancel.Height = 32; cancel.IsCancel = true; cancel.Margin = new Thickness(0);
        footer.Children.Add(confirm); footer.Children.Add(cancel);
        dialog.Content = layout;
        Motion.WhenLoadedEnter(layout, y: 4, duration: 160);
        if (dialog.ShowDialog() == true) ApplyClipboardSetting();
        PreserveHistoryImage(); RefreshPreservationState();
        RefreshCaptureOptions();
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        target.CloseToTray = source.CloseToTray; target.KeepHistory = source.KeepHistory;
        target.KeepCaptureImages = source.KeepCaptureImages; target.HistoryVersion = source.HistoryVersion;
        target.ArchiveClipboard = source.ArchiveClipboard;
        target.ReduceMotion = source.ReduceMotion; target.IncludeCursor = source.IncludeCursor;
        target.DelaySeconds = source.DelaySeconds; target.AfterCapture = source.AfterCapture; target.DetectSensitive = source.DetectSensitive;
        target.SaveFolder = source.SaveFolder; target.JpegQuality = source.JpegQuality; target.MaxHistory = source.MaxHistory;
        target.RegionHotkey = source.RegionHotkey; target.WindowHotkey = source.WindowHotkey;
        target.ScrollHotkey = source.ScrollHotkey; target.HotkeyModifiers = source.HotkeyModifiers;
    }

    private sealed class SettingsFolderOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
