using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AppTunnel.Helpers;

/// <summary>
/// True horizontal ticker: full text enters from one side of the viewport and exits the other.
/// Parent must use <c>ClipToBounds="True"</c>. Do not use <c>TextTrimming</c> on the same TextBlock.
/// </summary>
public static class TextMarqueeBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(TextMarqueeBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SpeedProperty =
        DependencyProperty.RegisterAttached(
            "Speed",
            typeof(double),
            typeof(TextMarqueeBehavior),
            new PropertyMetadata(36.0));

    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))!;

    private sealed class MarqueeState
    {
        public readonly TranslateTransform Transform = new();
        public TextTrimming SavedTrimming = TextTrimming.None;
        public double? SavedWidth;
        public bool IsActive;
    }

    private static readonly Dictionary<TextBlock, MarqueeState> States = new();

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static double GetSpeed(DependencyObject element) =>
        (double)element.GetValue(SpeedProperty);

    public static void SetSpeed(DependencyObject element, double value) =>
        element.SetValue(SpeedProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        if ((bool)e.NewValue)
        {
            EnsureTransform(textBlock);
            textBlock.Loaded += OnLayoutChanged;
            textBlock.SizeChanged += OnLayoutChanged;
            textBlock.IsVisibleChanged += OnVisibilityChanged;
            TextDescriptor.AddValueChanged(textBlock, OnTextChanged);
            QueueUpdate(textBlock);
        }
        else
        {
            textBlock.Loaded -= OnLayoutChanged;
            textBlock.SizeChanged -= OnLayoutChanged;
            textBlock.IsVisibleChanged -= OnVisibilityChanged;
            TextDescriptor.RemoveValueChanged(textBlock, OnTextChanged);
            Stop(textBlock);
            States.Remove(textBlock);
        }
    }

    private static void OnLayoutChanged(object sender, RoutedEventArgs e) => QueueUpdate((TextBlock)sender);

    private static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBlock textBlock)
            return;

        if (textBlock.IsVisible)
            QueueUpdate(textBlock);
        else
            Stop(textBlock);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock textBlock)
            QueueUpdate(textBlock);
    }

    private static void QueueUpdate(TextBlock textBlock)
    {
        if (!textBlock.IsLoaded)
            return;

        textBlock.Dispatcher.BeginInvoke(
            () => Update(textBlock),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void EnsureTransform(TextBlock textBlock)
    {
        var state = GetState(textBlock);
        if (!ReferenceEquals(textBlock.RenderTransform, state.Transform))
        {
            textBlock.RenderTransform = state.Transform;
            textBlock.RenderTransformOrigin = new System.Windows.Point(0, 0.5);
        }
    }

    private static MarqueeState GetState(TextBlock textBlock)
    {
        if (!States.TryGetValue(textBlock, out var state))
        {
            state = new MarqueeState();
            States[textBlock] = state;
        }

        return state;
    }

    private static void Update(TextBlock textBlock)
    {
        if (!GetIsEnabled(textBlock) || !textBlock.IsVisible)
            return;

        EnsureTransform(textBlock);
        var state = GetState(textBlock);

        var viewportWidth = GetViewportWidth(textBlock);
        if (viewportWidth <= 0)
            return;

        textBlock.TextTrimming = TextTrimming.None;
        textBlock.TextWrapping = TextWrapping.NoWrap;
        textBlock.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        textBlock.VerticalAlignment = VerticalAlignment.Center;
        textBlock.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = textBlock.DesiredSize.Width;

        if (textWidth <= viewportWidth + 1)
        {
            Stop(textBlock);
            textBlock.ClearValue(FrameworkElement.WidthProperty);
            textBlock.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            textBlock.TextAlignment = TextAlignment.Center;
            return;
        }

        if (!state.IsActive)
        {
            state.SavedTrimming = textBlock.TextTrimming;
            state.SavedWidth = double.IsNaN(textBlock.Width) ? null : textBlock.Width;
        }

        textBlock.Width = textWidth;
        textBlock.TextAlignment = TextAlignment.Left;

        var gap = 24.0;
        var isRtl = textBlock.FlowDirection == System.Windows.FlowDirection.RightToLeft;
        var from = isRtl ? -(textWidth + gap) : viewportWidth + gap;
        var to = isRtl ? viewportWidth + gap : -(textWidth + gap);
        var travel = Math.Abs(to - from);
        var duration = TimeSpan.FromSeconds(Math.Max(2.5, travel / Math.Max(12.0, GetSpeed(textBlock))));

        state.Transform.BeginAnimation(TranslateTransform.XProperty, null);
        state.Transform.X = from;

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            BeginTime = TimeSpan.FromSeconds(0.8),
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = false,
            FillBehavior = FillBehavior.HoldEnd
        };

        state.Transform.BeginAnimation(TranslateTransform.XProperty, animation);
        state.IsActive = true;
    }

    private static double GetViewportWidth(TextBlock textBlock)
    {
        if (textBlock.Parent is FrameworkElement parent && parent.ActualWidth > 0)
            return parent.ActualWidth;

        return textBlock.ActualWidth;
    }

    private static void Stop(TextBlock textBlock)
    {
        if (!States.TryGetValue(textBlock, out var state))
            return;

        state.Transform.BeginAnimation(TranslateTransform.XProperty, null);
        state.Transform.X = 0;

        if (state.SavedWidth.HasValue)
            textBlock.Width = state.SavedWidth.Value;
        else
            textBlock.ClearValue(FrameworkElement.WidthProperty);

        textBlock.TextTrimming = state.SavedTrimming;
        textBlock.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        textBlock.TextAlignment = TextAlignment.Center;
        state.IsActive = false;
    }
}
