using System.Text;

namespace GptPlusManager.Core.Codex;

/// <summary>当前生效的路由模式。</summary>
public enum CodexRoutingMode
{
    /// <summary>官方模式：不改动任何配置，用内置目录与官方登录。</summary>
    Official,

    /// <summary>第三方模式：目录只含第三方模型，请求全部路由到自建供应商。</summary>
    ThirdParty,
}

/// <summary>config.toml 的解析结果（只读视图）。</summary>
public sealed record CodexConfigSnapshot
{
    public CodexRoutingMode Mode { get; init; } = CodexRoutingMode.Official;
    public string? Model { get; init; }
    public string? ModelProvider { get; init; }
    public string? ModelCatalogJson { get; init; }

    /// <summary>是否检测到本应用写入的内容。</summary>
    public bool HasManagedBlock { get; init; }

    /// <summary>
    /// 配置是否处于<b>旧版危险布局</b>：起止标记跨度过大（桌面版会把悬空注释搬到文件末尾，
    /// 导致用户整份配置看起来都落在"待删范围"里）。
    ///
    /// <para>当前版本已不再按范围删除，所以即使如此也不会真的删数据；但旧版本会。
    /// 检出后界面应提示用户重新应用一次，把文件规整成新格式。</para>
    /// </summary>
    public bool HasLegacyBlockLayout { get; init; }

    /// <summary>目录指向的文件是否真的存在——不存在时 Codex 会直接启动失败。</summary>
    public bool CatalogFileExists { get; init; }

    /// <summary>
    /// 命令式配方把"本应用自己的 exe 路径"写进了 config.toml。如果之后应用被
    /// 移动、改名或重新发布到别的目录，路径就失效了——而 Codex 只会不断重试并报
    /// "wrote non-UTF-8 data"/"Invalid API Key" 这类毫无指向性的错，
    /// 用户根本猜不到是路径问题。所以这里主动检测。
    ///
    /// <para>注意 <see cref="StoredTokenCommandPath"/> 与 <see cref="TokenCommandPath"/> 的区别：
    /// 前者是配置里原样记录的值，后者是<b>当前实际应该用</b>的路径。两者不同即表示
    /// 程序被移动过，需要修复。</para>
    /// </summary>
    public bool TokenCommandExists { get; init; }

    /// <summary>配置里原样记录的取 token 命令路径。</summary>
    public string? StoredTokenCommandPath { get; init; }

    /// <summary>当前进程自身的可执行文件路径——即现在应该写进配置的值。</summary>
    public string? ExpectedTokenCommandPath { get; init; }

    /// <summary>
    /// 配置里的命令路径是否指向当前运行的这份程序。为 false 时应当重新应用一次来修复。
    /// </summary>
    public bool TokenCommandMatchesCurrentApp =>
        string.IsNullOrWhiteSpace(StoredTokenCommandPath) ||
        string.Equals(StoredTokenCommandPath, ExpectedTokenCommandPath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 安全读写 <c>~/.codex/config.toml</c>。
///
/// 设计要点（每条都对应一个已实测的坑）：
/// <list type="bullet">
/// <item><b>行级手术编辑</b>，不整文件重写：用户自己的注释、空行、未知配置必须原样保留。</item>
/// <item><b>作用域隔离</b>：顶层 <c>model</c> 等键只出现在首个 <c>[table]</c> 之前，
/// 绝不触碰 <c>[profiles.*]</c> 里的同名键。</item>
/// <item><b>三处同步</b>：切换时必须同时改 <c>model</c> / <c>model_provider</c> /
/// <c>model_catalog_json</c>；只删 provider 块却留下 <c>model_provider</c>，
/// Codex 会报 "Model provider not found" 且无法启动。</item>
/// <item><b>保留原始编码</b>：行尾序列与 BOM 按原文件保持，避免整文件字节漂移。</item>
/// <item>写前时间戳备份，写后校验"管理区外逐字节未变"+ TOML 可解析，失败自动回滚。</item>
/// <item><b>绝不按"起止标记之间的范围"删除内容</b>：桌面版会重写 config.toml，
/// 把悬空注释排到文件末尾——实测它把我们的结束标记挪到了最后一行，
/// 于是用户整个配置都落进"待删范围"里，切回官方就会删光。
/// 现在的做法是结构化识别（认自己的 provider 表与顶层键），标记只作单行注释。</item>
/// </list>
/// </summary>
public sealed class CodexConfigStore : IDisposable
{
    /// <summary>
    /// 旧版遗留的块标记。只作为<b>单行注释</b>清理，绝不用它们圈定删除范围——
    /// 桌面版会把结束标记挪到文件末尾，按范围删就等于删掉整个文件。
    /// </summary>
    private const string LegacyBeginMarker = "# --- GptPlus Manager managed block ---";
    private const string LegacyEndMarker = "# --- end GptPlus Manager managed block ---";

    /// <summary>
    /// 记录"改动前顶层区域原文"的那一行。
    ///
    /// <para>为什么不直接在切回官方时删掉三个顶层键：用户本来就可能自己设了
    /// <c>model = "..."</c>。我们只是<b>覆盖</b>了它，删掉就等于把用户的设置抹了。</para>
    ///
    /// <para>所以进第三方模式前先把整个顶层区域（首个 <c>[table]</c> 之前的所有行）
    /// 原样记下来，切回时整段还原——字节级精确，也不依赖"我们插在第几行"这种脆弱假设。</para>
    /// </summary>
    private const string HeadMarker = "# gptplus-head:";

    /// <summary>
    /// 记录当前管理的供应商 ID。用来准确认出哪一个 <c>[model_providers.*]</c> 是我们写的，
    /// 从而只删自己那一张表。
    /// </summary>
    private const string ProviderMarker = "# gptplus-provider:";

    /// <summary>
    /// 命令式配方写在 provider 表里的特征串。即使标记注释被桌面版抹掉，
    /// 也能据此认出这张表是我们写的（用户自己配的 provider 不会带这个）。
    /// </summary>
    private const string TokenCommandSignature = "--provider-token";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="userProfile">
    /// 用户目录；留空取当前用户。测试与脚手架必须显式传入，否则会改到真实配置。
    /// </param>
    /// <param name="appExecutablePath">
    /// 本应用自己的可执行文件路径——命令式配方会把它写进 config.toml 的
    /// <c>auth.command</c>，供 Codex 回调取 token。
    ///
    /// <para><b>必须由调用方显式提供。</b>不能默认用 <see cref="Environment.ProcessPath"/>：
    /// 那个值取决于<b>谁加载了这个库</b>，于是任何测试或辅助程序一旦调用，就会把自己的
    /// exe 路径写进用户的真实配置，把原本正确的路径覆盖成临时程序——实测反复发生，
    /// 而 Codex 只会报 "wrote non-UTF-8 data" 这种毫无指向性的错。默认值仅作为
    /// 兜底（<c>ChatGptAccountManager.exe</c>），真实应用必须传入自己的路径。</para>
    /// </param>
    public CodexConfigStore(string? userProfile = null, string? appExecutablePath = null)
    {
        var profile = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfile;
        CodexHome = Path.Combine(profile, ".codex");
        ConfigPath = Path.Combine(CodexHome, "config.toml");
        AppExecutablePath = string.IsNullOrWhiteSpace(appExecutablePath) ? null : appExecutablePath;
    }

    public string CodexHome { get; }
    public string ConfigPath { get; }

    /// <summary>
    /// 写进 <c>auth.command</c> 的可执行文件路径。为 null 时表示调用方未指定，
    /// 此时不该新建命令式条目（见构造函数说明）。
    /// </summary>
    public string? AppExecutablePath { get; init; }

    public async Task<CodexConfigSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Parse((await ReadRawAsync(cancellationToken).ConfigureAwait(false)).Text, TokenCommandPath());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 进入第三方模式：写入三个顶层键并追加 provider 定义。
    /// 返回备份文件路径（供一键还原）；调用方需保证 catalog 文件已存在且合法。
    /// </summary>
    public async Task<string?> ApplyThirdPartyAsync(
        ProviderDefinition provider,
        string catalogPath,
        string? apiToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.Normalize();
        if (string.IsNullOrWhiteSpace(provider.Id)) throw new ArgumentException("供应商 ID 不能为空。", nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new ArgumentException("供应商 Base URL 不能为空。", nameof(provider));
        if (provider.Models.Count == 0) throw new ArgumentException("供应商至少要有一个模型。", nameof(provider));
        if (string.IsNullOrWhiteSpace(catalogPath)) throw new ArgumentException("模型目录路径不能为空。", nameof(catalogPath));
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException("模型目录文件不存在，写入后 Codex 将无法启动。", catalogPath);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = await ReadRawAsync(cancellationToken).ConfigureAwait(false);
            var updated = BuildThirdPartyConfig(original, provider, catalogPath, apiToken, TokenCommandPath());
            return await WriteWithValidationAsync(original, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>回到官方模式：移除本应用管理的键与 provider 块，其余内容原样保留。</summary>
    public async Task<string?> ApplyOfficialAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = await ReadRawAsync(cancellationToken).ConfigureAwait(false);
            var updated = BuildOfficialConfig(original);
            if (string.Equals(original.Text, updated.Text, StringComparison.Ordinal))
            {
                return null; // 已是官方模式，无改动则不产生备份。
            }
            return await WriteWithValidationAsync(original, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>预览将要写入的结果，供界面展示。</summary>
    public async Task<string> PreviewThirdPartyAsync(
        ProviderDefinition provider,
        string catalogPath,
        string? apiToken,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = await ReadRawAsync(cancellationToken).ConfigureAwait(false);
            return BuildThirdPartyConfig(original, provider, catalogPath, apiToken, TokenCommandPath()).Text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>预览回到官方模式后的结果。</summary>
    public async Task<string> PreviewOfficialAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return BuildOfficialConfig(await ReadRawAsync(cancellationToken).ConfigureAwait(false)).Text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>从备份还原。</summary>
    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("备份文件不存在。", backupPath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(CodexHome);
            var bytes = await File.ReadAllBytesAsync(backupPath, cancellationToken).ConfigureAwait(false);
            await WriteAtomicAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 若配置里的取 token 命令路径已不是当前这份程序，就地改写为当前路径。
    ///
    /// <para>这是对"程序被移动/改名后 Codex 静默取不到密钥"的自愈：用户不需要理解
    /// 发生了什么，重新应用一次即可。只在确实不一致时才写盘，并保留备份。</para>
    ///
    /// <para>返回备份路径；无需修复时返回 null。</para>
    /// </summary>
    public async Task<string?> RepairTokenCommandPathAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = await ReadRawAsync(cancellationToken).ConfigureAwait(false);
            var lines = SplitLines(original.Text);

            var stored = ExtractTokenCommand(lines);
            if (string.IsNullOrWhiteSpace(stored)) return null; // 非命令式，无需处理

            var expected = TokenCommandPath();
            if (string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase)) return null;

            // 只在记录的路径**已经不指向任何文件**时才重写。
            //
            // 这条约束是防污染的：路径有效说明配置是好的，没有可修的东西。若此处按
            // "与 expected 不同就改"，任何辅助程序（测试、调试脚手架）只要用自己注入的
            // 路径调一次，就会把用户正确的配置覆盖成一个临时 exe——实测反复发生，
            // 之后 Codex 只会报 "wrote non-UTF-8 data"，用户完全看不出是路径问题。
            // 真正需要修的场景（应用被移动/改名）恰好就是旧路径失效，所以这不损失功能。
            if (File.Exists(stored)) return null;

            var patched = new List<string>(lines.Count);
            var replaced = false;
            foreach (var line in lines)
            {
                if (!replaced && TryMatchKey(line, "auth") && line.Contains("command ="))
                {
                    var quoteIndex = line.IndexOf("command =", StringComparison.Ordinal) + "command =".Length;
                    var rest = line[quoteIndex..];
                    var leading = rest.Length - rest.TrimStart().Length;
                    var quoteAt = quoteIndex + leading;
                    if (quoteAt < line.Length && line[quoteAt] is '\'' or '"')
                    {
                        var quote = line[quoteAt];
                        var end = line.IndexOf(quote, quoteAt + 1);
                        if (end > quoteAt)
                        {
                            // 路径用字面量字符串，反斜杠无需转义。
                            var replacement = quote == '\''
                                ? expected.Replace("'", "''")
                                : expected.Replace("\\", "\\\\").Replace("\"", "\\\"");
                            patched.Add(line[..(quoteAt + 1)] + replacement + line[end..]);
                            replaced = true;
                            continue;
                        }
                    }
                }
                patched.Add(line);
            }

            if (!replaced) return null;

            var updated = original with { Text = JoinLines(patched, original.NewLine) };
            return await WriteWithValidationAsync(original, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- 构建 ----------

    private static RawConfig BuildThirdPartyConfig(
        RawConfig original, ProviderDefinition provider, string catalogPath, string? apiToken,
        string tokenCommandPath)
    {
        var lines = SplitLines(original.Text);

        // 已经处在第三方模式时，管理块里那份"原始顶层区域"才是权威记录——必须复用它。
        // 若在这里重新计算，抄到的会是我们自己上次写进去的 model = "<第三方模型>"，
        // 于是切回官方时"还原"成一个第三方配置，用户的设置就永久丢了。
        var recordedHead = FindRecordedHeadPayload(lines)
            ?? RecordCurrentHead(lines);

        // 只替换本供应商的表，并把旧的顶层区域记录去掉（下面会写回同一份）。
        //
        // 关键：**不要**清掉其他供应商的 provider 表。每个对话创建时都把自己的
        // model_provider 记进会话元数据；把旧表删掉，那些对话再打开就会
        // "Model provider not found"，实测桌面版正是如此。
        lines = RemoveMarkerLines(lines);
        lines = RemoveProviderTableFor(lines, provider.SafeTomlKey());

        // 顶层三处必须一起改，缺任何一个 Codex 都会报错。
        lines = SetTopLevelKey(lines, "model", TomlString(provider.Models[0].Slug));
        lines = SetTopLevelKey(lines, "model_provider", TomlString(provider.SafeTomlKey()));
        lines = SetTopLevelKey(lines, "model_catalog_json", TomlLiteral(catalogPath));

        // 联网搜索：端点不支持时必须显式写成 disabled，Codex 才会把 web_search 工具
        // 从请求里摘掉——这是唯一的开关（目录里的 web_search_tool_type 只管形态）。
        // 支持时把控制权还给用户：还原改动前的值，原本没设就删掉这个键。
        lines = provider.SupportsWebSearch
            ? RestoreTopLevelKey(lines, recordedHead, "web_search")
            : SetTopLevelKey(lines, "web_search", TomlString("disabled"));

        // 追加 provider 定义。
        //
        // 不使用"起止标记圈定范围"的写法：桌面版重写 config.toml 时会把悬空注释
        // 排到文件末尾，结束标记一旦被挪到最后一行，用户整个配置都会落进"待删范围"。
        // 只用单行注释标记，识别时按结构判定。
        //
        // 插到首个已存在的 [table] 之前：三个顶层键属于文件头部，而 provider 表
        // 必须排在顶层键之后。
        var managed = new List<string>
        {
            $"{ProviderMarker} {provider.SafeTomlKey()}",
            $"[model_providers.{TomlKey(provider.SafeTomlKey())}]",
            $"name = {TomlString(provider.DisplayName.Length > 0 ? provider.DisplayName : provider.Id)}",
            $"base_url = {TomlLiteral(provider.BaseUrl)}",
            "wire_api = \"responses\"",
        };

        if (provider.AuthMode == ProviderAuthMode.PlainToken)
        {
            // 明文模式：保留官方账号上下文，但密钥落在配置文件里。
            managed.Add("requires_openai_auth = true");
            managed.Add($"experimental_bearer_token = {TomlString(apiToken ?? string.Empty)}");
        }
        else
        {
            // 命令式：Codex 每次（重新）取 token 时调用本应用，配置文件里没有密钥。
            managed.Add(
                $"auth = {{ command = {TomlLiteral(tokenCommandPath)}, args = [\"--provider-token\", {TomlString(provider.Id)}] }}");
        }

        // 顶层区域记录：放在 provider 表之后，作为一行注释。
        managed.Add($"{HeadMarker} {recordedHead}");
        managed.Add(string.Empty);

        lines.InsertRange(IndexOfFirstTable(lines), managed);

        return original with { Text = JoinLines(lines, original.NewLine) };
    }

    private static RawConfig BuildOfficialConfig(RawConfig original)
    {
        var lines = SplitLines(original.Text);

        // "本应用是否改过这个文件"不再看块标记——桌面版会把悬空注释搬到文件末尾，
        // 标记位置完全不可靠。改为看有没有我们写的痕迹：顶层区域记录，或自己的 provider 表。
        var hasHeadRecord = FindRecordedHeadPayload(lines) is not null;
        var hasProviderTable = HasManagedProviderTable(lines, out _);

        if (!hasHeadRecord && !hasProviderTable)
        {
            // 从未改过：彻底空操作，否则会把用户自己写的顶层 model 一起删掉。
            return original;
        }

        var recordedHead = hasHeadRecord
            ? DecodeHead(FindRecordedHeadPayload(lines)!)
            : null;

        // 只去掉自己写的注释标记；**保留所有 provider 表**。
        //
        // 这些表必须留着：每个对话创建时把自己的 model_provider 记进会话元数据，
        // 删掉表就等于让那些对话"找不到自己的供应商"，打开时报
        // "Model provider not found"（实测桌面版如此）。表的保留没有副作用——
        // 它们只在被引用时才生效，而切回官方后顶层 model_provider 已不存在。
        var body = RemoveMarkerLines(lines);

        if (recordedHead is not null)
        {
            // 整段还原改动前的顶层区域——字节级精确。
            var limit = IndexOfFirstTable(body);
            return original with
            {
                Text = JoinLines([.. recordedHead, .. body.Skip(limit)], original.NewLine),
            };
        }

        // 记录损坏或缺失（例如旧版本留下的配置）：至少把管理键清干净，
        // 不留下"model_provider 指向不存在的表"这种 Codex 无法启动的半状态。
        body = RemoveTopLevelKey(body, "model");
        body = RemoveTopLevelKey(body, "model_provider");
        body = RemoveTopLevelKey(body, "model_catalog_json");
        return original with { Text = JoinLines(body, original.NewLine) };
    }

    /// <summary>
    /// 检出旧版"起止标记圈定范围"的危险布局。
    ///
    /// <para>判据：起止标记都在，且它们之间夹着与托管内容无关的表
    /// （<c>[plugins.*]</c> / <c>[mcp_servers.*]</c> / <c>[desktop]</c> 等）。
    /// 那说明结束标记被桌面版搬到了文件末尾，旧版本会据此删掉用户全部配置。</para>
    /// </summary>
    private static bool DetectLegacyBlockLayout(List<string> lines)
    {
        var begin = lines.FindIndex(l => l.Trim() == LegacyBeginMarker);
        var end = lines.FindLastIndex(l => l.Trim() == LegacyEndMarker);
        if (begin < 0 || end < 0 || end <= begin) return false;

        for (var i = begin; i <= end; i++)
        {
            if (!TryParseTableHeader(lines[i].Trim(), out var table)) continue;
            // 托管内容只可能是 model_providers.*；出现别的表就说明范围被撑大了。
            if (!table.StartsWith("model_providers.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>文件里是否存在本应用写入的 provider 表。</summary>
    private static bool HasManagedProviderTable(List<string> lines, out string? tableName)    {
        tableName = null;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!TryParseTableHeader(lines[i].Trim(), out var name)) continue;
            if (!IsManagedProviderTable(lines, i, name)) continue;

            tableName = name;
            return true;
        }
        return false;
    }

    /// <summary>抄下当前顶层区域（首个 <c>[table]</c> 之前的所有行）并编码，供日后还原。</summary>
    private static string RecordCurrentHead(List<string> lines)
    {
        var head = lines.Take(IndexOfFirstTable(lines)).ToList();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', head)));
    }

    /// <summary>
    /// 取出管理块里记录的原始顶层区域（base64）。没有记录时返回 null——
    /// 调用方据此区分"我们改过这个文件"与"从未碰过"。
    /// </summary>
    private static string? FindRecordedHeadPayload(List<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(HeadMarker, StringComparison.Ordinal)) continue;

            var payload = trimmed[HeadMarker.Length..].Trim();
            if (payload.Length > 0) return payload;
        }
        return null;
    }

    /// <summary>
    /// 解码顶层区域记录；记录损坏时返回 null，让调用方走保守路径。
    ///
    /// <para>解出来的内容还会再滤一遍管理块：万一某个版本写坏过、把管理块记录进了
    /// 自己的 head，这里就能自愈，而不是把它当成"用户的原始配置"永远还回去。</para>
    /// </summary>
    private static List<string>? DecodeHead(string payload)
    {
        try
        {
            var decoded = SplitLines(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return RemoveManaged(decoded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 移除本应用写入的内容：单行标记注释、顶层区域记录、以及<b>自己的</b>
    /// <c>[model_providers.*]</c> 表。
    ///
    /// <para><b>绝不移除"起止标记之间的所有行"。</b>桌面版会重写 config.toml，把悬空注释
    /// 排到文件末尾——实测它把结束标记挪到了最后一行，于是用户整个配置
    /// （plugins / mcp_servers / desktop / windows / projects）都落进了"待删范围"。
    /// 按范围删等于删掉整个文件。</para>
    ///
    /// <para>所以这里只做两件安全的事：删掉<b>自己写的那几行注释</b>（单行，删错也只损失一行），
    /// 以及删掉<b>识别出属于自己的 provider 表</b>（按表名 + 内容特征判定）。</para>
    /// </summary>
    /// <summary>
    /// 移除本应用写的<b>全部</b>内容：标记注释与所有自己的 provider 表。
    /// 只用于清洗记坏了的顶层记录，不参与常规切换——常规切换请用
    /// <see cref="RemoveProviderTableFor"/>。
    /// </summary>
    private static List<string> RemoveManaged(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        var index = 0;

        while (index < lines.Count)
        {
            var trimmed = lines[index].Trim();

            // 单行标记注释：直接丢。
            if (trimmed == LegacyBeginMarker || trimmed == LegacyEndMarker ||
                trimmed.StartsWith(HeadMarker, StringComparison.Ordinal) ||
                trimmed.StartsWith(ProviderMarker, StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            // provider 表头：若是我们写的，连同它的表体一起跳过。
            if (TryParseTableHeader(trimmed, out var tableName) &&
                IsManagedProviderTable(lines, index, tableName))
            {
                index = SkipTable(lines, index);
                continue;
            }

            result.Add(lines[index]);
            index++;
        }

        return result;
    }

    /// <summary>
    /// 只移除<b>我们自己写的注释标记</b>（顶层记录、旧版成对标记），
    /// 保留所有 provider 表与它们的标记。
    /// </summary>
    private static List<string> RemoveMarkerLines(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == LegacyBeginMarker || trimmed == LegacyEndMarker ||
                trimmed.StartsWith(HeadMarker, StringComparison.Ordinal))
            {
                continue;
            }
            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// 只移除指定供应商的那张 <c>[model_providers.*]</c> 表（连同上方的标记注释
    /// 与一张表后的空行），<b>其他供应商的表原样保留</b>。
    ///
    /// <para><b>为什么必须保留其他 provider 表：</b>每个对话在创建时都把自己的
    /// <c>model_provider</c> 写进会话元数据。切换供应商若把旧表删掉，那些对话就再也
    /// 解析不到自己的 provider，打开时直接报 "Model provider not found"——
    /// 实测桌面版正是如此。保留这些表没有副作用：它们只在被引用时才生效，
    /// 而新对话走的是顶层 <c>model_provider</c>。</para>
    /// </summary>
    private static List<string> RemoveProviderTableFor(List<string> lines, string providerId)
    {
        var target = $"model_providers.{providerId}";
        var result = new List<string>(lines.Count);
        var index = 0;

        while (index < lines.Count)
        {
            var trimmed = lines[index].Trim();

            if (TryParseTableHeader(trimmed, out var tableName) &&
                string.Equals(tableName, target, StringComparison.OrdinalIgnoreCase) &&
                IsManagedProviderTable(lines, index, tableName))
            {
                // 连同上方的标记注释一起去掉（随后会重新写）。
                if (result.Count > 0 &&
                    result[^1].Trim().StartsWith(ProviderMarker, StringComparison.Ordinal))
                {
                    result.RemoveAt(result.Count - 1);
                }

                index = SkipTable(lines, index);

                // 再吃掉紧随其后的一个空行，避免反复切换把空行越积越多。
                if (index < lines.Count && lines[index].Trim().Length == 0) index++;
                continue;
            }

            result.Add(lines[index]);
            index++;
        }

        return result;
    }

    /// <summary>解析 <c>[a.b]</c> 形式的表头，返回表名。忽略 <c>[[array]]</c>。</summary>
    private static bool TryParseTableHeader(string trimmedLine, out string tableName)
    {
        tableName = string.Empty;
        if (trimmedLine.Length < 3 || trimmedLine[0] != '[' || trimmedLine[1] == '[') return false;

        var close = trimmedLine.IndexOf(']');
        if (close <= 1) return false;

        tableName = trimmedLine[1..close].Trim();
        return true;
    }

    /// <summary>跳过一张表（表头行 + 直到下一个表头或文件尾的行）。</summary>
    private static int SkipTable(List<string> lines, int headerIndex)
    {
        var index = headerIndex + 1;
        while (index < lines.Count && !IsTableHeader(lines[index])) index++;
        return index;
    }

    private static bool IsTableHeader(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('[') && !trimmed.StartsWith("#", StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断某张 <c>[model_providers.*]</c> 表是否由本应用写入。
    ///
    /// <para>两条独立证据，任一成立即可：表体里出现本应用特有的取 token 命令签名，
    /// 或者紧邻表头上方有我们写的 provider 标记。用"内容特征"是为了兼容旧版本
    /// 写下的、只有块标记而没有 provider 标记的配置。</para>
    ///
    /// <para>判定保守是刻意的：漏认自己的表只是留下一点残留（用户能看见、能手动删），
    /// 误认用户的表则会把他的自建 provider 删掉。宁可残留，不可误删。</para>
    /// </summary>
    private static bool IsManagedProviderTable(List<string> lines, int headerIndex, string tableName)
    {
        if (!tableName.StartsWith("model_providers.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 证据一：紧邻上方的标记注释（可能隔着空行）。
        for (var i = headerIndex - 1; i >= 0 && i >= headerIndex - 3; i--)
        {
            var above = lines[i].Trim();
            if (above.Length == 0) continue;
            if (above.StartsWith(ProviderMarker, StringComparison.Ordinal))
            {
                // 标记里记了 ID，与表名核对，避免标记与实际表不符时误删。
                var marked = above[ProviderMarker.Length..].Trim();
                return marked.Length == 0 ||
                       string.Equals(tableName["model_providers.".Length..], marked, StringComparison.OrdinalIgnoreCase);
            }
            break;
        }

        // 证据二：表体里出现本应用特有的取 token 参数。
        var end = SkipTable(lines, headerIndex);
        for (var i = headerIndex + 1; i < end; i++)
        {
            if (lines[i].Contains(TokenCommandSignature, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>顶层键只认首个 <c>[table]</c> 之前的行，避免误改 [profiles.*] 里的同名键。</summary>
    private static int IndexOfFirstTable(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('[')) return i;
        }
        return lines.Count;
    }

    private static List<string> SetTopLevelKey(List<string> lines, string key, string renderedValue)
    {
        var limit = IndexOfFirstTable(lines);
        for (var i = 0; i < limit; i++)
        {
            if (TryMatchKey(lines[i], key))
            {
                lines[i] = $"{key} = {renderedValue}";
                return lines;
            }
        }

        // 跳过紧贴 [table] 的连续空行，让新键不与表头粘连。
        var insertAt = limit;
        while (insertAt > 0 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
        lines.Insert(insertAt, $"{key} = {renderedValue}");
        return lines;
    }

    private static List<string> RemoveTopLevelKey(List<string> lines, string key)
    {
        var limit = IndexOfFirstTable(lines);
        for (var i = limit - 1; i >= 0; i--)
        {
            if (TryMatchKey(lines[i], key)) lines.RemoveAt(i);
        }
        return lines;
    }

    /// <summary>
    /// 把某个顶层键还原成"用户改动前的那一行"；原始配置里没有这个键就删掉它。
    ///
    /// <para>用于 <c>web_search</c> 这类我们只在特定端点下才需要覆盖的键：切到支持它的
    /// 端点时必须把控制权交还用户，而不是把上一个端点写下的 <c>disabled</c> 留着——
    /// 那等于替用户永久关掉了联网搜索。</para>
    /// </summary>
    private static List<string> RestoreTopLevelKey(List<string> lines, string headPayload, string key)
    {
        var original = DecodeHead(headPayload);
        var originalLine = original is null
            ? null
            : original.Take(IndexOfFirstTable(original)).FirstOrDefault(l => TryMatchKey(l, key));

        lines = RemoveTopLevelKey(lines, key);
        if (originalLine is null) return lines;

        var equals = originalLine.IndexOf('=');
        return equals < 0
            ? lines
            : SetTopLevelKey(lines, key, originalLine[(equals + 1)..].Trim());
    }

    /// <summary>判断某行是否为给定顶层键的赋值（忽略缩进与注释行）。</summary>
    private static bool TryMatchKey(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#') return false;
        if (!trimmed.StartsWith(key, StringComparison.Ordinal)) return false;
        return trimmed[key.Length..].TrimStart().StartsWith('=');
    }

    // ---------- TOML 字面量 ----------

    /// <summary>基本字符串：转义反斜杠与引号，用于标识符与显示名。</summary>
    private static string TomlString(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>
    /// 字面量字符串：反斜杠原样保留。Windows 路径与 URL 用它可读性好得多，
    /// 也避免基本字符串里 <c>\U</c> 之类被当成非法转义。
    /// </summary>
    private static string TomlLiteral(string value) => $"'{value.Replace("'", "''")}'";

    private static string TomlKey(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? value
            : TomlString(value);

    /// <summary>
    /// 命令式取 token 时执行的程序路径：即本应用自身。
    /// 用主模块路径而非 Assembly.Location——单文件发布下后者是空串。
    /// </summary>
    private string TokenCommandPath()
    {
        // 只用调用方显式提供的路径。绝不回落到 Environment.ProcessPath：那是"谁加载了
        // 这个库"而不是"谁是这个应用"，会让任何测试/辅助程序把自己的 exe 写进用户配置。
        if (!string.IsNullOrWhiteSpace(AppExecutablePath)) return AppExecutablePath;

        // 未指定时的兜底：用与库同目录的正式程序名，至少不会指向一个临时脚手架。
        var baseDir = AppContext.BaseDirectory;
        return string.IsNullOrWhiteSpace(baseDir)
            ? "ChatGptAccountManager.exe"
            : Path.Combine(baseDir, "ChatGptAccountManager.exe");
    }

    // ---------- 读写 ----------

    /// <summary>原始文本 + 待保持的编码特征。</summary>
    private sealed record RawConfig(string Text, string NewLine, bool HasBom);

    private async Task<RawConfig> ReadRawAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigPath))
        {
            return new RawConfig(string.Empty, Environment.NewLine, false);
        }

        var bytes = await File.ReadAllBytesAsync(ConfigPath, cancellationToken).ConfigureAwait(false);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        return new RawConfig(text, DetectNewLine(text), hasBom);
    }

    private static string DetectNewLine(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') return i > 0 && text[i - 1] == '\r' ? "\r\n" : "\n";
            if (text[i] == '\r') return i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
        }
        return Environment.NewLine;
    }

    /// <summary>
    /// 写入并校验：<b>本应用不管理的部分</b>必须逐行不变，且产物能被 TOML 解析，否则回滚。
    /// 管理范围即管理块 + 三个顶层键，二者之外的任何改动都是 bug。
    /// </summary>
    private async Task<string?> WriteWithValidationAsync(
        RawConfig original, RawConfig updated, CancellationToken cancellationToken)
    {
        if (!string.Equals(UserContent(original.Text), UserContent(updated.Text), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("配置校验失败：管理范围之外的配置发生了意外改动，已中止写入。");
        }

        Directory.CreateDirectory(CodexHome);

        var bytes = new UTF8Encoding(updated.HasBom).GetBytes(updated.Text);

        string? backupPath = null;
        if (File.Exists(ConfigPath))
        {
            backupPath = $"{ConfigPath}.{DateTime.Now:yyyyMMdd-HHmmssfff}.bak";
            File.Copy(ConfigPath, backupPath, false);
        }

        var temp = Path.Combine(CodexHome, $".config.toml.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);

            // 产物必须是可解析的 TOML——坏文件会让 Codex 完全无法启动。
            TomlValidation.EnsureParsable(temp);

            if (File.Exists(ConfigPath))
            {
                File.Replace(temp, ConfigPath, null, true);
            }
            else
            {
                File.Move(temp, ConfigPath);
            }

            return backupPath;
        }
        catch
        {
            // 回滚：写失败时保证原文件仍在。
            if (backupPath is not null && File.Exists(backupPath) && !File.Exists(ConfigPath))
            {
                File.Copy(backupPath, ConfigPath, true);
            }
            throw;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task WriteAtomicAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(CodexHome, $".config.toml.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            if (File.Exists(ConfigPath))
            {
                File.Replace(temp, ConfigPath, null, true);
            }
            else
            {
                File.Move(temp, ConfigPath);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// 剥掉本应用管理的全部内容后剩下的用户配置——用于写入前的一致性校验。
    /// 只要这个指纹不变，就说明我们没碰到任何不属于自己的东西。
    /// </summary>
    private static string UserContent(string text)
    {
        var lines = RemoveManaged(SplitLines(text));
        lines = RemoveTopLevelKey(lines, "model");
        lines = RemoveTopLevelKey(lines, "model_provider");
        lines = RemoveTopLevelKey(lines, "model_catalog_json");
        // web_search 由我们按端点能力改写，不算用户内容——否则每次切换的写入校验
        // 都会判成"管理范围外被改动"而整体中止。
        lines = RemoveTopLevelKey(lines, "web_search");
        return string.Join('\n', lines);
    }

    private static CodexConfigSnapshot Parse(string content, string tokenCommandPath)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new CodexConfigSnapshot();
        }

        var lines = SplitLines(content);
        var limit = IndexOfFirstTable(lines);

        string? model = null, provider = null, catalog = null;
        for (var i = 0; i < limit; i++)
        {
            // model_catalog_json 必须比 model 先判——否则 StartsWith("model") 会误吞它。
            if (TryMatchKey(lines[i], "model_catalog_json")) catalog = Unquote(ValueOf(lines[i]));
            else if (TryMatchKey(lines[i], "model_provider")) provider = Unquote(ValueOf(lines[i]));
            else if (TryMatchKey(lines[i], "model")) model = Unquote(ValueOf(lines[i]));
        }

        // "本应用是否改过这个文件"按结构判定：有顶层记录，或有自己的 provider 表。
        // 不看块标记——桌面版会把悬空注释搬到文件末尾，标记位置不可靠。
        var managed = FindRecordedHeadPayload(lines) is not null
            || HasManagedProviderTable(lines, out _);

        // 检出旧版危险布局：起止标记都在，但两者之间夹着大量非托管内容。
        // 桌面版把结束标记搬到文件末尾就会出现这种形态。
        var legacyLayout = DetectLegacyBlockLayout(lines);

        // provider 与 catalog 都在才算第三方模式——缺任一项 Codex 都会报错或行为异常。
        var mode = !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(catalog)
            ? CodexRoutingMode.ThirdParty
            : CodexRoutingMode.Official;

        // 命令式配方：从管理块里取出 command = '...' 以便检测它是否还有效。
        var tokenCommand = mode == CodexRoutingMode.ThirdParty
            ? ExtractTokenCommand(lines)
            : null;

        return new CodexConfigSnapshot
        {
            Mode = mode,
            Model = model,
            ModelProvider = provider,
            ModelCatalogJson = catalog,
            HasManagedBlock = managed,
            HasLegacyBlockLayout = legacyLayout,
            CatalogFileExists = !string.IsNullOrWhiteSpace(catalog) && File.Exists(catalog),
            StoredTokenCommandPath = tokenCommand,
            ExpectedTokenCommandPath = tokenCommandPath,
            TokenCommandExists = string.IsNullOrWhiteSpace(tokenCommand) || File.Exists(tokenCommand),
        };
    }

    /// <summary>从管理块的 auth 行里取出 command 的值。</summary>
    private static string? ExtractTokenCommand(List<string> lines)
    {
        foreach (var line in lines)
        {
            var index = line.IndexOf("auth = {", StringComparison.Ordinal);
            if (index < 0) continue;
            if (!TryMatchKey(line, "auth")) continue;

            var commandIndex = line.IndexOf("command =", index, StringComparison.Ordinal);
            if (commandIndex < 0) continue;

            var rest = line[(commandIndex + "command =".Length)..].TrimStart();
            if (rest.Length == 0) continue;

            var quote = rest[0];
            if (quote is not ('\'' or '"')) continue;

            // 字面量字符串里的 '' 表示一个单引号；本应用的路径不含引号，简单取到下一个同类引号即可。
            var end = rest.IndexOf(quote, 1);
            if (end <= 0) continue;

            return rest[1..end];
        }
        return null;
    }

    private static string ValueOf(string line)
    {
        var index = line.IndexOf('=');
        return index < 0 ? string.Empty : line[(index + 1)..].Trim();
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }
        return trimmed;
    }

    private static List<string> SplitLines(string text) =>
        [.. text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')];

    private static string JoinLines(List<string> lines, string newLine) => string.Join(newLine, lines);

    public void Dispose() => _gate.Dispose();
}
