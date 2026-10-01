using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DamaCapture;

internal static class Motion
{
    private sealed class EntryState
    {
        public Transform? Wrapper;
        public TranslateTransform? Translation;
        public bool LoadEnterScheduled;
    }
    private sealed class ButtonState
    {
        public Border? Border, HoverBorder;
        public readonly ScaleTransform Scale = new(1, 1);
        public readonly TranslateTransform Translation = new();
        public bool PointerPressed, KeyPressed;
    }
    private sealed record ActiveAnimation(WeakReference<DependencyObject> Target, DependencyProperty Property, object FinalValue);
    private static readonly ConditionalWeakTable<FrameworkElement, EntryState> entries = new();
    private static readonly ConditionalWeakTable<Button, ButtonState> buttons = new();
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, long>> revisions = new();
    private static readonly Dictionary<long, ActiveAnimation> active = new();
    private static long nextRevision;
    private static bool reduceMotion;

    static Motion()
    {
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation) && !Enabled) FinishActive();
        };
    }
    public static bool ReduceMotion
    {
        get => reduceMotion;
        set { reduceMotion = value; if (!Enabled) FinishActive(); }
    }
    public static bool Enabled => !ReduceMotion && SystemParameters.ClientAreaAnimation;

    public static void Enter(FrameworkElement target, double x = 0, double y = 8, int delay = 0, int duration = 200)
    {
        var state = entries.GetValue(target, _ => new EntryState());
        if (state.Translation == null || !ReferenceEquals(state.Wrapper, target.RenderTransform))
        {
            var group = new TransformGroup();
            if (target.RenderTransform != null) group.Children.Add(target.RenderTransform);
            state.Translation = new TranslateTransform(); group.Children.Add(state.Translation);
            state.Wrapper = group; target.RenderTransform = group;
        }
        Run(target, UIElement.OpacityProperty, 0, 1, duration, delay);
        Run(state.Translation, TranslateTransform.XProperty, x, 0, duration, delay);
        Run(state.Translation, TranslateTransform.YProperty, y, 0, duration, delay);
    }
    public static void WhenLoadedEnter(FrameworkElement target, double x = 0, double y = 8, int delay = 0, int duration = 200)
    {
        var state = entries.GetValue(target, _ => new EntryState());
        if (state.LoadEnterScheduled) return;
        state.LoadEnterScheduled = true;
        if (target.IsLoaded) { Enter(target, x, y, delay, duration); return; }
        if (Enabled) target.SetValue(UIElement.OpacityProperty, 0d);
        RoutedEventHandler? loaded = null;
        loaded = (_, _) => { target.Loaded -= loaded; Enter(target, x, y, delay, duration); };
        target.Loaded += loaded;
    }
    public static void Animate(Animatable target, DependencyProperty prop, double to, int duration = 180) =>
        Run(target, prop, (double)target.GetValue(prop), to, duration);
    public static void Animate(UIElement target, DependencyProperty prop, double to, int duration = 180) =>
        Run(target, prop, (double)target.GetValue(prop), to, duration);
    /// <summary>One pass from <paramref name="from"/> to <paramref name="to"/>; the property rests at <paramref name="rest"/> (or <paramref name="to"/>) afterwards.</summary>
    public static void Play(DependencyObject target, DependencyProperty prop, double from, double to, int duration, double? rest = null, IEasingFunction? ease = null)
    {
        var final = rest ?? to;
        if (!Enabled || duration <= 0) { Start(target, prop, final, null); return; }
        Start(target, prop, final, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        });
    }
    /// <summary>A short choreography: each frame is a value reached at a time, eased from the frame before it.</summary>
    public static void Play(DependencyObject target, DependencyProperty prop, params (double Value, int At, IEasingFunction? Ease)[] frames)
    {
        if (frames.Length == 0) return;
        var final = frames[^1].Value;
        if (!Enabled) { Start(target, prop, final, null); return; }
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(frames[^1].At), FillBehavior = FillBehavior.Stop };
        foreach (var (value, at, ease) in frames)
        {
            var time = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at));
            animation.KeyFrames.Add(ease == null && at == 0 ? new DiscreteDoubleKeyFrame(value, time) : new EasingDoubleKeyFrame(value, time, ease ?? new CubicEase { EasingMode = EasingMode.EaseOut }));
        }
        Start(target, prop, final, animation);
    }
    /// <summary>Repeats forever while motion is allowed; otherwise the property holds <paramref name="rest"/>.</summary>
    public static void Loop(DependencyObject target, DependencyProperty prop, double from, double to, int duration, double rest)
    {
        if (!Enabled || duration <= 0) { Start(target, prop, rest, null); return; }
        Start(target, prop, rest, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration)) { RepeatBehavior = RepeatBehavior.Forever, FillBehavior = FillBehavior.Stop });
    }
    public static void Stop(DependencyObject target, DependencyProperty prop, double rest) => Start(target, prop, rest, null);

    internal static void AnimateColor(SolidColorBrush target, Color to, int duration = 110)
    {
        var from = target.Color;
        AnimationTimeline? animation = Enabled && duration > 0 && from != to
            ? new ColorAnimation(from, to, TimeSpan.FromMilliseconds(duration))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            }
            : null;
        Start(target, SolidColorBrush.ColorProperty, to, animation);
    }

    private static void Run(DependencyObject target, DependencyProperty prop, double from, double to, int duration, int delay = 0)
    {
        if (!double.IsFinite(to)) throw new ArgumentOutOfRangeException(nameof(to));
        if (!double.IsFinite(from)) from = to;
        if (!Enabled || duration <= 0 || Math.Abs(from - to) < .0001) { Start(target, prop, to, null); return; }
        delay = Math.Max(0, delay);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        AnimationTimeline animation;
        if (delay == 0)
            animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        else
        {
            var frames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delay + duration), FillBehavior = FillBehavior.Stop };
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + duration)), ease));
            animation = frames;
        }
        Start(target, prop, to, animation);
    }
    private static void Start(DependencyObject target, DependencyProperty prop, object finalValue, AnimationTimeline? animation)
    {
        var versions = revisions.GetOrCreateValue(target);
        if (versions.TryGetValue(prop, out var previous)) active.Remove(previous);
        var revision = ++nextRevision; versions[prop] = revision;
        Begin(target, prop, null);
        // The final value belongs to the element, so removing a clock cannot undo the state change.
        // SetCurrentValue is discarded when WPF attaches an animation to this property.
        target.SetValue(prop, finalValue);
        if (animation == null) return;
        active[revision] = new ActiveAnimation(new WeakReference<DependencyObject>(target), prop, finalValue);
        animation.Completed += (_, _) =>
        {
            active.Remove(revision);
            if (versions.TryGetValue(prop, out var current) && current == revision)
            {
                Begin(target, prop, null);
                target.SetValue(prop, finalValue);
            }
        };
        Begin(target, prop, animation);
        if (active.Count > 64)
            foreach (var item in active.Where(item => !item.Value.Target.TryGetTarget(out _)).ToArray()) active.Remove(item.Key);
    }
    private static void Begin(DependencyObject target, DependencyProperty prop, AnimationTimeline? animation)
    {
        if (target is UIElement element) element.BeginAnimation(prop, animation, HandoffBehavior.SnapshotAndReplace);
        else if (target is Animatable animatable) animatable.BeginAnimation(prop, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private static void FinishActive()
    {
        var pending = active.ToArray(); active.Clear();
        foreach (var item in pending)
        {
            if (!item.Value.Target.TryGetTarget(out var target)) continue;
            void Finish()
            {
                if (revisions.TryGetValue(target, out var versions) && versions.TryGetValue(item.Value.Property, out var current) && current == item.Key)
                {
                    versions[item.Value.Property] = ++nextRevision;
                    Begin(target, item.Value.Property, null);
                    target.SetValue(item.Value.Property, item.Value.FinalValue);
                }
            }
            if (target.Dispatcher.CheckAccess()) Finish(); else target.Dispatcher.BeginInvoke((Action)Finish);
        }
    }

    public static void HookButton(Button button)
    {
        if (buttons.TryGetValue(button, out _)) return;
        var state = new ButtonState(); buttons.Add(button, state);
        void Update(bool animate = true)
        {
            if (!button.IsLoaded) return;
            button.ApplyTemplate();
            if (button.Template?.FindName("Border", button) is not Border border) return;
            if (!ReferenceEquals(state.Border, border))
            {
                state.Border = border; state.HoverBorder = button.Template.FindName("HoverBorder", button) as Border;
                var group = new TransformGroup(); group.Children.Add(state.Scale); group.Children.Add(state.Translation);
                border.RenderTransform = group; border.RenderTransformOrigin = new Point(.5, .5);
                // The untransformed template grid owns pointer hits, even while the visual lifts.
                border.IsHitTestVisible = false;
            }
            var hover = button.IsEnabled && button.IsMouseOver;
            var pressed = button.IsEnabled && ((state.PointerPressed && hover) || state.KeyPressed);
            var duration = animate ? 110 : 0;
            Animate(state.Scale, ScaleTransform.ScaleXProperty, pressed ? .98 : 1, duration);
            Animate(state.Scale, ScaleTransform.ScaleYProperty, pressed ? .98 : 1, duration);
            Animate(state.Translation, TranslateTransform.YProperty, hover && !pressed ? -1 : 0, duration);
            if (state.HoverBorder is { } outline) Run(outline, UIElement.OpacityProperty, outline.Opacity, hover ? .85 : 0, duration);
        }
        button.Loaded += (_, _) => Update(false);
        button.MouseEnter += (_, _) => Update(); button.MouseLeave += (_, _) => Update();
        button.PreviewMouseLeftButtonDown += (_, _) => { state.PointerPressed = true; Update(); };
        button.PreviewMouseLeftButtonUp += (_, _) => { state.PointerPressed = false; Update(); };
        button.LostMouseCapture += (_, _) => { state.PointerPressed = false; Update(); };
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) { state.KeyPressed = true; Update(); } };
        button.PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) { state.KeyPressed = false; Update(); } };
        button.LostKeyboardFocus += (_, _) => { state.KeyPressed = false; Update(); };
        button.IsEnabledChanged += (_, _) => { if (!button.IsEnabled) state.PointerPressed = state.KeyPressed = false; Update(); };
        button.Unloaded += (_, _) =>
        {
            state.PointerPressed = state.KeyPressed = false;
            Animate(state.Scale, ScaleTransform.ScaleXProperty, 1, 0); Animate(state.Scale, ScaleTransform.ScaleYProperty, 1, 0);
            Animate(state.Translation, TranslateTransform.YProperty, 0, 0);
            if (state.HoverBorder is { } outline) Run(outline, UIElement.OpacityProperty, outline.Opacity, 0, 0);
        };
        if (button.IsLoaded) Update(false);
    }
}
