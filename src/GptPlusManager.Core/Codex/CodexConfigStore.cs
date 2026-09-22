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

    /// <summary>是否检测到本应用写入的管理块。</summary>
    public bool HasManagedBlock { get; init; }

    /// <summary>目录指向的文件是否真的存在——不存在时 Codex 会直接启动失败。</summary>
    public bool CatalogFileExists { get; init; }
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
/// </list>
/// </summary>
public sealed class CodexConfigStore : IDisposable
{
    private const string BeginMarker = "# --- GptPlus Manager managed block ---";
    private const string EndMarker = "# --- end GptPlus Manager managed block ---";

    /// <summary>
    /// 管理块里记录"改动前顶层区域原文"的那一行。
    ///
    /// <para>为什么不直接在切回官方时删掉三个顶层键：用户本来就可能自己设了
    /// <c>model = "..."</c>。我们只是<b>覆盖</b>了它，删掉就等于把用户的设置抹了。</para>
    ///
    /// <para>所以进第三方模式前先把整个顶层区域（首个 <c>[table]</c> 之前的所有行）
    /// 原样记下来，切回时整段还原——字节级精确，也不依赖"我们插在第几行"这种脆弱假设。</para>
    /// </summary>
    private const string HeadMarker = "# gptplus-head:";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public CodexConfigStore(string? userProfile = null)
    {
        var profile = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfile;
        CodexHome = Path.Combine(profile, ".codex");
        ConfigPath = Path.Combine(CodexHome, "config.toml");
    }

    public string CodexHome { get; }
    public string ConfigPath { get; }

    public async Task<CodexConfigSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Parse((await ReadRawAsync(cancellationToken).ConfigureAwait(false)).Text);
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
            var updated = BuildThirdPartyConfig(original, provider, catalogPath, apiToken);
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
            return BuildThirdPartyConfig(original, provider, catalogPath, apiToken).Text;
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

    // ---------- 构建 ----------

    private static RawConfig BuildThirdPartyConfig(
        RawConfig original, ProviderDefinition provider, string catalogPath, string? apiToken)
    {
        var lines = SplitLines(original.Text);

        // 已经处在第三方模式时，管理块里那份"原始顶层区域"才是权威记录——必须复用它。
        // 若在这里重新计算，抄到的会是我们自己上次写进去的 model = "<第三方模型>"，
        // 于是切回官方时"还原"成一个第三方配置，用户的设置就永久丢了。
        var recordedHead = FindRecordedHeadPayload(lines)
            ?? RecordCurrentHead(lines);
        // 清掉上一次管理的块与键，避免反复切换时叠加。
        lines = RemoveManaged(lines);

        // 顶层三处必须一起改，缺任何一个 Codex 都会报错。
        lines = SetTopLevelKey(lines, "model", TomlString(provider.Models[0].Slug));
        lines = SetTopLevelKey(lines, "model_provider", TomlString(provider.SafeTomlKey()));
        lines = SetTopLevelKey(lines, "model_catalog_json", TomlLiteral(catalogPath));

        // 追加管理块。三个顶层键必须在文件头部，而块内的 [model_providers.*] 是表，
        //    只能出现在顶层键之后——插到首个已存在的 [table] 之前正好两全。
        //
        //    块本身不加前导空行：那会在管理范围之外留下一个我们"多出来"的空行，
        //    既让校验指纹对不上，也让切回官方时无法字节级还原。
        var managed = new List<string>
        {
            BeginMarker,
            $"{HeadMarker} {recordedHead}",
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
                $"auth = {{ command = {TomlLiteral(TokenCommandPath())}, args = [\"--provider-token\", {TomlString(provider.Id)}] }}");
        }

        managed.Add(EndMarker);
        // 块尾留一个空行作视觉分隔，否则 "# --- end ---" 会和用户的下一个 [table]
        // 粘在一起，读起来像出错了。这一行算管理区的一部分，移除时一并吃掉。
        managed.Add(string.Empty);

        lines.InsertRange(IndexOfFirstTable(lines), managed);

        return original with { Text = JoinLines(lines, original.NewLine) };
    }

    private static RawConfig BuildOfficialConfig(RawConfig original)
    {
        var lines = SplitLines(original.Text);

        // 没有管理块说明本应用从未改过这个文件——此时"切回官方"必须是彻底的空操作，
        // 否则会把用户自己写的顶层 model 一起删掉。
        if (!lines.Any(l => l.Trim() == BeginMarker))
        {
            return original;
        }

        var recordedHead = FindRecordedHeadPayload(lines) is { } payload
            ? DecodeHead(payload)
            : null;
        var body = RemoveManaged(lines);

        if (recordedHead is not null)
        {
            // 整段还原改动前的顶层区域——字节级精确。
            var limit = IndexOfFirstTable(body);
            return original with
            {
                Text = JoinLines([.. recordedHead, .. body.Skip(limit)], original.NewLine),
            };
        }

        // 记录损坏（用户手删了那行？）时退回保守路径：至少把管理键清干净，
        // 不留下一个 Codex 无法启动的半状态。
        body = RemoveTopLevelKey(body, "model");
        body = RemoveTopLevelKey(body, "model_provider");
        body = RemoveTopLevelKey(body, "model_catalog_json");
        return original with { Text = JoinLines(body, original.NewLine) };
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
    /// 移除管理块（含块内 provider 定义）、两端标记，以及块尾那个分隔空行。
    /// 分隔空行由本应用写入，因此也由本应用负责吃掉——这样反复切换不会让空行越积越多。
    /// </summary>
    private static List<string> RemoveManaged(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        var inside = false;
        var afterEnd = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == BeginMarker) { inside = true; afterEnd = false; continue; }
            if (trimmed == EndMarker) { inside = false; afterEnd = true; continue; }
            if (inside) continue;

            // 块尾第一个空行属于管理区；只吃一个，用户自己的空行不受影响。
            if (afterEnd && trimmed.Length == 0) { afterEnd = false; continue; }
            afterEnd = false;

            result.Add(line);
        }
        return result;
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
    private static string TokenCommandPath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path)) return path;

        var module = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        return module ?? "ChatGptAccountManager.exe";
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
        return string.Join('\n', lines);
    }

    private static CodexConfigSnapshot Parse(string content)
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

        var managed = lines.Any(l => l.Trim() == BeginMarker);
        // provider 与 catalog 都在才算第三方模式——缺任一项 Codex 都会报错或行为异常。
        var mode = !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(catalog)
            ? CodexRoutingMode.ThirdParty
            : CodexRoutingMode.Official;

        return new CodexConfigSnapshot
        {
            Mode = mode,
            Model = model,
            ModelProvider = provider,
            ModelCatalogJson = catalog,
            HasManagedBlock = managed,
            CatalogFileExists = !string.IsNullOrWhiteSpace(catalog) && File.Exists(catalog),
        };
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
