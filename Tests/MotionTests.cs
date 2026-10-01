using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DamaCapture.Tests;

public static class MotionTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        var previous = Motion.ReduceMotion;
        try
        {
            Motion.ReduceMotion = false;
            var animated = Motion.Enabled;
            var button = Ui.Button("모자이크", () => { });
            Ui.SetPrimary(button, true);
            var selected = (SolidColorBrush)button.Background;
            // This failed before the fix: attaching an animation discarded SetCurrentValue's color.
            Assert((Color)selected.GetAnimationBaseValue(SolidColorBrush.ColorProperty) == Ui.Primary.Color,
                "The selected color must be an animation base value, not only its temporary visual value.");

            var marker = new TranslateTransform();
            Motion.Animate(marker, TranslateTransform.YProperty, 83, 110);
            Assert(Close((double)marker.GetAnimationBaseValue(TranslateTransform.YProperty), 83), "Navigation marker lost its destination base value.");
            var panel = new Border { Opacity = .4 };
            Motion.Enter(panel, x: 12, y: 7, delay: 20, duration: 110);
            Pump(330);

            Assert(selected.Color == Ui.Primary.Color, "The selected button returned to its old color after animation.");
            Assert(!selected.HasAnimatedProperties, "Selected color retained an animation clock after completion.");
            results.Add("PASS: Selected button color persists after its transition and releases its clock");
            Assert(Close(marker.Y, 83) && !marker.HasAnimatedProperties, "Navigation marker returned to its old position or retained its clock.");
            results.Add("PASS: Navigation marker keeps its final position after animation");
            Assert(Close(panel.Opacity, 1) && Close(panel.RenderTransform.Value.OffsetX, 0) && Close(panel.RenderTransform.Value.OffsetY, 0), "Page entrance did not finish fully visible at its final position.");
            results.Add("PASS: Delayed entrance finishes at full opacity with zero translation");

            Ui.SetPrimary(button, false); Pump(25);
            Ui.SetPrimary(button, true); Pump(25);
            Ui.SetPrimary(button, false); Pump(25);
            Ui.SetPrimary(button, true); Pump(330);
            Assert(((SolidColorBrush)button.Background).Color == Ui.Primary.Color, "An old completion callback replaced the most recent tool selection.");
            Assert(!((SolidColorBrush)button.Background).HasAnimatedProperties, "Rapid tool selection retained an animation clock.");
            results.Add("PASS: Rapid tool changes preserve the latest selected color");

            Motion.Animate(marker, TranslateTransform.YProperty, 144, 300);
            Ui.SetPrimary(button, false);
            Motion.Enter(panel, x: -12, duration: 300);
            Pump(25);
            Motion.ReduceMotion = true;
            Assert(Close(marker.Y, 144) && !marker.HasAnimatedProperties, "Reduced motion did not settle the active navigation transition.");
            Assert(((SolidColorBrush)button.Background).Color == Ui.Panel.Color && !((SolidColorBrush)button.Background).HasAnimatedProperties, "Reduced motion did not settle the active button transition.");
            Assert(Close(panel.Opacity, 1) && Close(panel.RenderTransform.Value.OffsetX, 0) && Close(panel.RenderTransform.Value.OffsetY, 0), "Reduced motion left the entering page partially hidden.");
            results.Add("PASS: Enabling reduced motion immediately settles active transitions");
            if (!animated) results.Add("NOTE: Windows animations are disabled; immediate final states were verified");
            return results;
        }
        finally { Motion.ReduceMotion = true; Motion.ReduceMotion = previous; }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        EventHandler? tick = null;
        tick = (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Tick += tick;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= tick; }
    }
    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) < .001;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Motion regression: " + message);
    }
}
