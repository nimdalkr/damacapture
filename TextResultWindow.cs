using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DamaCapture.Services;

namespace DamaCapture;

/// <summary>Shows recognized text for correction, copying, or sending to an AI chat.</summary>
internal sealed class TextResultWindow : Window
{
    public TextResultWindow(Window owner, TextRecognition.Result result, MainWindow.ChatService[] services, Func<MainWindow.ChatService, string, string> sendToChat)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sendToChat);
        Owner = owner; Icon = owner.Icon; Title = "추출한 텍스트";
        Width = 680; Height = 460; MinWidth = 520; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = Ui.Panel; Foreground = Ui.Text; FontFamily = owner.FontFamily; FontSize = 13;

        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBox
        {
            Text = result.Text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 13, Padding = new Thickness(10), BorderBrush = Ui.Line, BorderThickness = new Thickness(1)
        };
        AutomationProperties.SetName(text, "추출한 텍스트");
        layout.Children.Add(text);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var feedback = Ui.Label("", 12, Ui.Muted); feedback.TextWrapping = TextWrapping.NoWrap; feedback.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = Ui.Button("닫기", Close); close.IsCancel = true; close.MinWidth = 76; close.Height = 32; close.Margin = new Thickness(0, 0, 8, 0);
        var copy = Ui.CommandButton("복사", "copy", () =>
        {
            try { ClipboardTransfer.SetText(text.Text); feedback.Text = "복사했습니다"; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "복사하지 못했습니다", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }, true);
        right.Children.Add(close); right.Children.Add(copy);
        DockPanel.SetDock(right, Dock.Right); footer.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var sendLabel = Ui.Label("AI에서 편집하기", 12, Ui.Muted); sendLabel.Margin = new Thickness(0, 0, 8, 0); left.Children.Add(sendLabel);
        foreach (var service in services)
        {
            var button = Ui.Button(service.Name, () =>
            {
                try { feedback.Text = sendToChat(service, text.Text); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, service.Name, MessageBoxButton.OK, MessageBoxImage.Warning); }
            });
            button.Height = 32; button.Padding = new Thickness(12, 0, 12, 0); button.Margin = new Thickness(0, 0, 6, 0);
            AutomationProperties.SetName(button, service.Name + "에서 편집하기");
            left.Children.Add(button);
        }
        feedback.Margin = new Thickness(10, 0, 10, 0); left.Children.Add(feedback);
        footer.Children.Add(left);
        Grid.SetRow(footer, 1); layout.Children.Add(footer);
        Content = layout;
        Motion.WhenLoadedEnter(layout, y: 4, duration: 160);
        Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
    }
}
