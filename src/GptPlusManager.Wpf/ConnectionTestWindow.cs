using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GptPlusManager.Core.Codex;

namespace GptPlusManager.Wpf;

/// <summary>
/// 自检结果弹窗：逐步展示三步的结果，未通过时给出下一步该做什么。
///
/// <para>刻意逐步展示而不是只报"失败"——问题出在哪一层（连不上 / 模型名错 /
/// 协议不兼容）决定了对策完全不同，混成一句话用户还是不知道该怎么办。</para>
/// </summary>
public sealed class ConnectionTestWindow : Window
{
    private ConnectionTestWindow(Window? owner, string model, ConnectionTestReport report)
    {
        Title = "模型自检";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        ShowInTaskbar = false;

        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = report.Success ? "✓ 自检通过" : "✕ 自检未通过",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = report.Success ? Brush("GreenBrush") : Brush("AmberBrush"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        // 报告是按模型给出的，标题里写明是哪个——测过多个模型时才知道看的是哪一份。
        panel.Children.Add(new TextBlock
        {
            Text = "模型：" + model,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brush("MutedBrush"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(report.Diagnosis))
        {
            panel.Children.Add(new TextBlock
            {
                Text = report.Diagnosis,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextBrush"),
                Margin = new Thickness(0, 8, 0, 0),
                LineHeight = 20,
            });
        }

        if (report.Steps.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "检查过程",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("MutedBrush"),
                Margin = new Thickness(0, 18, 0, 6),
            });
        }

        foreach (var step in report.Steps)
        {
            var row = new Border
            {
                Background = Brush("InputBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = (step.Passed ? "✓ " : "✕ ") + step.Name,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = step.Passed ? Brush("TextBrush") : Brush("AmberBrush"),
                        },
                        new TextBlock
                        {
                            Text = step.Detail,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 11,
                            Margin = new Thickness(0, 3, 0, 0),
                            Foreground = step.Passed ? Brush("MutedBrush") : Brush("TextBrush"),
                        },
                    },
                },
            };
            panel.Children.Add(row);
        }

        var close = new Button
        {
            Content = "关闭",
            Width = 88,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)FindResource("ModernButton"),
            IsCancel = true,
        };
        panel.Children.Add(close);

        Content = new Border
        {
            Padding = new Thickness(22),
            Background = Brush("CanvasBrush"),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel },
        };
    }

    public static void Show(Window? owner, string model, ConnectionTestReport report) =>
        new ConnectionTestWindow(owner, model, report).ShowDialog();

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
