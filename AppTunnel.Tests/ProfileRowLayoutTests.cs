using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace AppTunnel.Tests;

/// <summary>
/// Layout contract for issue #68: truncatable cells must yield to the action column.
/// Runs on Windows CI (STA); no-ops on non-Windows agents.
/// </summary>
public class ProfileRowLayoutTests
{
    [Fact]
    public void Long_name_and_ping_result_keep_actions_inside_row()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                MeasureRow();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught != null)
            throw caught;
    }

    private static void MeasureRow()
    {
        const double rowWidth = 640;

        var grid = new Grid { MinWidth = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 0 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star), MinWidth = 0 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = "Telegram = t.me/SOSK-very-long-config-title-that-used-to-overflow",
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var ping = new TextBlock
        {
            Text = "اتصال: SOCKS5 connect failed (code 1)",
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var pingButton = new Button { Content = "پینگ", MinWidth = 44, Padding = new Thickness(7, 4, 7, 4) };
        var editButton = new Button { Content = "ویرایش", Padding = new Thickness(8, 4, 8, 4) };
        actions.Children.Add(pingButton);
        actions.Children.Add(editButton);

        Grid.SetColumn(name, 0);
        Grid.SetColumn(ping, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(name);
        grid.Children.Add(ping);
        grid.Children.Add(actions);

        grid.Measure(new Size(rowWidth, 48));
        grid.Arrange(new Rect(0, 0, rowWidth, 48));

        var actionLeft = actions.TranslatePoint(new Point(0, 0), grid).X;
        var actionRight = actionLeft + actions.ActualWidth;

        Assert.True(actions.ActualWidth > 40, "Action buttons should keep their desired width.");
        Assert.InRange(actionLeft, 0, rowWidth);
        Assert.InRange(actionRight, 0, rowWidth + 0.5);
        Assert.True(name.ActualWidth + ping.ActualWidth + actions.ActualWidth <= rowWidth + 1);
    }
}
