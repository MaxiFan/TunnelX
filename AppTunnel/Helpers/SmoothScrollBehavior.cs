using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace AppTunnel.Helpers;

/// <summary>
/// Pixel-based mouse-wheel scrolling with per-frame interpolation (no WPF offset animations).
/// </summary>
public static class SmoothScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private const double WheelScale = 0.28;
    private const double LerpFactor = 0.22;
    private const double StopThreshold = 0.5;

    private static readonly Dictionary<ScrollViewer, ScrollState> States = new();
    private static readonly object StatesLock = new();
    private static bool _renderHooked;

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.Loaded += OnHostLoaded;
            element.Unloaded += OnHostUnloaded;
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            element.Loaded -= OnHostLoaded;
            element.Unloaded -= OnHostUnloaded;
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        if (FindScrollViewer(sender as DependencyObject) is not { } scrollViewer)
            return;

        scrollViewer.CanContentScroll = false;
        scrollViewer.PanningMode = PanningMode.None;
    }

    private static void OnHostUnloaded(object sender, RoutedEventArgs e)
    {
        if (FindScrollViewer(sender as DependencyObject) is not { } scrollViewer)
            return;

        lock (StatesLock)
            States.Remove(scrollViewer);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject host)
            return;

        var scrollViewer = FindScrollViewer(host);
        if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0)
            return;

        e.Handled = true;

        var state = GetOrCreateState(scrollViewer);
        if (!state.IsAnimating)
            state.TargetOffset = scrollViewer.VerticalOffset;

        var deltaPixels = -e.Delta * WheelScale;
        state.TargetOffset = Math.Clamp(
            state.TargetOffset + deltaPixels,
            0,
            scrollViewer.ScrollableHeight);

        EnsureRenderLoop();
    }

    private static ScrollState GetOrCreateState(ScrollViewer scrollViewer)
    {
        lock (StatesLock)
        {
            if (!States.TryGetValue(scrollViewer, out var state))
            {
                state = new ScrollState(scrollViewer);
                States[scrollViewer] = state;
            }

            return state;
        }
    }

    private static void EnsureRenderLoop()
    {
        if (_renderHooked)
            return;

        _renderHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        ScrollState[] active;
        lock (StatesLock)
        {
            active = States.Values.Where(s => s.HasPendingTarget).ToArray();
            if (active.Length == 0)
            {
                _renderHooked = false;
                CompositionTarget.Rendering -= OnRendering;
                return;
            }
        }

        foreach (var state in active)
        {
            var scrollViewer = state.ScrollViewer;
            if (!scrollViewer.IsVisible)
            {
                state.TargetOffset = scrollViewer.VerticalOffset;
                state.IsAnimating = false;
                continue;
            }

            var max = scrollViewer.ScrollableHeight;
            state.TargetOffset = Math.Clamp(state.TargetOffset, 0, max);

            var current = scrollViewer.VerticalOffset;
            var diff = state.TargetOffset - current;

            if (Math.Abs(diff) <= StopThreshold)
            {
                if (Math.Abs(current - state.TargetOffset) > 0.01)
                    scrollViewer.ScrollToVerticalOffset(state.TargetOffset);
                state.IsAnimating = false;
                continue;
            }

            state.IsAnimating = true;
            var step = diff * LerpFactor;
            scrollViewer.ScrollToVerticalOffset(current + step);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root == null)
            return null;

        if (root is ScrollViewer scrollViewer)
            return scrollViewer;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null)
                return found;
        }

        return null;
    }

    private sealed class ScrollState
    {
        public ScrollState(ScrollViewer scrollViewer) => ScrollViewer = scrollViewer;

        public ScrollViewer ScrollViewer { get; }
        public double TargetOffset { get; set; }
        public bool IsAnimating { get; set; }
        public bool HasPendingTarget => Math.Abs(ScrollViewer.VerticalOffset - TargetOffset) > StopThreshold;
    }
}
