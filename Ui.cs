using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Markup;

namespace DamaCapture;

internal static class Ui
{
    private static readonly ConditionalWeakTable<Button, Dictionary<DependencyProperty, SolidColorBrush>> buttonColors = new();
    private static readonly ConditionalWeakTable<Border, SolidColorBrush> borderFills = new();
    private static bool chromeHooked;
    // Brand: black #080809, red #EE202E, warm white #F0EEEB. Neutrals stay warm to match.
    public static readonly SolidColorBrush Background = Brush("#F0EEEB");
    public static readonly SolidColorBrush Panel = Brush("#FAF9F7");
    public static readonly SolidColorBrush Canvas = Brush("#E4E1DC");
    public static readonly SolidColorBrush Text = Brush("#1A191C");
    public static readonly SolidColorBrush Muted = Brush("#6B6661");
    public static readonly SolidColorBrush Line = Brush("#E3DFD9");
    public static readonly SolidColorBrush Primary = Brush("#080809");
    public static readonly SolidColorBrush OnPrimary = Brush("#FAF9F7");
    public static readonly SolidColorBrush Red = Brush("#EE202E");
    public static readonly SolidColorBrush RedText = Brush("#C4121E");
    public static readonly SolidColorBrush RedTint = Brush("#FBE5E5");
    public static readonly SolidColorBrush Track = Brush("#ECE9E4");
    public static SolidColorBrush Brush(string color) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); b.Freeze(); return b; }

    public static void Install(Application app)
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
<Style TargetType="Window"><Setter Property="Background" Value="#F0EEEB"/><Setter Property="Foreground" Value="#1A191C"/><Setter Property="FontFamily" Value="Segoe UI, Malgun Gothic"/><Setter Property="FontSize" Value="12"/><Setter Property="UseLayoutRounding" Value="True"/></Style>
<Style TargetType="Button"><Setter Property="Foreground" Value="#1A191C"/><Setter Property="Background" Value="#FAF9F7"/><Setter Property="BorderBrush" Value="#DAD5CE"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="10,5"/><Setter Property="Margin" Value="0,0,4,0"/><Setter Property="MinHeight" Value="30"/><Setter Property="FontSize" Value="12"/><Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="Cursor" Value="Arrow"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Grid Background="Transparent"><Border x:Name="Border" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/></Border><Border x:Name="HoverBorder" Background="#0E080809" CornerRadius="6" Opacity="0" IsHitTestVisible="False" RenderTransformOrigin="0.5,0.5" RenderTransform="{Binding RenderTransform, ElementName=Border}"/><Border x:Name="FocusBorder" BorderBrush="#EE202E" BorderThickness="1.5" CornerRadius="6" Opacity="0" IsHitTestVisible="False" RenderTransformOrigin="0.5,0.5" RenderTransform="{Binding RenderTransform, ElementName=Border}"/></Grid><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="FocusBorder" Property="Opacity" Value="1"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.38"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="TextBox"><Setter Property="Foreground" Value="#1A191C"/><Setter Property="Background" Value="White"/><Setter Property="BorderBrush" Value="#CFC9C2"/><Setter Property="BorderThickness" Value="1"/><Setter Property="CaretBrush" Value="#1A191C"/><Setter Property="Padding" Value="6,3"/><Setter Property="SelectionBrush" Value="#EE202E"/><Setter Property="SelectionOpacity" Value="0.3"/><Setter Property="MinHeight" Value="28"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox"><Border x:Name="Box" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" SnapsToDevicePixels="True"><ScrollViewer x:Name="PART_ContentHost" Focusable="False" HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#A8A098"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#080809"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="CheckBox"><Setter Property="Foreground" Value="#1A191C"/><Setter Property="Background" Value="White"/><Setter Property="BorderBrush" Value="#B3ACA4"/><Setter Property="Margin" Value="0,5,0,5"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><Grid Background="Transparent"><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition/></Grid.ColumnDefinitions><Border x:Name="Box" Width="17" Height="17" CornerRadius="4" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1.5" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"><Path x:Name="Mark" Data="M3.2,7 L5.8,9.6 L10.8,4.2" Stroke="#FAF9F7" StrokeThickness="1.9" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" Opacity="0"/></Border><ContentPresenter Grid.Column="1" Margin="9,0,0,0" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/><Border x:Name="Focus" Grid.ColumnSpan="2" BorderBrush="#EE202E" BorderThickness="1.5" CornerRadius="5" Margin="-4,-3" Opacity="0" IsHitTestVisible="False"/></Grid><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#6B6661"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="Box" Property="Background" Value="#080809"/><Setter TargetName="Box" Property="BorderBrush" Value="#080809"/><Setter TargetName="Mark" Property="Opacity" Value="1"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Opacity" Value="1"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="ComboBox"><Setter Property="Foreground" Value="#1A191C"/><Setter Property="Background" Value="White"/><Setter Property="BorderBrush" Value="#CFC9C2"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="9,0,26,0"/><Setter Property="MinHeight" Value="28"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid><ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6"><Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,1,10,0" Data="M0,0 L4,4 L8,0" Stroke="#6B6661" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#A8A098"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#080809"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton><ContentPresenter IsHitTestVisible="False" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}" Margin="{TemplateBinding Padding}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" HorizontalAlignment="Left"/><Border x:Name="Focus" BorderBrush="#EE202E" BorderThickness="1.5" CornerRadius="6" Opacity="0" IsHitTestVisible="False"/><Popup x:Name="PART_Popup" Placement="Bottom" VerticalOffset="4" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False" PopupAnimation="None"><Border Background="White" BorderBrush="#E3DFD9" BorderThickness="1" CornerRadius="7" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}"><ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border></Popup></Grid><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Opacity" Value="1"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="ComboBoxItem"><Setter Property="Foreground" Value="#1A191C"/><Setter Property="Padding" Value="8,5"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="Row" Background="Transparent" CornerRadius="5" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="#F0EEEB"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter Property="FontWeight" Value="SemiBold"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="ListBox"><Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="#1A191C"/><Setter Property="BorderBrush" Value="#E3DFD9"/><Setter Property="BorderThickness" Value="0"/></Style>
<Style TargetType="ListBoxItem"><Setter Property="Foreground" Value="#1A191C"/><Setter Property="Padding" Value="10,7"/><Setter Property="Margin" Value="0,0,0,2"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem"><Border x:Name="Row" Padding="{TemplateBinding Padding}" BorderBrush="Transparent" BorderThickness="1" CornerRadius="6" Background="Transparent"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="#F0EEEB"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="#FBE5E5"/><Setter Property="Foreground" Value="#C4121E"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Row" Property="BorderBrush" Value="#EE202E"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.38"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="MenuItem"><Setter Property="Foreground" Value="#1A191C"/><Setter Property="FontSize" Value="12"/><Setter Property="MinHeight" Value="30"/><Setter Property="Padding" Value="6,5,12,5"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem"><Border x:Name="Row" Background="Transparent" CornerRadius="5" Padding="{TemplateBinding Padding}" MinHeight="{TemplateBinding MinHeight}"><Grid VerticalAlignment="Center"><Grid.ColumnDefinitions><ColumnDefinition Width="24"/><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><Path x:Name="Check" Data="M1,5 L4.2,8.2 L10,2" Stroke="#1A191C" StrokeThickness="1.7" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" VerticalAlignment="Center" HorizontalAlignment="Center" Visibility="Hidden"/><ContentPresenter ContentSource="Icon" VerticalAlignment="Center" HorizontalAlignment="Center" Margin="0,0,2,0"/><ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/><TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}" Foreground="#8A847D" FontWeight="Normal" Margin="28,0,0,0" VerticalAlignment="Center"/></Grid></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="#F0EEEB"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/><Setter Property="FontWeight" Value="SemiBold"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator"><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Background="#E3DFD9" Margin="8,4"/></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="ContextMenu"><Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#1A191C"/><Setter Property="BorderBrush" Value="#E3DFD9"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="4"/><Setter Property="HasDropShadow" Value="False"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="8" Margin="0,0,6,6"><Border.Effect><DropShadowEffect BlurRadius="14" ShadowDepth="3" Direction="270" Opacity="0.12" Color="#080809"/></Border.Effect><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/></Border></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="TabControl"><Setter Property="Background" Value="#FAF9F7"/><Setter Property="Foreground" Value="#1A191C"/><Setter Property="BorderBrush" Value="#E3DFD9"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="12"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabControl"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8"><Grid KeyboardNavigation.TabNavigation="Local"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions><Border BorderBrush="#E3DFD9" BorderThickness="0,0,0,1" Padding="6,0"><TabPanel x:Name="HeaderPanel" IsItemsHost="True" KeyboardNavigation.TabIndex="1" Background="Transparent"/></Border><ContentPresenter x:Name="PART_SelectedContentHost" Grid.Row="1" ContentSource="SelectedContent" Margin="{TemplateBinding Padding}" SnapsToDevicePixels="{TemplateBinding SnapsToDevicePixels}"/></Grid></Border></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="TabItem"><Setter Property="Foreground" Value="#1A191C"/><Setter Property="Background" Value="Transparent"/><Setter Property="Padding" Value="12,9"/><Setter Property="MinHeight" Value="36"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Grid Background="Transparent"><Border x:Name="TabBody" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"><ContentPresenter x:Name="Header" ContentSource="Header" RecognizesAccessKey="True" HorizontalAlignment="Center" VerticalAlignment="Center" TextElement.Foreground="#6B6661"/></Border><Border x:Name="TabFocus" BorderBrush="#EE202E" BorderThickness="1.5" CornerRadius="5" Margin="2,3" IsHitTestVisible="False" Opacity="0"/></Grid><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Header" Property="TextElement.Foreground" Value="#1A191C"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="Header" Property="TextElement.Foreground" Value="#1A191C"/><Setter TargetName="Header" Property="TextElement.FontWeight" Value="SemiBold"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="TabFocus" Property="Opacity" Value="1"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.38"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="ToolTip"><Setter Property="Foreground" Value="#FAF9F7"/><Setter Property="FontSize" Value="12"/><Setter Property="HasDropShadow" Value="False"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToolTip"><Border Background="#1F1E21" CornerRadius="5" Padding="9,5"><ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter></Style>
<Style x:Key="PageButton" TargetType="RepeatButton"><Setter Property="Focusable" Value="False"/><Setter Property="IsTabStop" Value="False"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter></Style>
<Style x:Key="ScrollThumb" TargetType="Thumb"><Setter Property="Focusable" Value="False"/><Setter Property="IsTabStop" Value="False"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb"><Border x:Name="Bar" Background="#C9C3BC" CornerRadius="3"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bar" Property="Background" Value="#A39C94"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Bar" Property="Background" Value="#7A746D"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<ControlTemplate x:Key="VerticalScroll" TargetType="ScrollBar"><Grid Background="{TemplateBinding Background}"><Track x:Name="PART_Track" IsDirectionReversed="True"><Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Style="{StaticResource PageButton}"/></Track.DecreaseRepeatButton><Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Style="{StaticResource PageButton}"/></Track.IncreaseRepeatButton><Track.Thumb><Thumb Style="{StaticResource ScrollThumb}" Margin="3,2"/></Track.Thumb></Track></Grid></ControlTemplate>
<ControlTemplate x:Key="HorizontalScroll" TargetType="ScrollBar"><Grid Background="{TemplateBinding Background}"><Track x:Name="PART_Track"><Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageLeftCommand" Style="{StaticResource PageButton}"/></Track.DecreaseRepeatButton><Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageRightCommand" Style="{StaticResource PageButton}"/></Track.IncreaseRepeatButton><Track.Thumb><Thumb Style="{StaticResource ScrollThumb}" Margin="2,3"/></Track.Thumb></Track></Grid></ControlTemplate>
<Style TargetType="ScrollBar"><Setter Property="Background" Value="Transparent"/><Setter Property="Width" Value="11"/><Setter Property="MinWidth" Value="11"/><Setter Property="Template" Value="{StaticResource VerticalScroll}"/><Style.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="MinWidth" Value="0"/><Setter Property="Height" Value="11"/><Setter Property="MinHeight" Value="11"/><Setter Property="Template" Value="{StaticResource HorizontalScroll}"/></Trigger></Style.Triggers></Style>
<Style TargetType="ScrollViewer"><Setter Property="PanningMode" Value="Both"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollViewer"><Grid Background="{TemplateBinding Background}"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><Grid.RowDefinitions><RowDefinition/><RowDefinition Height="Auto"/></Grid.RowDefinitions><ScrollContentPresenter x:Name="PART_ScrollContentPresenter" CanContentScroll="{TemplateBinding CanContentScroll}" Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" Margin="{TemplateBinding Padding}"/><ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Cursor="Arrow" Maximum="{TemplateBinding ScrollableHeight}" Minimum="0" ViewportSize="{TemplateBinding ViewportHeight}" Value="{Binding VerticalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}" Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}" AutomationProperties.AutomationId="VerticalScrollBar"/><ScrollBar x:Name="PART_HorizontalScrollBar" Grid.Row="1" Orientation="Horizontal" Cursor="Arrow" Maximum="{TemplateBinding ScrollableWidth}" Minimum="0" ViewportSize="{TemplateBinding ViewportWidth}" Value="{Binding HorizontalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}" Visibility="{TemplateBinding ComputedHorizontalScrollBarVisibility}" AutomationProperties.AutomationId="HorizontalScrollBar"/></Grid></ControlTemplate></Setter.Value></Setter></Style>
<Style x:Key="SliderFill" TargetType="RepeatButton"><Setter Property="Focusable" Value="False"/><Setter Property="IsTabStop" Value="False"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Grid Background="Transparent"><Border Height="4" CornerRadius="2" Background="#080809" VerticalAlignment="Center"/></Grid></ControlTemplate></Setter.Value></Setter></Style>
<Style x:Key="SliderRest" TargetType="RepeatButton"><Setter Property="Focusable" Value="False"/><Setter Property="IsTabStop" Value="False"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Grid Background="Transparent"><Border Height="4" CornerRadius="2" Background="#DAD5CE" VerticalAlignment="Center"/></Grid></ControlTemplate></Setter.Value></Setter></Style>
<Style x:Key="SliderThumb" TargetType="Thumb"><Setter Property="Width" Value="16"/><Setter Property="Height" Value="16"/><Setter Property="BorderBrush" Value="#080809"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb"><Ellipse x:Name="Knob" Fill="White" Stroke="{TemplateBinding BorderBrush}" StrokeThickness="2"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Knob" Property="StrokeThickness" Value="3"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Knob" Property="StrokeThickness" Value="4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType="Slider"><Setter Property="Margin" Value="0,4,0,4"/><Setter Property="MinHeight" Value="22"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Slider"><Grid Background="Transparent" VerticalAlignment="Center"><Track x:Name="PART_Track"><Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Style="{StaticResource SliderFill}"/></Track.DecreaseRepeatButton><Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Style="{StaticResource SliderRest}"/></Track.IncreaseRepeatButton><Track.Thumb><Thumb x:Name="Thumb" Style="{StaticResource SliderThumb}"/></Track.Thumb></Track></Grid><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Thumb" Property="BorderBrush" Value="#EE202E"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
</ResourceDictionary>
""";
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml));
        if (chromeHooked) return;
        chromeHooked = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => TintCaption((Window)sender)));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;
    // Windows 11 draws the title bar in the app's surface color; earlier versions ignore these attributes.
    private static void TintCaption(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var caption = ColorRef(Panel.Color); var border = ColorRef(Color.FromRgb(0xD5, 0xD0, 0xC9));
        try { DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)); DwmSetWindowAttribute(handle, 34, ref border, sizeof(int)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public static TextBlock Label(string value, double size = 13, Brush? brush = null, FontWeight? weight = null) =>
        new() { Text = value, FontSize = size, Foreground = brush ?? Text, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    public static Button Button(string label, Action click, bool primary = false)
    {
        var b = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(b, label);
        SetPrimary(b, primary, animate: false); Motion.HookButton(b);
        b.Click += (_, _) => click(); return b;
    }
    public static FrameworkElement Icon(string icon, double size = 20, Brush? brush = null) => ToolbarIcons.Create(icon, size, brush);
    public static Button IconButton(string label, string icon, Action click, bool primary = false, bool compact = false)
    {
        var button = Button(label, click, primary);
        button.Width = compact ? 32 : 56;
        button.Height = compact ? 32 : 60;
        button.Padding = new Thickness(compact ? 6 : 2);
        button.Margin = new Thickness(0);
        button.ToolTip = label;
        ToolTipService.SetInitialShowDelay(button, 500);
        if (!primary) SetGhost(button, false, animate: false);
        if (compact) button.Content = Icon(icon, 18);
        else
        {
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(Icon(icon, 22));
            var text = new TextBlock
            {
                Text = label, FontSize = 12, TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 7, 0, 0)
            };
            text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
            content.Children.Add(text);
            button.Content = content;
        }
        return button;
    }
    public static Button CommandButton(string label, string icon, Action click, bool primary = false)
    {
        var button = Button(label, click, primary);
        button.Height = 32;
        button.Padding = new Thickness(11, 0, 12, 0);
        button.Margin = new Thickness(0);
        button.Content = HorizontalButtonContent(button, label, icon);
        return button;
    }
    public static Button ToolButton(string label, string icon, Action click)
    {
        var button = Button(label, click);
        button.Height = 32;
        button.Padding = new Thickness(9, 0, 9, 0);
        button.Margin = new Thickness(0);
        button.Content = HorizontalButtonContent(button, label, icon);
        SetGhost(button, false, animate: false);
        return button;
    }
    public static FrameworkElement HorizontalButtonContent(Button button, string label, string icon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(Icon(icon, 18));
        var text = new TextBlock
        {
            Text = label, FontSize = 13, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 1)
        };
        text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        content.Children.Add(text);
        return content;
    }
    /// <summary>Joins a command and its menu arrow into one outlined control.</summary>
    public static Border Split(Button main, Button menu)
    {
        foreach (var button in new[] { main, menu }) { button.Margin = new Thickness(0); button.Height = 30; SetGhost(button, false, animate: false); }
        menu.Width = 22; menu.Padding = new Thickness(2, 0, 4, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(main);
        row.Children.Add(new Border { Width = 1, Margin = new Thickness(0, 7, 0, 7), Background = Line });
        row.Children.Add(menu);
        return new Border { Child = row, CornerRadius = new CornerRadius(7), BorderBrush = Brush("#DAD5CE"), BorderThickness = new Thickness(1), Background = Panel, VerticalAlignment = VerticalAlignment.Center };
    }
    // Neutral, bordered button on a panel.
    public static void SetPrimary(Button button, bool primary, bool animate = true)
    {
        Paint(button, primary ? Primary.Color : Panel.Color, primary ? OnPrimary.Color : Text.Color, primary ? Primary.Color : Color.FromRgb(0xDA, 0xD5, 0xCE), animate);
        button.FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal;
    }
    // Toggle-style buttons (panel switches, launcher actions): tinted when on.
    public static void SetSelected(Button button, bool selected, bool animate = true)
    {
        Paint(button, selected ? RedTint.Color : Panel.Color, selected ? RedText.Color : Text.Color, selected ? RedTint.Color : Panel.Color, animate);
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }
    // Borderless buttons that sit inside a shared surface such as a split or tool group.
    // The weight stays fixed so selecting a tool never changes toolbar widths.
    public static void SetGhost(Button button, bool selected, bool animate = true)
    {
        Paint(button, Colors.Transparent, selected ? RedText.Color : Text.Color, Colors.Transparent, animate);
        button.BorderThickness = new Thickness(0);
        button.FontWeight = FontWeights.Normal;
    }
    // One option of a segmented control: the chosen option is lifted onto the panel color.
    public static void SetSegment(Button button, bool selected, bool animate = true)
    {
        Paint(button, selected ? Panel.Color : Colors.Transparent, selected ? Text.Color : Muted.Color, selected ? Color.FromRgb(0xDA, 0xD5, 0xCE) : Colors.Transparent, animate);
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }
    public static void SetFill(Border border, Color color, bool animate = true)
    {
        if (!borderFills.TryGetValue(border, out var brush) || !ReferenceEquals(border.Background, brush))
        {
            brush = new SolidColorBrush((border.Background as SolidColorBrush)?.Color ?? color);
            borderFills.AddOrUpdate(border, brush); border.Background = brush;
        }
        Motion.AnimateColor(brush, color, animate ? 110 : 0);
    }
    private static void Paint(Button button, Color background, Color foreground, Color border, bool animate)
    {
        SetButtonColor(button, Control.BackgroundProperty, background, animate);
        SetButtonColor(button, Control.ForegroundProperty, foreground, animate);
        SetButtonColor(button, Control.BorderBrushProperty, border, animate);
    }
    private static void SetButtonColor(Button button, DependencyProperty property, Color color, bool animate)
    {
        var owned = buttonColors.GetOrCreateValue(button);
        var current = button.GetValue(property) as SolidColorBrush;
        if (!owned.TryGetValue(property, out var brush) || !ReferenceEquals(current, brush) || brush.IsFrozen)
        {
            // A caller may have assigned a shared brush; only animate this button's own instance.
            brush = new SolidColorBrush(current?.Color ?? color); owned[property] = brush;
            button.SetValue(property, brush);
        }
        Motion.AnimateColor(brush, color, animate ? 110 : 0);
    }
    public static Border Rule(double margin = 16) => new() { Height = 1, Background = Line, Margin = new Thickness(0, margin, 0, margin) };
    public static StackPanel Field(string label, UIElement input)
    {
        var p = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        p.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 7) });
        p.Children.Add(input); return p;
    }
    public static TextBlock Section(string title, bool first = false) =>
        new() { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(0, first ? 0 : 18, 0, 6) };
    public static ComboBox Choice(string[] values, int selected = 0)
    {
        var c = new ComboBox { ItemsSource = values, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch }; return c;
    }
}
