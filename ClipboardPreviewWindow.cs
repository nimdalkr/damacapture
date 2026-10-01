using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DamaCapture.Services;

namespace DamaCapture;

internal sealed class ClipboardPreviewWindow : Window
{
    public ClipboardPreviewWindow(Window owner, HistoryEntry entry, Func<bool> copy)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(copy);
        if (entry.Kind is not (HistoryKind.ClipboardText or HistoryKind.ClipboardFiles))
            throw new ArgumentException("텍스트 또는 파일 목록 기록이 필요합니다.", nameof(entry));

        var files = entry.Kind == HistoryKind.ClipboardFiles;
        Owner = owner; Icon = owner.Icon; Title = files ? "복사한 파일 목록" : "복사한 텍스트";
        Width = 680; Height = 430; MinWidth = 500; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = Ui.Panel; Foreground = Ui.Text; FontFamily = owner.FontFamily; FontSize = 13;

        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var metadata = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var time = entry.CapturedAt?.LocalDateTime ?? entry.SavedAt.ToLocalTime();
        var origin = time.ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture) + "  ·  " + entry.ApplicationName;
        var originLabel = Ui.Label(origin, 12, Ui.Muted);
        originLabel.TextWrapping = TextWrapping.NoWrap; originLabel.TextTrimming = TextTrimming.CharacterEllipsis; originLabel.ToolTip = origin;
        metadata.Children.Add(originLabel);
        if (!string.IsNullOrWhiteSpace(entry.WindowTitle))
        {
            var windowLabel = Ui.Label(entry.WindowTitle, 12, Ui.Text);
            windowLabel.Margin = new Thickness(0, 4, 0, 0); windowLabel.TextWrapping = TextWrapping.NoWrap;
            windowLabel.TextTrimming = TextTrimming.CharacterEllipsis; windowLabel.ToolTip = entry.WindowTitle;
            metadata.Children.Add(windowLabel);
        }
        layout.Children.Add(metadata);

        var contents = new TextBox
        {
            Text = files ? FileList(entry) : entry.Text ?? "",
            IsReadOnly = true, IsReadOnlyCaretVisible = true, AcceptsReturn = true, AcceptsTab = true,
            TextWrapping = files ? TextWrapping.NoWrap : TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = files ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top, FontSize = 13, Padding = new Thickness(10),
            BorderBrush = Ui.Line, BorderThickness = new Thickness(1)
        };
        AutomationProperties.SetName(contents, files ? "복사한 파일 이름과 경로" : "복사한 텍스트 내용");
        Grid.SetRow(contents, 1); layout.Children.Add(contents);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var close = Ui.Button("닫기", Close); close.IsCancel = true; close.MinWidth = 76; close.Height = 32; close.Margin = new Thickness(0, 0, 8, 0);
        var feedback = Ui.Label("", 12, Ui.Muted); feedback.Margin = new Thickness(0, 0, 12, 0);
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        var recopy = Ui.CommandButton("다시 복사", "copy", () =>
        {
            try { feedback.Text = copy() ? "복사했습니다" : "복사하지 못했습니다"; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "복사하지 못했습니다", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }, true);
        footer.Children.Add(feedback); footer.Children.Add(close); footer.Children.Add(recopy);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
        Motion.WhenLoadedEnter(layout, y: 4, duration: 160);
    }

    private static string FileList(HistoryEntry entry)
    {
        var text = new StringBuilder();
        if (entry.FilePaths is null) return "";
        foreach (var path in entry.FilePaths)
        {
            if (text.Length > 0) text.AppendLine().AppendLine();
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            text.Append(string.IsNullOrEmpty(name) ? path : name);
            if (!File.Exists(path) && !Directory.Exists(path)) text.Append("  ·  원본 없음");
            text.AppendLine().Append(path);
        }
        return text.ToString();
    }
}
