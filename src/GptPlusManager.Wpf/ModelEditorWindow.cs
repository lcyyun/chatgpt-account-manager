using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GptPlusManager.Core.Codex;

namespace GptPlusManager.Wpf;

/// <summary>
/// 单个模型的编辑弹窗：模型 ID、显示名、上下文窗口、是否支持图片。
///
/// <para>纯 code-behind 构建，沿用 <see cref="PreviewWindow"/> 的做法——
/// 这类小表单不值得为它单独开一对 XAML 文件。</para>
/// </summary>
public sealed class ModelEditorWindow : Window
{
    private readonly TextBox _slugBox;
    private readonly TextBox _displayBox;
    private readonly TextBox _windowBox;
    private readonly CheckBox _imagesCheck;
    private readonly ComboBox _protocolBox;
    private ProviderModel? _result;

    private ModelEditorWindow(Window? owner, ProviderModel model, bool isNew)
    {
        Title = isNew ? "添加模型" : "编辑模型";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        ShowInTaskbar = false;

        _slugBox = Field(model.Slug);
        _displayBox = Field(model.DisplayName);
        _windowBox = Field(model.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        _imagesCheck = new CheckBox
        {
            Content = "支持图片输入（vision）",
            IsChecked = model.SupportsImages,
            Foreground = Brush("TextBrush"),
            Margin = new Thickness(0, 4, 0, 0),
        };

        // 工具协议：默认经典模式。第三方模型几乎都按标准 function calling 训练，
        // 用 Codex 的私有 code mode 会把工具调用当文本吐出来。
        _protocolBox = new ComboBox
        {
            Style = (Style)FindResource("ModernTextBox"),
            Margin = new Thickness(0),
        };
        _protocolBox.Items.Add("经典模式 — 标准 function calling（推荐）");
        _protocolBox.Items.Add("Code mode — Codex 私有协议（仅官方后端支持）");
        _protocolBox.SelectedIndex = model.Protocol == ToolProtocol.CodeMode ? 1 : 0;

        var save = new Button
        {
            Content = isNew ? "添加" : "保存",
            Width = 96,
            Style = (Style)FindResource("PrimaryButton"),
        };
        save.Click += (_, _) => Commit();

        var cancel = new Button
        {
            Content = "取消",
            Width = 88,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("ModernButton"),
            IsCancel = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var panel = new StackPanel();
        panel.Children.Add(Label("模型 ID（端点定义的写法，区分大小写）"));
        panel.Children.Add(_slugBox);
        panel.Children.Add(Label("显示名（留空则用模型 ID）", top: 12));
        panel.Children.Add(_displayBox);
        panel.Children.Add(Label($"上下文窗口（token，留空按 {ModelCatalogBuilder.DefaultContextWindow:N0}）", top: 12));
        panel.Children.Add(_windowBox);
        panel.Children.Add(new TextBlock
        {
            Text = "按这个模型的实际能力填。填大了会在长对话里被端点拒绝，填小了会过早触发压缩。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brush("MutedBrush"),
        });
        panel.Children.Add(_imagesCheck);
        panel.Children.Add(Label("工具协议", top: 14));
        panel.Children.Add(_protocolBox);
        panel.Children.Add(new TextBlock
        {
            Text = "经典模式用标准 function calling，第三方模型与中转站基本都支持。" +
                   "Code mode 是 Codex 的私有协议，只有官方后端认得——" +
                   "选错会表现为模型把工具调用当普通文字输出、命令不执行。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brush("MutedBrush"),
        });
        panel.Children.Add(buttons);

        Content = new Border
        {
            Padding = new Thickness(22),
            Background = Brush("CanvasBrush"),
            Child = panel,
        };

        Loaded += (_, _) => { _slugBox.Focus(); _slugBox.SelectAll(); };
    }

    /// <summary>弹出编辑框；用户确认时返回新对象，取消返回 null。</summary>
    public static ProviderModel? ShowDialog(Window? owner, ProviderModel model, bool isNew)
    {
        var window = new ModelEditorWindow(owner, model, isNew);
        window.ShowDialog();
        return window._result;
    }

    private void Commit()
    {
        var slug = _slugBox.Text.Trim();
        if (slug.Length == 0)
        {
            MessageBox.Show(this, "模型 ID 不能为空。", "添加模型",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _slugBox.Focus();
            return;
        }

        // 非数字一律当作"用默认"，不弹错——这个字段是可选的。
        int? window = int.TryParse(_windowBox.Text.Trim(), out var parsed) && parsed > 0 ? parsed : null;

        _result = new ProviderModel
        {
            Slug = slug,
            DisplayName = _displayBox.Text.Trim(),
            ContextWindow = window,
            SupportsImages = _imagesCheck.IsChecked == true,
            Protocol = _protocolBox.SelectedIndex == 1 ? ToolProtocol.CodeMode : ToolProtocol.Classic,
            Enabled = true,
        };
        _result.Normalize();
        Close();
    }

    private static TextBox Field(string value) => new()
    {
        Text = value,
        Style = (Style)Application.Current.FindResource("ModernTextBox"),
    };

    private static TextBlock Label(string text, double top = 0) => new()
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(0, top, 0, 4),
        Foreground = Brush("MutedBrush"),
    };

    private static System.Windows.Media.Brush Brush(string key) =>
        (System.Windows.Media.Brush)Application.Current.FindResource(key);
}
