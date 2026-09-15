using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace EasyPub.Desktop;

/// <summary>Short, replaceable opacity transitions; never delay input or change layout.</summary>
public static class MotionEffects
{
    public static readonly DependencyProperty HoverEnabledProperty = DependencyProperty.RegisterAttached(
        "HoverEnabled", typeof(bool), typeof(MotionEffects), new PropertyMetadata(false, OnHoverEnabledChanged));

    public static bool GetHoverEnabled(DependencyObject element) => (bool)element.GetValue(HoverEnabledProperty);
    public static void SetHoverEnabled(DependencyObject element, bool value) => element.SetValue(HoverEnabledProperty, value);

    internal static bool CanAnimate(bool enabled) => enabled && SystemParameters.ClientAreaAnimation
        && !SystemParameters.HighContrast;

    private static void OnHoverEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        element.MouseEnter -= Enter;
        element.MouseLeave -= Leave;
        element.PreviewMouseDown -= Stop;
        element.IsEnabledChanged -= EnabledChanged;
        element.Unloaded -= Unloaded;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!(bool)args.NewValue) return;
        element.MouseEnter += Enter;
        element.MouseLeave += Leave;
        element.PreviewMouseDown += Stop;
        element.IsEnabledChanged += EnabledChanged;
        element.Unloaded += Unloaded;
    }

    private static void Enter(object sender, MouseEventArgs args) => Hover((FrameworkElement)sender, 1, 0.90);
    private static void Leave(object sender, MouseEventArgs args) => Hover((FrameworkElement)sender, 0.90, 1);
    private static void Stop(object sender, MouseButtonEventArgs args) => Clear((FrameworkElement)sender);
    private static void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => Clear((FrameworkElement)sender);
    private static void Unloaded(object sender, RoutedEventArgs args) => Clear((FrameworkElement)sender);
    private static void Clear(FrameworkElement element) => element.BeginAnimation(UIElement.OpacityProperty, null);

    private static void Hover(FrameworkElement element, double from, double to)
    {
        if (!element.IsEnabled || !CanAnimate(GetHoverEnabled(element))) { Clear(element); return; }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(90))
        {
            FillBehavior = FillBehavior.Stop,
        }, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void ShowPage(FrameworkElement page, bool enabled)
    {
        Clear(page);
        if (!CanAnimate(enabled)) return;
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(140))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        }, HandoffBehavior.SnapshotAndReplace);
    }
}
