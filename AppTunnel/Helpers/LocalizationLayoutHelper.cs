using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AppTunnel.Services;
using FlowDirection = System.Windows.FlowDirection;

namespace AppTunnel.Helpers;

/// <summary>
/// Applies app language flow and Persian text alignment to a visual subtree.
/// Refreshes layout bindings so RTL/LTR updates apply even when bindings were created earlier.
/// </summary>
public static class LocalizationLayoutHelper
{
    public static void ApplyTo(DependencyObject root)
    {
        var loc = LocalizationService.Instance;
        ApplyTo(root, loc.FlowDirection, loc.TextAlignment, new HashSet<DependencyObject>());
        RefreshLayoutBindings(root);
    }

    private static void ApplyTo(
        DependencyObject node,
        FlowDirection flow,
        TextAlignment align,
        HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node))
            return;

        if (node is FrameworkElement fe)
        {
            if (ShouldApplyLocalFlowDirection(fe))
                fe.FlowDirection = flow;

            switch (fe)
            {
                case TextBlock tb when !TextBlockFlags.GetUseEmojiFont(tb):
                    ApplyTextBlockAlignment(tb, align);
                    break;
                case System.Windows.Controls.TextBox box when box.FlowDirection != FlowDirection.LeftToRight:
                    if (!HasBinding(box, System.Windows.Controls.TextBox.TextAlignmentProperty))
                        box.TextAlignment = align;
                    break;
            }
        }

        if (node is Visual)
        {
            try
            {
                var visualCount = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < visualCount; i++)
                    ApplyTo(VisualTreeHelper.GetChild(node, i), flow, align, visited);
            }
            catch (InvalidOperationException)
            {
                // Some logical-only nodes can still surface here during template apply.
            }
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            ApplyTo(child, flow, align, visited);
    }

    private static bool ShouldApplyLocalFlowDirection(FrameworkElement fe)
    {
        if (HasBinding(fe, FrameworkElement.FlowDirectionProperty))
            return false;

        if (TextBlockFlags.GetUseEmojiFont(fe))
            return false;

        if (fe.ReadLocalValue(FrameworkElement.FlowDirectionProperty) is FlowDirection.LeftToRight)
            return false;

        return fe.ReadLocalValue(FrameworkElement.FlowDirectionProperty) is FlowDirection.RightToLeft;
    }

    private static void ApplyTextBlockAlignment(TextBlock tb, TextAlignment align)
    {
        if (HasBinding(tb, TextBlock.TextAlignmentProperty))
            return;

        if (TextBlockFlags.GetUseEmojiFont(tb))
            return;

        if (tb.FlowDirection == FlowDirection.LeftToRight)
            return;

        tb.TextAlignment = align;
    }

    public static void RefreshLayoutBindings(DependencyObject root)
    {
        RefreshLayoutBindings(root, new HashSet<DependencyObject>());
    }

    private static void RefreshLayoutBindings(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node))
            return;

        if (node is FrameworkElement fe)
        {
            BindingOperations.GetBindingExpression(fe, FrameworkElement.FlowDirectionProperty)?.UpdateTarget();
            BindingOperations.GetBindingExpression(fe, FrameworkElement.HorizontalAlignmentProperty)?.UpdateTarget();

            if (fe is TextBlock tb)
                BindingOperations.GetBindingExpression(tb, TextBlock.TextAlignmentProperty)?.UpdateTarget();
        }

        if (node is Visual)
        {
            try
            {
                var visualCount = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < visualCount; i++)
                    RefreshLayoutBindings(VisualTreeHelper.GetChild(node, i), visited);
            }
            catch (InvalidOperationException)
            {
            }
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            RefreshLayoutBindings(child, visited);
    }

    private static bool HasBinding(DependencyObject element, DependencyProperty property)
        => BindingOperations.GetBindingExpressionBase(element, property) != null;
}
