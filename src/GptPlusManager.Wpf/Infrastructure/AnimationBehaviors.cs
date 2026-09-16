using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace GptPlusManager.Wpf.Infrastructure;

public static class EntranceAnimation
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(EntranceAnimation), new PropertyMetadata(false, OnChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        element.Loaded += (_, _) =>
        {
            element.Opacity = 0;
            element.RenderTransform = new TranslateTransform(0, 10);
            var duration = TimeSpan.FromMilliseconds(180);
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            ((TranslateTransform)element.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
    }
}

public static class AnimatedProgress
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(AnimatedProgress), new PropertyMetadata(0d, OnValueChanged));

    public static void SetValue(DependencyObject element, double value) => element.SetValue(ValueProperty, value);
    public static double GetValue(DependencyObject element) => (double)element.GetValue(ValueProperty);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProgressBar bar) return;
        var animation = new DoubleAnimation((double)e.OldValue, (double)e.NewValue, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
