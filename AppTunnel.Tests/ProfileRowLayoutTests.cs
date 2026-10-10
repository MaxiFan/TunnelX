using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace AppTunnel.Tests;

/// <summary>
/// Layout contract for issue #68 (actions stay inside a narrow row) and #82
/// (the same row mirrors with FlowDirection). Runs on Windows CI (STA).
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
                MeasureRow(FlowDirection.LeftToRight, actionsOnLeft: false);
                MeasureRow(FlowDirection.RightToLeft, actionsOnLeft: true);
                AssertInkOnStartEdge(FlowDirection.LeftToRight, inkOnRight: false);
                AssertInkOnStartEdge(FlowDirection.RightToLeft, inkOnRight: true);
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

    [Fact]
    public void List_row_templates_bind_app_flow_direction()
    {
        var connection = System.IO.File.ReadAllText(RepoFile("AppTunnel/Views/ConnectionTabView.xaml"));
        var profiles = Slice(connection, "<ListBox x:Name=\"ProfilesList\"", "</ListBox.ItemTemplate>");
        Assert.Contains(
            "FlowDirection=\"{Binding DataContext.AppFlowDirection, RelativeSource={RelativeSource AncestorType=ListBox}}\"",
            profiles,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOf(profiles, "FlowDirection=\"LeftToRight\""));

        var subscriptions = Slice(connection, "<ItemsControl ItemsSource=\"{Binding Subscriptions}\">", "</ItemsControl.ItemTemplate>");
        Assert.Contains(
            "FlowDirection=\"{Binding DataContext.AppFlowDirection, RelativeSource={RelativeSource AncestorType=UserControl}}\"",
            subscriptions,
            StringComparison.Ordinal);

        var apps = System.IO.File.ReadAllText(RepoFile("AppTunnel/Views/AppsTabView.xaml"));
        var cardStyle = Slice(apps, "<Style x:Key=\"AppRowCard\"", "</Style>");
        Assert.DoesNotContain("FlowDirection", cardStyle, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(apps, "FlowDirection=\"{Binding DataContext.AppFlowDirection, RelativeSource={RelativeSource AncestorType=UserControl}}\""));

        var history = System.IO.File.ReadAllText(RepoFile("AppTunnel/Views/HistoryTabView.xaml"));
        Assert.Contains(
            "FlowDirection=\"{Binding DataContext.AppFlowDirection, RelativeSource={RelativeSource AncestorType=UserControl}}\"",
            history,
            StringComparison.Ordinal);
    }

    private static void MeasureRow(FlowDirection flow, bool actionsOnLeft)
    {
        const double rowWidth = 640;

        var host = new Grid
        {
            FlowDirection = FlowDirection.LeftToRight,
            Width = rowWidth,
            Height = 48
        };

        var grid = new Grid
        {
            MinWidth = 0,
            FlowDirection = flow
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 0 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star), MinWidth = 0 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = "Telegram = t.me/SOSK-very-long-config-title-that-used-to-overflow",
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Left,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var ping = new TextBlock
        {
            Text = "اتصال: SOCKS5 connect failed (code 1)",
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Left,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            FlowDirection = flow
        };
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
        host.Children.Add(grid);

        host.Measure(new Size(rowWidth, 48));
        host.Arrange(new Rect(0, 0, rowWidth, 48));
        host.UpdateLayout();

        var nameSpan = VisualSpan(name, host);
        var actionSpan = VisualSpan(actions, host);
        var pingSpan = VisualSpan(pingButton, host);
        var editSpan = VisualSpan(editButton, host);

        Assert.True(actions.ActualWidth > 40, "Action buttons should keep their desired width.");
        Assert.InRange(actionSpan.Left, -0.5, rowWidth);
        Assert.InRange(actionSpan.Right, 0, rowWidth + 0.5);
        Assert.True(name.ActualWidth + ping.ActualWidth + actions.ActualWidth <= rowWidth + 1);
        Assert.Equal(flow, name.FlowDirection);
        Assert.Equal(TextAlignment.Left, name.TextAlignment);

        var unconstrained = new TextBlock
        {
            Text = name.Text,
            FontSize = name.FontSize,
            TextWrapping = TextWrapping.NoWrap
        };
        unconstrained.Measure(new Size(double.PositiveInfinity, 48));
        Assert.True(name.ActualWidth < unconstrained.DesiredSize.Width - 8, "Long names must ellipsize inside the star column.");

        if (actionsOnLeft)
        {
            Assert.True(actionSpan.Right <= nameSpan.Left + 1, "RTL rows place actions to the left of the name.");
            Assert.True(editSpan.Right <= pingSpan.Left + 1, "RTL button order starts at the right of the action group.");
        }
        else
        {
            Assert.True(nameSpan.Right <= actionSpan.Left + 1, "LTR rows place actions to the right of the name.");
            Assert.True(pingSpan.Right <= editSpan.Left + 1, "LTR button order starts at the left of the action group.");
        }
    }

    private static void AssertInkOnStartEdge(FlowDirection flow, bool inkOnRight)
    {
        const int width = 220;
        const int height = 32;

        var text = new TextBlock
        {
            Text = "سلام",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            Width = width,
            Height = height,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            FlowDirection = flow,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        var parameters = new HwndSourceParameters("profile-row-align")
        {
            Width = width,
            Height = height,
            WindowStyle = 0
        };
        using var source = new HwndSource(parameters);
        source.RootVisual = text;
        text.Measure(new Size(width, height));
        text.Arrange(new Rect(0, 0, width, height));
        text.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(text);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);

        var left = FirstInkColumn(pixels, width, height, fromLeft: true);
        var right = FirstInkColumn(pixels, width, height, fromLeft: false);
        Assert.True(left >= 0 && right >= 0, "Persian sample text should render so alignment can be checked.");

        var leftGap = left;
        var rightGap = width - 1 - right;
        if (inkOnRight)
            Assert.True(rightGap < leftGap, $"RTL start alignment should keep text on the right (leftGap={leftGap}, rightGap={rightGap}).");
        else
            Assert.True(leftGap < rightGap, $"LTR start alignment should keep text on the left (leftGap={leftGap}, rightGap={rightGap}).");
    }

    private static int FirstInkColumn(byte[] pixels, int width, int height, bool fromLeft)
    {
        var start = fromLeft ? 0 : width - 1;
        var step = fromLeft ? 1 : -1;
        for (var x = start; x >= 0 && x < width; x += step)
        {
            for (var y = 0; y < height; y++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i + 3] > 16 && pixels[i] < 200)
                    return x;
            }
        }

        return -1;
    }

    private static (double Left, double Right) VisualSpan(FrameworkElement element, UIElement relativeTo)
    {
        var origin = element.TranslatePoint(new Point(0, 0), relativeTo);
        var far = element.TranslatePoint(new Point(element.ActualWidth, 0), relativeTo);
        return (Math.Min(origin.X, far.X), Math.Max(origin.X, far.X));
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing marker {startMarker}");
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing marker {endMarker}");
        return text[start..end];
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string RepoFile(string relative)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "AppTunnel.sln")))
                return System.IO.Path.Combine(dir.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
