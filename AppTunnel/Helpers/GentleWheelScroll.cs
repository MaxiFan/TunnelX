using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AppTunnel.Helpers;

/// <summary>
/// Caps mouse-wheel scroll speed on pixel ScrollViewers without smooth/lerp animation.
/// One notch (~120 delta) moves a fixed comfortable step instead of WPF's larger default jump.
/// </summary>
public static class GentleWheelScroll
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(GentleWheelScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Device-independent pixels scrolled per standard mouse notch (delta 120).</summary>
    private const double PixelsPerNotch = 52;

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        if ((bool)e.NewValue)
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject host)
            return;

        var scrollViewer = FindScrollViewer(host);
        if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0)
            return;

        // Let an inner scrollable region consume the wheel first when it still has room.
        // OriginalSource may be a Run/Inline (not a Visual) — walk safely.
        if (e.OriginalSource is DependencyObject origin
            && FindScrollViewer(origin) is { } inner
            && !ReferenceEquals(inner, scrollViewer)
            && IsAncestorOf(scrollViewer, inner)
            && CanScrollFurther(inner, e.Delta))
        {
            return;
        }

        e.Handled = true;
        var notches = e.Delta / 120.0;
        var next = Math.Clamp(
            scrollViewer.VerticalOffset - notches * PixelsPerNotch,
            0,
            scrollViewer.ScrollableHeight);
        scrollViewer.ScrollToVerticalOffset(next);
    }

    private static bool CanScrollFurther(ScrollViewer viewer, int delta)
    {
        if (delta > 0)
            return viewer.VerticalOffset > 0.5;
        return viewer.VerticalOffset < viewer.ScrollableHeight - 0.5;
    }

    private static bool IsAncestorOf(DependencyObject ancestor, DependencyObject node)
    {
        for (var current = GetParentSafe(node); current != null; current = GetParentSafe(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root == null)
            return null;
        if (root is ScrollViewer self)
            return self;

        // Prefer a ScrollViewer that owns this element (walk up first).
        // Start may be Inline/Run — not a Visual — so use safe parent walk.
        for (var parent = GetParentSafe(root); parent != null; parent = GetParentSafe(parent))
        {
            if (parent is ScrollViewer sv)
                return sv;
        }

        return FindScrollViewerDescending(AsVisualRoot(root));
    }

    private static ScrollViewer? FindScrollViewerDescending(DependencyObject? root)
    {
        if (root == null || !IsVisualTreeNode(root))
            return null;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            var nested = FindScrollViewerDescending(child);
            if (nested != null)
                return nested;
        }

        return null;
    }

    /// <summary>
    /// VisualTreeHelper only accepts Visual/Visual3D. Inlines like <see cref="Run"/> must
    /// climb the logical tree until a real visual (usually TextBlock) is found.
    /// </summary>
    private static DependencyObject? GetParentSafe(DependencyObject? node)
    {
        if (node == null)
            return null;

        if (IsVisualTreeNode(node))
            return VisualTreeHelper.GetParent(node);

        return LogicalTreeHelper.GetParent(node);
    }

    private static DependencyObject? AsVisualRoot(DependencyObject node)
    {
        if (IsVisualTreeNode(node))
            return node;

        for (var current = LogicalTreeHelper.GetParent(node); current != null; current = LogicalTreeHelper.GetParent(current))
        {
            if (IsVisualTreeNode(current))
                return current;
        }

        return null;
    }

    private static bool IsVisualTreeNode(DependencyObject node) =>
        node is Visual or Visual3D;
}
