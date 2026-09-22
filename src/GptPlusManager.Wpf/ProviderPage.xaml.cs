using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GptPlusManager.Core.Codex;
using GptPlusManager.Wpf.Services;

namespace GptPlusManager.Wpf;

/// <summary>
/// 「第三方」页面：模式切换、供应商增删改、密钥设置、配置预览、应用并重启。
/// 从主窗口的页面切换进入，不再是模态弹窗。
/// </summary>
public partial class ProviderPage : UserControl
{
    private readonly IProviderManagerService _service;
    private readonly List<ProviderDefinition> _providers = [];
    private string? _selectedId;
    private bool _loading;

    /// <summary>
    /// 正在把模型对象写回输入框。此期间输入框的 TextChanged 必须被忽略，
    /// 否则"写回"会被当成"用户编辑"再解析一遍，模型列表会被反复重写。
    /// </summary>
    private bool _syncing;
    private bool _keyRevealed;

    /// <summary>本次载入时是否自动修复了取 token 命令路径（用于提示用户）。</summary>
    private bool _selfHealed;

    public ProviderPage(IProviderManagerService service)
    {
        InitializeComponent();
        _service = service;
        ConfigPathText.Text = "配置文件：" + service.ConfigPath;
    }

    /// <summary>模式或供应商发生变化时触发，宿主窗口据此刷新状态栏。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>切到第三方模式后触发：Codex 已重启，账号页的"当前账号"等缓存需要刷新。</summary>
    public event EventHandler? CodexRestarted;

    /// <summary>进入本页时调用。</summary>
    public async Task ActivateAsync()
    {
        await LoadAsync();
        ApiKeyBox.Focus();
    }

    /// <summary>供宿主在切回本页时读取当前模式（标签上做提示用）。</summary>
    public Task<CodexConfigSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        _service.GetStatusAsync(cancellationToken);

    // ---------- 载入 ----------

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _providers.Clear();
            _providers.AddRange(await _service.LoadProvidersAsync());

            // 自愈：程序被移动或改名后，配置里记录的取 token 命令就会指向不存在的位置，
            // Codex 只会报"取不到密钥"这类无指向性的错。这里静默修回当前路径，
            // 用户不需要理解发生了什么。
            string? selfHealed = null;
            try
            {
                selfHealed = await _service.RepairTokenCommandPathAsync();
            }
            catch (Exception)
            {
                // 修复失败不影响页面加载；下面的警告会提示用户手动重新应用。
            }

            var snapshot = await _service.GetStatusAsync();
            _selfHealed = selfHealed is not null;

            OfficialRadio.IsChecked = snapshot.Mode == CodexRoutingMode.Official;
            ThirdPartyRadio.IsChecked = snapshot.Mode == CodexRoutingMode.ThirdParty;

            var modeName = snapshot.Mode == CodexRoutingMode.ThirdParty ? "第三方模式" : "官方模式";
            var warnings = new List<string>();
            if (snapshot.Mode == CodexRoutingMode.ThirdParty)
            {
                if (!snapshot.CatalogFileExists) warnings.Add("目录文件缺失，Codex 将无法启动");
                if (!snapshot.TokenCommandMatchesCurrentApp)
                {
                    warnings.Add("取密钥的命令路径已失效——Codex 会一直取不到 token。" +
                                 "路径指向的是本程序，移动或改名后就会这样。请点「应用并重启 Codex」重新写入");
                }
            }

            // 旧版危险布局：桌面版重写过 config.toml，把我们的结束标记搬到了文件末尾，
            // 于是用户整份配置看起来都在"可删范围"内。新版本已不会误删，
            // 但仍要提示重新应用一次把文件规整干净。
            if (snapshot.HasLegacyBlockLayout)
            {
                warnings.Add("config.toml 被桌面版重写过，托管标记位置异常。" +
                             "新版已能安全处理，但建议点一次「应用并重启 Codex」把文件规整好");
            }

            ModeSummaryText.Text =
                $"当前：{modeName}　·　模型：{snapshot.Model ?? "(官方默认)"}" +
                (warnings.Count > 0 ? "　⚠ " + string.Join("；", warnings) : string.Empty);

            if (_selfHealed)
            {
                // 自动修好了，但仍要告诉用户发生过什么，否则他会困惑于"为什么刚才不能用"。
                StatusText.Text = "检测到本程序位置已变化，已自动修正取密钥的命令路径。" +
                                  "重启 Codex 后生效。";
            }

            var activeId = await _service.GetActiveProviderIdAsync();
            RefreshProviderList(activeId);
            UpdateKeySummary(activeId);
        }
        finally
        {
            _loading = false;
        }
    }

    private async void UpdateKeySummary(string? activeId)
    {
        if (string.IsNullOrWhiteSpace(activeId))
        {
            KeyStatusSummary.Text = _providers.Count == 0 ? "尚未配置供应商" : "未应用到 Codex";
            return;
        }

        var hasKey = await _service.HasApiKeyAsync(activeId);
        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase));
        KeyStatusSummary.Text = provider is null
            ? "未应用到 Codex"
            : $"生效中：{provider.DisplayName}　密钥：{(hasKey ? "已设置" : "缺失")}";
    }

    private void RefreshProviderList(string? selectId = null)
    {
        var items = _providers.Select(p => new ProviderRow(p)).ToList();
        ProviderList.ItemsSource = items;

        var target = selectId ?? _selectedId;
        var match = target is null
            ? items.FirstOrDefault()
            : items.FirstOrDefault(i => string.Equals(i.Id, target, StringComparison.OrdinalIgnoreCase))
              ?? items.FirstOrDefault();

        ProviderList.SelectedItem = match;
        if (match is null)
        {
            ClearDetail();
            ShowDetail(false);
        }
    }

    private sealed class ProviderRow : System.ComponentModel.INotifyPropertyChanged
    {
        public ProviderRow(ProviderDefinition definition) => Definition = definition;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public ProviderDefinition Definition { get; }
        public string Id => Definition.Id;
        public string Title => string.IsNullOrWhiteSpace(Definition.DisplayName) ? Definition.Id : Definition.DisplayName;
        public string Subtitle => $"{Definition.Id} · {Definition.Models.Count} 个模型";

        /// <summary>
        /// 就地刷新标题，而不是重建整个列表。
        ///
        /// <para>重建会重置选中项并再次触发 SelectionChanged，而那个处理器会把供应商字段
        /// 写回输入框——用户在详情里每敲一个字都会被打断。通知式刷新没有这个回路。</para>
        /// </summary>
        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Subtitle)));
        }
    }

    /// <summary>
    /// 模型行的显示包装。
    ///
    /// <para>直接绑 <see cref="ProviderModel"/> 也行，但徽章要算字符串、开关要双向回写，
    /// 包一层能让 XAML 保持声明式，也避免把界面逻辑塞进 Core 模型。</para>
    /// </summary>
    private sealed class ModelRow : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly ProviderPage _owner;

        public ModelRow(ProviderModel model, ProviderPage owner)
        {
            Model = model;
            _owner = owner;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public ProviderModel Model { get; }
        public string Slug => Model.Slug;

        /// <summary>
        /// 上下文窗口徽章。显示的是<b>实际生效值</b>——模型没设时用默认值，
        /// 而不是什么都不显示，否则用户看不出这个模型到底按多少算。
        /// </summary>
        public bool HasWindow => true;

        public string WindowBadge => FormatWindow(EffectiveWindow);

        /// <summary>模型自己设的值；未设时为 null。</summary>
        public int? OwnWindow => Model.ContextWindow;

        private int EffectiveWindow => Model.ContextWindow is > 0
            ? Model.ContextWindow!.Value
            : ModelCatalogBuilder.DefaultContextWindow;

        private static string FormatWindow(int tokens) => tokens switch
        {
            >= 1_000_000 when tokens % 1_000_000 == 0 => $"{tokens / 1_000_000}M",
            >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
            >= 1000 => $"{tokens / 1000}K",
            _ => tokens.ToString(),
        };

        public bool HasFlags => true;

        /// <summary>
        /// 徽章文案：图片能力与工具协议。
        /// 协议只在非常规值（Code mode）时显示——它是"多数情况会坏"的信号，
        /// 摆在行上比藏进编辑框更容易发现问题。
        /// </summary>
        public string FlagBadge
        {
            get
            {
                var parts = new List<string>();
                if (Model.SupportsImages) parts.Add("vision");
                if (Model.Protocol == ToolProtocol.CodeMode) parts.Add("code mode");
                return string.Join(" · ", parts);
            }
        }

        /// <summary>开关双向回写：关掉的模型不参与目录生成。</summary>
        public bool Enabled
        {
            get => Model.Enabled;
            set
            {
                if (Model.Enabled == value) return;
                Model.Enabled = value;
                _owner.OnModelToggled();
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Enabled)));
            }
        }

        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Slug)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasWindow)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(WindowBadge)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasFlags)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FlagBadge)));
        }
    }

    /// <summary>模型行增删改后同步到供应商对象并刷新界面。</summary>
    private void OnModelToggled()
    {
        SyncModelsFromRows();
        UpdateModelEmptyHint();
    }

    private void SyncModelsFromRows()
    {
        if (Selected is not { } provider) return;
        provider.Models = [.. ModelList.ItemsSource.Cast<ModelRow>().Select(r => r.Model)];
    }

    private void UpdateModelEmptyHint()
    {
        var empty = ModelList.ItemsSource is null || !ModelList.ItemsSource.Cast<ModelRow>().Any();
        ModelEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty && Selected is { } provider && provider.Models.Count > 0)
        {
            // 行列表还没渲染完，以数据为准。
            ModelEmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderModels(ProviderDefinition provider)
    {
        ModelList.ItemsSource = provider.Models.Select(m => new ModelRow(m, this)).ToList();
        UpdateModelEmptyHint();
    }

    private void ShowDetail(bool visible)
    {
        EmptyStatePanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        DetailScroller.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // 没有选中项时"删除"是空操作，禁掉比让人点了没反应好。
        DeleteProviderButton.IsEnabled = visible;
        ProviderIdBox.IsEnabled = visible;
        DisplayNameBox.IsEnabled = visible;
        BaseUrlBox.IsEnabled = visible;
        ApiKeyBox.IsEnabled = visible;
        RevealKeyButton.IsEnabled = visible;
        ApiKeyPlainBox.IsEnabled = visible;
        CommandAuthRadio.IsEnabled = visible;
        PlainTokenAuthRadio.IsEnabled = visible;
        AddModelButton.IsEnabled = visible;
        FetchModelsButton.IsEnabled = visible;
    }

    private void ClearDetail()
    {
        ProviderIdBox.Text = string.Empty;
        DisplayNameBox.Text = string.Empty;
        BaseUrlBox.Text = string.Empty;
        ApiKeyBox.Clear();
        ApiKeyPlainBox.Text = string.Empty;
        KeyStatusText.Text = string.Empty;
        ModelList.ItemsSource = null;
        ModelEmptyHint.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
        CommandAuthRadio.IsChecked = true;
        CustomToolsCheck.IsChecked = false;
        WebSearchCheck.IsChecked = false;
        UpdateCapabilityHint();
    }

    private ProviderDefinition? Selected => (ProviderList.SelectedItem as ProviderRow)?.Definition;

    // ---------- 列表操作 ----------

    private async void ProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 这里刻意不检查 _loading：LoadAsync 期间也要把详情填上，否则会出现
        // "左侧高亮已选中、右侧一片空白"的状态。防止"程序化写入被当成用户编辑"
        // 是 _syncing 的职责，由下面真正写字段时开启。
        var provider = Selected;
        if (provider is null) return;

        _loading = true;
        _syncing = true;
        try
        {
            _selectedId = provider.Id;
            ShowDetail(true);
            ProviderIdBox.Text = provider.Id;
            DisplayNameBox.Text = provider.DisplayName;
            BaseUrlBox.Text = provider.BaseUrl;
            CommandAuthRadio.IsChecked = provider.AuthMode == ProviderAuthMode.Command;
            PlainTokenAuthRadio.IsChecked = provider.AuthMode == ProviderAuthMode.PlainToken;
            CustomToolsCheck.IsChecked = provider.SupportsCustomTools;
            WebSearchCheck.IsChecked = provider.SupportsWebSearch;
            UpdateCapabilityHint();
            RenderModels(provider);

            // 永远不把已保存的密钥填回输入框：读不出来时就显示"已保存"，
            // 让用户知道留空 = 不改动。
            var hasKey = await _service.HasApiKeyAsync(provider.Id);
            ApiKeyBox.Clear();
            ApiKeyPlainBox.Text = string.Empty;
            SetKeyRevealed(false);
            KeyStatusText.Text = hasKey ? "已保存密钥（留空表示不修改）" : "尚未设置密钥";
        }
        finally
        {
            _syncing = false;
            _loading = false;
        }
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var index = 1;
        string id;
        do
        {
            id = $"provider{index++}";
        }
        while (_providers.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)));

        var provider = new ProviderDefinition
        {
            Id = id,
            DisplayName = "新供应商",
            BaseUrl = "https://api.example.com/v1",
            AuthMode = ProviderAuthMode.Command,
        };
        _providers.Add(provider);
        RefreshProviderList(provider.Id);
        StatusText.Text = "已新增。填好详情后点上方「保存供应商」。";
        ProviderIdBox.Focus();
        ProviderIdBox.SelectAll();
    }

    private async void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        var provider = Selected;
        if (provider is null) return;

        var confirm = MessageBox.Show(
            $"确定删除供应商「{provider.DisplayName}」及其密钥吗？\n\n" +
            "删除后 config.toml 里的引用不会自动清除——请先切回官方模式再删除。",
            "删除供应商", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        _providers.Remove(provider);
        await _service.SaveProvidersAsync(_providers, null);
        _selectedId = null;
        RefreshProviderList();
        UpdateKeySummary(null);
        StatusText.Text = "已删除。";
    }

    // ---------- 详情编辑 ----------

    private void Detail_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _syncing) return;
        if (Selected is not { } provider) return;

        // 把编辑同步进模型对象，这样"保存"拿到的就是屏幕上的内容。
        provider.Id = ProviderIdBox.Text.Trim();
        provider.DisplayName = DisplayNameBox.Text.Trim();
        provider.BaseUrl = BaseUrlBox.Text.Trim();
        SyncModelsFromRows();

        // 就地刷新左侧标题——重建列表会把用户的输入顶掉（见 ProviderRow.Refresh 注释）。
        if (ProviderList.SelectedItem is ProviderRow row) row.Refresh();
    }

    /// <summary>
    /// 拉取端点自己声明的模型 ID，直接覆盖下方列表。
    ///
    /// <para>模型 ID 的写法由端点定义、各家不同（大小写尤其容易错），手填基本靠猜。
    /// 让端点报一份清单是最省事也最不容易错的做法。</para>
    /// </summary>
    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } provider) return;

        FetchModelsButton.IsEnabled = false;
        StatusText.Text = "正在查询端点…";
        try
        {
            // 用户可能刚改过 Base URL 还没保存，所以传入当前输入框的值。
            var baseUrl = BaseUrlBox.Text.Trim();
            var key = CurrentKeyInput();
            if (key.Length == 0)
            {
                key = await _service.GetApiKeyAsync(provider.Id) ?? string.Empty;
            }

            var result = await _service.FetchModelsAsync(baseUrl, key);
            if (!result.Success)
            {
                StatusText.Text = "获取失败：" + result.Error;
                return;
            }

            // 保留用户已填的显示名与逐模型设置：同名（不区分大小写）就沿用，
            // 只把 ID 换成端点的写法；端点新增的模型则补进来。
            var existing = provider.Models.ToList();
            var merged = new List<ProviderModel>();

            foreach (var slug in result.Models)
            {
                var match = existing.FirstOrDefault(m =>
                    string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    merged.Add(new ProviderModel
                    {
                        Slug = slug,
                        DisplayName = $"[第三方] {slug}",
                        ContextWindow = null,
                        SupportsImages = provider.SupportsImages,
                        Enabled = true,
                    });
                }
                else
                {
                    match.Slug = slug;
                    if (string.IsNullOrWhiteSpace(match.DisplayName)) match.DisplayName = $"[第三方] {slug}";
                    merged.Add(match);
                }
            }

            provider.Models = merged;
            RenderModels(provider);
            StatusText.Text =
                $"已从端点获取 {result.Models.Count} 个模型。注意大小写——端点只认这些写法。";
        }
        catch (Exception exception)
        {
            StatusText.Text = "获取失败：" + exception.Message;
        }
        finally
        {
            FetchModelsButton.IsEnabled = true;
        }
    }

    // ---------- 模型行操作 ----------

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } provider) return;

        var model = new ProviderModel
        {
            ContextWindow = null,
            SupportsImages = provider.SupportsImages,
            Enabled = true,
        };

        var edited = ModelEditorWindow.ShowDialog(Window.GetWindow(this), model, isNew: true);
        if (edited is null || edited.Slug.Length == 0) return;

        provider.Models.Add(edited);
        RenderModels(provider);
        StatusText.Text = $"已添加模型 {edited.Slug}。记得点上方「保存供应商」。";
    }

    private void EditModel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } provider) return;
        if ((sender as FrameworkElement)?.Tag is not ModelRow row) return;

        var edited = ModelEditorWindow.ShowDialog(Window.GetWindow(this), row.Model, isNew: false);
        if (edited is null || edited.Slug.Length == 0) return;

        row.Model.Slug = edited.Slug;
        row.Model.DisplayName = edited.DisplayName;
        row.Model.ContextWindow = edited.ContextWindow;
        row.Model.SupportsImages = edited.SupportsImages;
        row.Model.Protocol = edited.Protocol;
        row.Refresh();
        SyncModelsFromRows();
        StatusText.Text = "模型已修改。记得点上方「保存供应商」。";
    }

    private void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } provider) return;
        if ((sender as FrameworkElement)?.Tag is not ModelRow row) return;

        provider.Models.Remove(row.Model);
        RenderModels(provider);
        StatusText.Text = $"已移除模型 {row.Slug}。记得点上方「保存供应商」。";
    }

    /// <summary>
    /// 测试<b>单个模型</b>能否正常使用：连通性 → 模型名 → 带工具的请求。
    ///
    /// <para>按模型测而不是按供应商测，因为同一个端点下不同模型的行为可能不同
    /// （有的支持工具、有的不支持，模型名大小写也可能各有要求）。
    /// 只有逐模型验证过，才知道到底是哪个模型有问题。</para>
    ///
    /// <para>第三步是关键：Codex 每次请求都带工具，而不少协议差异只在带工具时才暴露
    /// （就是之前那个 unsupported_feature 的形态）。</para>
    /// </summary>
    private async void TestModel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } provider) return;
        if ((sender as FrameworkElement)?.Tag is not ModelRow row) return;

        var slug = row.Model.Slug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            StatusText.Text = "这个模型还没有 ID，无法测试。";
            return;
        }

        if (sender is Button button) button.IsEnabled = false;
        StatusText.Text = $"正在测试 {slug}…";
        try
        {
            var baseUrl = BaseUrlBox.Text.Trim();
            var key = CurrentKeyInput();
            if (key.Length == 0) key = await _service.GetApiKeyAsync(provider.Id) ?? string.Empty;

            if (key.Length == 0)
            {
                StatusText.Text = "请先填写 API Key 再测试。";
                return;
            }

            // 自检必须照抄这个模型的实际工具形态，否则会替一个能用的配置报故障：
            // 端点接不接受取决于发的是 function 还是 custom、有没有 web_search。
            var report = await _service.TestConnectionAsync(
                baseUrl, key, slug,
                usesResponsesLite: row.Model.Protocol == ToolProtocol.CodeMode,
                protocol: row.Model.Protocol,
                supportsCustomTools: provider.SupportsCustomTools,
                supportsWebSearch: provider.SupportsWebSearch);
            StatusText.Text = report.Success
                ? $"{slug} 自检通过。"
                : $"{slug} 自检未通过，详见弹窗。";
            ConnectionTestWindow.Show(Window.GetWindow(this), slug, report);
        }
        catch (Exception exception)
        {
            StatusText.Text = "测试失败：" + exception.Message;
        }
        finally
        {
            if (sender is Button b) b.IsEnabled = true;
        }
    }

    private void AuthMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _syncing || Selected is not { } provider) return;
        provider.AuthMode = PlainTokenAuthRadio.IsChecked == true
            ? ProviderAuthMode.PlainToken
            : ProviderAuthMode.Command;
    }

    /// <summary>
    /// 端点能力开关。默认都关——见 <see cref="ProviderDefinition.SupportsCustomTools"/>。
    /// 勾错不会静默降级：Codex 会把工具原样发出去，由端点 400 拒绝，
    /// 所以这里给出明确的后果提示。
    /// </summary>
    private void Capability_Changed(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } provider) return;

        if (!_loading && !_syncing)
        {
            provider.SupportsCustomTools = CustomToolsCheck.IsChecked == true;
            provider.SupportsWebSearch = WebSearchCheck.IsChecked == true;
        }

        UpdateCapabilityHint();
    }

    private void UpdateCapabilityHint()
    {
        var parts = new List<string>();
        if (CustomToolsCheck.IsChecked != true)
        {
            parts.Add("不发送自定义工具（apply_patch 会退回标准 function 形式）");
        }
        if (WebSearchCheck.IsChecked != true)
        {
            parts.Add("不发送联网搜索工具，切换时会写入 web_search = \"disabled\"");
        }

        CapabilityHintText.Text = parts.Count == 0
            ? "两项都发。仅在端点确实支持时才这样选，否则请求会被拒绝。"
            : "切换后：" + string.Join("；", parts) + "。";
    }

    /// <summary>
    /// 切换密钥的显示 / 隐藏。
    ///
    /// <para>两个输入框始终保存着同一份文本：隐藏时 <see cref="PasswordBox"/> 显示圆点，
    /// 显示时 <see cref="ApiKeyPlainBox"/> 显示明文。任一时刻只有一个是可见的，
    /// 但两个都要保持同步，否则来回切换会丢内容。</para>
    ///
    /// <para><b>同步期间必须抑制回写</b>（<see cref="_syncingKey"/>）：两个控件互为镜像，
    /// 一个的 Changed 事件会去改另一个，而那个又会反过来触发事件——不同步好的话，
    /// 第二次切换就会把文本清空。</para>
    /// </summary>
    private bool _syncingKey;

    private void SetKeyRevealed(bool revealed)
    {
        // 取值方向不能按"目标状态"来定，必须看**切换前**谁可见：
        // 那时可见的那个才持有用户输入。（这个规则由 RevealToggle 承载并被测试固定，
        // 因为搞反过一次，结果明文框拿到旧值后与掩码框的内容拼接，密钥变成两份重复。）
        var value = RevealToggle.Resolve(_keyRevealed, ApiKeyBox.Password, ApiKeyPlainBox.Text);

        _syncingKey = true;
        try
        {
            // 两个控件都写入同一个值，来回切换才不会丢内容。
            ApiKeyBox.Password = value;
            ApiKeyPlainBox.Text = value;
        }
        finally
        {
            _syncingKey = false;
        }

        _keyRevealed = revealed;
        ApiKeyBox.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyPlainBox.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;

        if (RevealKeyIcon is not null)
        {
            // 睁眼 = 当前明文可见（点它去隐藏）；闭眼 = 当前隐藏（点它去显示）。
            RevealKeyIcon.Data = (System.Windows.Media.Geometry)FindResource(
                revealed ? "IconEyeOff" : "IconEye");
        }
        RevealKeyButton.ToolTip = revealed ? "隐藏密钥" : "显示密钥";
    }

    private void RevealKey_Click(object sender, RoutedEventArgs e) => SetKeyRevealed(!_keyRevealed);

    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingKey || _keyRevealed) return;
        _syncingKey = true;
        try { ApiKeyPlainBox.Text = ApiKeyBox.Password; }
        finally { _syncingKey = false; }
    }

    private void ApiKeyPlain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingKey || !_keyRevealed) return;
        _syncingKey = true;
        try { ApiKeyBox.Password = ApiKeyPlainBox.Text; }
        finally { _syncingKey = false; }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _syncing) return;
        StatusText.Text = ThirdPartyRadio.IsChecked == true
            ? "点右上「应用并重启 Codex」进入第三方模式。"
            : "点右上「切回官方并重启」还原 config.toml。";
    }

    // ---------- 公开动作（由宿主工具栏调用）----------

    /// <summary>保存供应商定义与（若填写了）API Key。</summary>
    public async Task<bool> SaveAsync()
    {
        Detail_Changed(this, new RoutedEventArgs());

        foreach (var provider in _providers) provider.Normalize();
        var duplicate = _providers
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            StatusText.Text = $"供应商 ID 重复：{duplicate.Key}。每个供应商的 ID 必须唯一。";
            return false;
        }

        if (_providers.Any(p => string.IsNullOrWhiteSpace(p.Id)))
        {
            StatusText.Text = "有供应商的 ID 为空，请填写。";
            return false;
        }

        // 没有模型的供应商根本切不过去（ApplyThirdParty 会拒绝），
        // 早点拦住比留一个看起来配好、实则不可用的条目强。
        var modelLess = _providers.FirstOrDefault(p => p.Models.Count == 0);
        if (modelLess is not null)
        {
            StatusText.Text = $"供应商「{modelLess.DisplayName}」还没有模型，请在模型列表里至少填一行。";
            return false;
        }

        var badUrl = _providers.FirstOrDefault(p =>
            !Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps));
        if (badUrl is not null)
        {
            StatusText.Text = $"供应商「{badUrl.DisplayName}」的 Base URL 必须是完整的 http:// 或 https:// 地址。";
            return false;
        }

        // 用当前选中项的 ID，而不是 _selectedId：用户可能刚改过 ID，
        // 把旧 ID 当生效项会留下一个查不到的悬空引用。
        var selected = Selected;
        await _service.SaveProvidersAsync(_providers, selected?.Id);

        if (selected is not null)
        {
            var key = CurrentKeyInput();
            if (key.Length > 0)
            {
                await _service.SetApiKeyAsync(selected.Id, key);
                ApiKeyBox.Clear();
                ApiKeyPlainBox.Text = string.Empty;
                SetKeyRevealed(false);
                KeyStatusText.Text = "已保存密钥（留空表示不修改）";
            }

            _selectedId = selected.Id;
            UpdateKeySummary(selected.Id);
        }

        StatusText.Text = "已保存。";
        RefreshProviderList(_selectedId);
        StatusChanged?.Invoke(this, "供应商已保存。");
        return true;
    }

    private string CurrentKeyInput() => _keyRevealed ? ApiKeyPlainBox.Text : ApiKeyBox.Password;

    public async Task PreviewAsync()
    {
        try
        {
            var preview = await _service.PreviewAsync(_selectedId);
            var window = new PreviewWindow(preview, _service.ConfigPath, Window.GetWindow(this));
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            StatusText.Text = "预览失败：" + exception.Message;
        }
    }

    public async Task RestoreLatestBackupAsync()
    {
        var latest = _service.FindLatestBackup();
        if (latest is null)
        {
            StatusChanged?.Invoke(this, "没有找到可还原的备份。");
            return;
        }

        var confirm = MessageBox.Show(
            $"用备份覆盖当前 config.toml？\n\n备份：{Path.GetFileName(latest)}\n时间：{File.GetLastWriteTime(latest):yyyy-MM-dd HH:mm:ss}",
            "还原配置", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            await _service.RestoreAsync(latest);
            await LoadAsync();
            StatusChanged?.Invoke(this, "已还原到上一次备份。重启 Codex 后生效。");
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, "还原失败：" + exception.Message);
        }
    }

    /// <summary>按当前选中的单选按钮，应用第三方模式或切回官方模式。</summary>
    public async Task ApplyAsync()
    {
        try
        {
            if (ThirdPartyRadio.IsChecked == true)
            {
                if (Selected is not { } provider)
                {
                    StatusChanged?.Invoke(this, "请先选择一个供应商。");
                    return;
                }

                var searchNote = provider.SupportsWebSearch
                    ? "联网搜索：保留你的设置。"
                    : "联网搜索：该端点不支持，切换后会关掉（切回官方时自动还原你的原设置）。";

                var confirm = MessageBox.Show(
                    $"切换到第三方模式并重启 Codex？\n\n供应商：{provider.DisplayName}\n模型：{provider.Models.Count} 个\n\n" +
                    "官方模型在第三方模式下不显示也不可用。切换前会自动备份 config.toml，登录状态与插件不受影响。\n\n" +
                    searchNote,
                    "切换模式", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.OK) return;

                var result = await _service.ApplyThirdPartyAsync(provider.Id);

                // 保存后再重启：Codex 是启动时读配置的，不重启不生效。
                await _service.RestartCodexAsync();
                CodexRestarted?.Invoke(this, EventArgs.Empty);

                var message =
                    $"已切到第三方模式，Codex 已重启（{result.ModelCount} 个模型，{result.CatalogBytes / 1024} KB）。" +
                    $"备份：{Path.GetFileName(result.BackupPath)}";
                if (!string.IsNullOrEmpty(result.Note)) message += "\n" + result.Note;
                StatusChanged?.Invoke(this, message);
                await LoadAsync();
            }
            else
            {
                var confirm = MessageBox.Show(
                    "切回官方模式并重启 Codex？\n\nconfig.toml 会还原成你原来的内容，官方模型列表恢复，无需重新登录。",
                    "切换模式", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.OK) return;

                var backup = await _service.ApplyOfficialAsync();
                await _service.RestartCodexAsync();
                CodexRestarted?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, backup is null
                    ? "此前已是官方模式，未改动配置；Codex 已重启。"
                    : $"已切回官方模式，Codex 已重启。备份：{Path.GetFileName(backup)}");
                await LoadAsync();
            }
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, "应用失败：" + exception.Message);
        }
    }
}

/// <summary>只读预览窗：展示即将写入 config.toml 的内容。</summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow(string content, string configPath, Window? owner)
    {
        Title = "配置预览";
        Width = 760;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        ShowInTaskbar = false;

        var box = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            Padding = new Thickness(12),
        };

        var hint = new TextBlock
        {
            Text = "以下内容尚未写入。目标文件：" + configPath,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 10, 12, 0),
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
        };

        var close = new Button
        {
            Content = "关闭",
            Width = 88,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)FindResource("ModernButton"),
            IsCancel = true,
        };

        var panel = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(hint);
        panel.Children.Add(close);
        panel.Children.Add(box);

        Content = new Border
        {
            Padding = new Thickness(10),
            Background = (System.Windows.Media.Brush)FindResource("CanvasBrush"),
            Child = panel,
        };
    }
}
