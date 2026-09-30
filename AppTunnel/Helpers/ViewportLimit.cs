using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AppTunnel.Helpers;

/// <summary>
/// Caps a framework element's <see cref="FrameworkElement.MaxWidth"/> to its ancestor
/// <see cref="ScrollViewer.ViewportWidth"/> so star-sized children can trim instead of
/// stretching the item past the visible window (WPF often measures list content at infinity).
/// </summary>
public static class ViewportLimit
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ViewportLimit),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private sealed class State
    {
        public ScrollViewer? Viewer;
        public SizeChangedEventHandler? ViewerHandler;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

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
            element.Loaded += OnElementLoaded;
            element.Unloaded += OnElementUnloaded;
            QueueApply(element);
        }
        else
        {
            element.Loaded -= OnElementLoaded;
            element.Unloaded -= OnElementUnloaded;
            UnhookViewer(element);
            element.ClearValue(FrameworkElement.MaxWidthProperty);
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            QueueApply(element);
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            UnhookViewer(element);
    }

    private static void QueueApply(FrameworkElement element)
    {
        if (!element.IsLoaded)
            return;

        element.Dispatcher.BeginInvoke(
            () => Apply(element),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Apply(FrameworkElement element)
    {
        if (!GetIsEnabled(element) || !element.IsLoaded)
            return;

        var viewer = FindAncestorScrollViewer(element);
        if (viewer == null)
            return;

        HookViewer(element, viewer);

        var width = viewer.ViewportWidth;
        if (width <= 0)
            width = viewer.ActualWidth;
        if (width <= 0)
            return;

        if (double.IsNaN(element.MaxWidth) || Math.Abs(element.MaxWidth - width) > 0.5)
            element.MaxWidth = width;
    }

    private static void HookViewer(FrameworkElement element, ScrollViewer viewer)
    {
        var state = States.GetOrCreateValue(element);
        if (ReferenceEquals(state.Viewer, viewer))
            return;

        UnhookViewer(element);
        state.Viewer = viewer;
        state.ViewerHandler = (_, _) => Apply(element);
        viewer.SizeChanged += state.ViewerHandler;
    }

    private static void UnhookViewer(FrameworkElement element)
    {
        if (!States.TryGetValue(element, out var state))
            return;

        if (state.Viewer != null && state.ViewerHandler != null)
            state.Viewer.SizeChanged -= state.ViewerHandler;

        state.Viewer = null;
        state.ViewerHandler = null;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        for (var current = VisualTreeHelper.GetParent(start);
             current != null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer viewer)
                return viewer;
        }

        return null;
    }
}
