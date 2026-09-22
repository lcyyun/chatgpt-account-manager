using System.Text;
using GptPlusManager.Core.Codex;

namespace GptPlusManager.Core.Tests;

public sealed class CodexConfigStoreTests
{
    // A realistic config.toml: comments, blank runs, unknown keys, and — critically —
    // a [profiles.*] table that reuses the same key names we manage at top level.
    // Every one of these must survive a mode switch byte for byte.
    private const string RealisticConfig = """
        # my own notes about this file
        model = "gpt-6-astra"
        model_reasoning_effort = "ultra"
        sandbox_mode = "danger-full-access"

        notify = [ "C:\\tools\\notify.exe", "turn-ended" ]

        [marketplaces.openai-bundled]
        source_type = "local"
        source = '\\?\C:\Users\me\.codex\.tmp\bundled'

        [mcp_servers.node_repl]
        args = []
        command = 'C:\tools\node_repl.exe'
        startup_timeout_sec = 120

        [projects.'c:\users\me\documents\x']
        trust_level = "trusted"

        [profiles.work]
        model = "profile-only-model"
        model_provider = "profile-only-provider"
        model_catalog_json = "/should/not/be/touched.json"
        approval_policy = "never"
        """;

    private static ProviderDefinition MakeProvider(ProviderAuthMode mode = ProviderAuthMode.Command) => new()
    {
        Id = "acme",
        DisplayName = "Acme Gateway",
        BaseUrl = "https://api.acme.test/v1",
        AuthMode = mode,
        Models =
        {
            new ProviderModel { Slug = "acme-large", DisplayName = "[第三方] Acme Large" },
            new ProviderModel { Slug = "acme-small", DisplayName = "[第三方] Acme Small" },
        },
    };

    private static async Task<string> SeedAsync(TemporaryDirectory profile, string content = RealisticConfig)
    {
        var store = new CodexConfigStore(profile.Path);
        Directory.CreateDirectory(store.CodexHome);
        await File.WriteAllTextAsync(store.ConfigPath, content, new UTF8Encoding(false));
        return store.ConfigPath;
    }

    private static string MakeCatalog(TemporaryDirectory profile)
    {
        var catalogPath = Path.Combine(profile.Path, "catalog.json");
        File.WriteAllText(catalogPath, """{"models":[]}""");
        return catalogPath;
    }

    /// <summary>
    /// 一个带官方 <c>models_cache.json</c> 模板的临时用户目录。
    ///
    /// <para>目录生成必须克隆官方条目（手写最小条目会在 <c>support_verbosity</c>、
    /// <c>model_messages</c> 等必填字段上报 parse error），所以测试也得喂它一份模板。
    /// 这里用官方条目的真实字段子集，且<b>带 <c>apply_patch_tool_type</c></b>——
    /// 被测逻辑正是"要不要把它剥掉"。</para>
    /// </summary>
    private sealed class TempCatalogProfile : IDisposable
    {
        private readonly TemporaryDirectory _dir = new();

        public TempCatalogProfile()
        {
            var codexHome = System.IO.Path.Combine(_dir.Path, ".codex");
            Directory.CreateDirectory(codexHome);
            File.WriteAllText(System.IO.Path.Combine(codexHome, "models_cache.json"), Template);
        }

        public string Path => _dir.Path;

        public void Dispose() => _dir.Dispose();

        private const string Template = """
            {
              "models": [
                {
                  "slug": "gpt-5.5",
                  "display_name": "GPT-5.5",
                  "description": "official",
                  "default_reasoning_level": "medium",
                  "supported_reasoning_levels": [ { "effort": "medium", "description": "Balanced" } ],
                  "shell_type": "shell_command",
                  "visibility": "list",
                  "supported_in_api": true,
                  "priority": 1,
                  "support_verbosity": true,
                  "apply_patch_tool_type": "freeform",
                  "web_search_tool_type": "text_and_image",
                  "experimental_supported_tools": [],
                  "supports_search_tool": true,
                  "use_responses_lite": false,
                  "model_messages": { "instructions_template": "You are a coding agent." },
                  "input_modalities": [ "text" ]
                }
              ]
            }
            """;
    }

    [Fact]
    public async Task ApplyThirdParty_WritesThreeTopLevelKeysAndProviderBlock()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var catalog = MakeCatalog(profile);
        using var store = new CodexConfigStore(profile.Path);

        var backup = await store.ApplyThirdPartyAsync(MakeProvider(), catalog, null);

        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        var text = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.Contains("model = \"acme-large\"", text);
        Assert.Contains("model_provider = \"acme\"", text);
        Assert.Contains($"model_catalog_json = '{catalog}'", text);
        Assert.Contains("[model_providers.acme]", text);
        Assert.Contains("wire_api = \"responses\"", text);
        Assert.Contains("--provider-token", text);

        var snapshot = await store.LoadAsync();
        Assert.Equal(CodexRoutingMode.ThirdParty, snapshot.Mode);
        Assert.Equal("acme-large", snapshot.Model);
        Assert.Equal("acme", snapshot.ModelProvider);
        Assert.Equal(catalog, snapshot.ModelCatalogJson);
        Assert.True(snapshot.HasManagedBlock);
        Assert.True(snapshot.CatalogFileExists);
    }

    [Fact]
    public async Task ApplyThirdParty_PreservesEverythingOutsideManagedBlock()
    {
        using var profile = new TemporaryDirectory();
        var path = await SeedAsync(profile);
        var before = await File.ReadAllTextAsync(path);
        using var store = new CodexConfigStore(profile.Path);

        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var after = await File.ReadAllTextAsync(store.ConfigPath);
        // The user's comments, blank lines, unknown tables and trusted-project list
        // must all still be there.
        Assert.Contains("# my own notes about this file", after);
        Assert.Contains("notify = [ \"C:\\\\tools\\\\notify.exe\", \"turn-ended\" ]", after);
        Assert.Contains("[marketplaces.openai-bundled]", after);
        Assert.Contains("[mcp_servers.node_repl]", after);
        Assert.Contains("trust_level = \"trusted\"", after);
        Assert.Contains("startup_timeout_sec = 120", after);

        // Everything the app does not manage must be byte-identical, so stripping the
        // managed parts from both sides has to yield the same text.
        Assert.Equal(UserContentOf(before), UserContentOf(after));
    }

    [Fact]
    public async Task ApplyThirdParty_DoesNotTouchProfilesWithSameKeyNames()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var text = await File.ReadAllTextAsync(store.ConfigPath);
        // The [profiles.work] block reuses model / model_provider / model_catalog_json.
        // A naive whole-file replace would clobber these; scope isolation must keep them.
        Assert.Contains("model = \"profile-only-model\"", text);
        Assert.Contains("model_provider = \"profile-only-provider\"", text);
        Assert.Contains("model_catalog_json = \"/should/not/be/touched.json\"", text);
        Assert.Contains("approval_policy = \"never\"", text);
    }

    [Fact]
    public async Task ApplyOfficial_RemovesTopLevelKeysButKeepsProviderTables()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);
        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var backup = await store.ApplyOfficialAsync();

        Assert.NotNull(backup);
        var text = await File.ReadAllTextAsync(store.ConfigPath);

        // 顶层路由键必须清掉，否则 Codex 会认为还在第三方模式。
        Assert.DoesNotContain("model_catalog_json = '", text);
        Assert.DoesNotContain("\nmodel = \"acme-large\"", text);
        Assert.DoesNotContain("gptplus-head", text);
        Assert.DoesNotContain("GptPlus Manager managed block", text);

        // provider 表**保留**：每个对话创建时把自己的 provider 记进会话元数据，
        // 删掉表会让那些对话打不开（"Model provider not found"）。
        // 这些表在不被引用时是惰性的。
        Assert.Contains("[model_providers.acme]", text);
        Assert.Contains("--provider-token", text);

        // Top-level model is gone, but the profile's identically-named keys survive.
        Assert.Contains("model = \"profile-only-model\"", text);
        Assert.Contains("[profiles.work]", text);
        Assert.Contains("trust_level = \"trusted\"", text);

        var snapshot = await store.LoadAsync();
        Assert.Equal(CodexRoutingMode.Official, snapshot.Mode);
    }

    [Fact]
    public async Task SwitchingProviders_KeepsTheOtherProviderTableSoOldThreadsStillOpen()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var catalog = MakeCatalog(profile);
        using var store = new CodexConfigStore(profile.Path);

        // 用第一个供应商建对话（这里用写入配置来代表"对话记住了这个 provider"）。
        var first = MakeProvider();
        first.Id = "first";
        await store.ApplyThirdPartyAsync(first, catalog, null);

        // 再切到第二个供应商。
        var second = MakeProvider();
        second.Id = "second";
        await store.ApplyThirdPartyAsync(second, catalog, null);

        var text = await File.ReadAllTextAsync(store.ConfigPath);

        // 这正是用户报的问题：切到新供应商后，用旧供应商建的对话必须仍能打开，
        // 因此旧 provider 表不能被删掉。
        Assert.Contains("[model_providers.first]", text);
        Assert.Contains("[model_providers.second]", text);
        Assert.Contains("model_provider = \"second\"", text);
    }

    [Fact]
    public async Task ApplyOfficial_WhenAlreadyOfficial_DoesNotWriteOrBackup()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        var backup = await store.ApplyOfficialAsync();

        Assert.Null(backup);
        Assert.Empty(Directory.GetFiles(store.CodexHome, "*.bak"));
    }

    [Fact]
    public async Task RepeatedSwitching_DoesNotAccumulateManagedBlocks()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var catalog = MakeCatalog(profile);
        using var store = new CodexConfigStore(profile.Path);

        for (var i = 0; i < 3; i++)
        {
            await store.ApplyThirdPartyAsync(MakeProvider(), catalog, null);
            await store.ApplyOfficialAsync();
        }
        await store.ApplyThirdPartyAsync(MakeProvider(), catalog, null);

        var text = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.Equal(1, CountOccurrences(text, "[model_providers.acme]"));
        Assert.Equal(1, CountOccurrences(text, "model = \"acme-large\""));
        Assert.Equal(1, CountOccurrences(text, "# gptplus-head:"));
        Assert.Equal(1, CountOccurrences(text, "# gptplus-provider:"));
    }

    [Fact]
    public async Task ApplyingThirdPartyTwiceInARow_RecordsHeadOnlyOnce()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var catalog = MakeCatalog(profile);
        using var store = new CodexConfigStore(profile.Path);

        // Re-applying without switching back must not nest the recorded head — doing so
        // would make a later switch-back restore our own managed block as if it were
        // the user's config.
        await store.ApplyThirdPartyAsync(MakeProvider(), catalog, null);
        await store.ApplyThirdPartyAsync(MakeProvider(), catalog, null);
        await store.ApplyOfficialAsync();

        var text = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.DoesNotContain("GptPlus Manager managed block", text);
        Assert.DoesNotContain("gptplus-head", text);
        Assert.Equal("model = \"gpt-6-astra\"", text.Split('\n')[1].TrimEnd());
    }

    [Fact]
    public async Task RepeatedThirdPartyWithoutSwitching_StillRoundTripsToOriginal()
    {
        using var profile = new TemporaryDirectory();
        var path = await SeedAsync(profile);
        var original = await File.ReadAllTextAsync(path);
        var catalog = MakeCatalog(profile);
        using var store = new CodexConfigStore(profile.Path);

        for (var i = 0; i < 4; i++)
        {
            await store.ApplyThirdPartyAsync(MakeProvider(), catalog, null);
        }
        await store.ApplyOfficialAsync();

        // 用户的内容必须逐字恢复；我方留下的 provider 表是有意保留的（见下方测试），
        // 所以不再要求整文件字节相同，改为比对"用户内容"。
        var after = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.Equal(UserContentOf(original), UserContentOf(after));
    }

    // ---- 端点能力：按实测裁剪工具形态 ----

    /// <summary>
    /// 不勾"自定义工具"时，目录里不能出现 <c>apply_patch_tool_type</c>。
    ///
    /// <para>它会让 Codex 发 <c>type: "custom"</c> 的工具，实测小米端点以
    /// "custom tools require MiMo freeform Responses lite mode" 拒绝整个请求
    /// （DeepSeek 接受）。该字段在 Codex 里只有 Freeform 一个取值，
    /// 所以"不支持"只能用整个字段省略来表达。</para>
    /// </summary>
    [Fact]
    public async Task Catalog_StripsApplyPatchToolType_WhenCustomToolsUnsupported()
    {
        using var profile = new TempCatalogProfile();
        var builder = new ModelCatalogBuilder(profile.Path);

        var provider = MakeProvider();
        provider.SupportsCustomTools = false;
        var output = Path.Combine(profile.Path, "off.json");
        await builder.BuildAsync(provider, output);

        var json = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("apply_patch_tool_type", json);
    }

    [Fact]
    public async Task Catalog_KeepsApplyPatchToolType_WhenCustomToolsSupported()
    {
        using var profile = new TempCatalogProfile();
        var builder = new ModelCatalogBuilder(profile.Path);

        var provider = MakeProvider();
        provider.SupportsCustomTools = true;
        var output = Path.Combine(profile.Path, "on.json");
        await builder.BuildAsync(provider, output);

        var json = await File.ReadAllTextAsync(output);
        Assert.Contains("apply_patch_tool_type", json);
    }

    /// <summary>端点不支持联网搜索时，必须显式写 disabled —— 这是让 Codex 摘掉该工具的唯一开关。</summary>
    [Fact]
    public async Task ApplyThirdParty_WritesWebSearchDisabled_WhenEndpointLacksIt()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        var provider = MakeProvider();
        provider.SupportsWebSearch = false;
        await store.ApplyThirdPartyAsync(provider, MakeCatalog(profile), null);

        var text = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.Contains("web_search = \"disabled\"", text);
    }

    /// <summary>
    /// 端点支持时要"还政于用户"：不能把上一个端点写下的 disabled 留着，
    /// 那等于替用户永久关掉联网搜索。原本没设过就应把键删掉。
    /// </summary>
    [Fact]
    public async Task ApplyThirdParty_RestoresUserWebSearchValue_WhenEndpointSupportsIt()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile, RealisticConfig.Replace(
            "sandbox_mode = \"danger-full-access\"",
            "sandbox_mode = \"danger-full-access\"\nweb_search = \"live\""));
        using var store = new CodexConfigStore(profile.Path);

        // 先用不支持搜索的端点切一次，写下 disabled。
        var limited = MakeProvider();
        limited.SupportsWebSearch = false;
        await store.ApplyThirdPartyAsync(limited, MakeCatalog(profile), null);
        Assert.Contains("web_search = \"disabled\"", await File.ReadAllTextAsync(store.ConfigPath));

        // 再切到支持搜索的端点：用户原来的 live 必须回来。
        var full = MakeProvider();
        full.SupportsWebSearch = true;
        await store.ApplyThirdPartyAsync(full, MakeCatalog(profile), null);

        var text = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.Contains("web_search = \"live\"", text);
        Assert.DoesNotContain("web_search = \"disabled\"", text);
    }

    /// <summary>用户原本没有 web_search 时，切到支持它的端点不应凭空造出一个键。</summary>
    [Fact]
    public async Task ApplyThirdParty_DropsWebSearchKey_WhenUserNeverSetIt()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        var limited = MakeProvider();
        limited.SupportsWebSearch = false;
        await store.ApplyThirdPartyAsync(limited, MakeCatalog(profile), null);

        var full = MakeProvider();
        full.SupportsWebSearch = true;
        await store.ApplyThirdPartyAsync(full, MakeCatalog(profile), null);

        Assert.DoesNotContain("web_search", await File.ReadAllTextAsync(store.ConfigPath));
    }

    [Fact]
    public async Task ApplyThirdParty_PlainTokenMode_WritesBearerTokenAndKeepsOpenAiAuth()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        await store.ApplyThirdPartyAsync(
            MakeProvider(ProviderAuthMode.PlainToken), MakeCatalog(profile), "sk-secret-value");

        var text = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.Contains("requires_openai_auth = true", text);
        Assert.Contains("experimental_bearer_token = \"sk-secret-value\"", text);
        // The two recipes are mutually exclusive in Codex; never emit both.
        Assert.DoesNotContain("auth = {", text);
    }

    [Fact]
    public async Task ApplyThirdParty_CommandMode_EmitsNoPlaintextSecret()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), "sk-should-not-appear");

        var text = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.DoesNotContain("sk-should-not-appear", text);
        Assert.DoesNotContain("experimental_bearer_token", text);
        Assert.DoesNotContain("requires_openai_auth", text);
        Assert.Contains("auth = {", text);
    }

    [Fact]
    public async Task ApplyThirdParty_MissingCatalogFile_RefusesToWrite()
    {
        using var profile = new TemporaryDirectory();
        var path = await SeedAsync(profile);
        var before = await File.ReadAllTextAsync(path);
        using var store = new CodexConfigStore(profile.Path);

        // A dangling catalog path makes Codex fail to start (os error 2), so this
        // must be rejected before anything touches disk.
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ApplyThirdPartyAsync(
            MakeProvider(), Path.Combine(profile.Path, "does-not-exist.json"), null));

        Assert.Equal(before, await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Empty(Directory.GetFiles(store.CodexHome, "*.bak"));
    }

    [Fact]
    public async Task ApplyThirdParty_LeavesNoTempFilesBehind()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        Assert.Empty(Directory.GetFiles(store.CodexHome, "*.tmp"));
    }

    [Fact]
    public async Task SwitchBackAndForth_RestoresUserContentExactly()
    {
        using var profile = new TemporaryDirectory();
        var path = await SeedAsync(profile);
        var original = await File.ReadAllTextAsync(path);
        using var store = new CodexConfigStore(profile.Path);

        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);
        await store.ApplyOfficialAsync();

        // 切回官方必须逐字恢复用户的内容。我方留下的 provider 表是有意为之：
        // 删掉它们会让用该供应商建的对话打不开，所以不要求整文件字节相同。
        var after = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.Equal(UserContentOf(original), UserContentOf(after));
        Assert.Contains("[profiles.work]", after);
        Assert.Contains("model = \"profile-only-model\"", after);
    }

    [Fact]
    public async Task RestoreAsync_BringsBackBackupContent()
    {
        using var profile = new TemporaryDirectory();
        var path = await SeedAsync(profile);
        var original = await File.ReadAllBytesAsync(path);
        using var store = new CodexConfigStore(profile.Path);
        var backup = (await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null))!;

        await store.RestoreAsync(backup);

        Assert.Equal(original, await File.ReadAllBytesAsync(store.ConfigPath));
    }

    [Fact]
    public async Task LoadAsync_InfersThirdPartyOnlyWhenProviderAndCatalogBothPresent()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        // An orphaned model_provider without a catalog would crash Codex; the snapshot
        // treats that half-state as official rather than claiming it is usable.
        await File.WriteAllTextAsync(
            store.ConfigPath,
            "model_provider = \"acme\"\n\n[features]\njs_repl = false\n",
            new UTF8Encoding(false));

        var snapshot = await store.LoadAsync();

        Assert.Equal(CodexRoutingMode.Official, snapshot.Mode);
        Assert.Equal("acme", snapshot.ModelProvider);
    }

    [Fact]
    public async Task ApplyThirdParty_PreservesCrlfLineEndings()
    {
        using var profile = new TemporaryDirectory();
        var crlf = RealisticConfig.Replace("\r\n", "\n").Replace("\n", "\r\n");
        await SeedAsync(profile, crlf);
        using var store = new CodexConfigStore(profile.Path);

        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var after = await File.ReadAllTextAsync(store.ConfigPath);
        // A file that was CRLF must stay CRLF: mixing endings shows up as a spurious
        // diff in the user's own config file.
        Assert.Equal(CountOccurrences(after, "\r\n"), CountOccurrences(after, "\n"));
        Assert.DoesNotContain("\n\n\n", after.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task PreviewThirdParty_DoesNotModifyFile()
    {
        using var profile = new TemporaryDirectory();
        var path = await SeedAsync(profile);
        var before = await File.ReadAllTextAsync(path);
        using var store = new CodexConfigStore(profile.Path);

        var preview = await store.PreviewThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        Assert.Contains("[model_providers.acme]", preview);
        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(store.CodexHome, "*.bak"));
    }

    [Fact]
    public async Task ApplyThirdParty_PathWithSpacesAndBackslashes_ProducesParsableToml()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        // Windows paths contain backslashes; in a basic TOML string \U would be an
        // invalid escape and Codex would refuse to start. Literal strings must be used.
        var nested = Path.Combine(profile.Path, "my folder", "cat.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        await File.WriteAllTextAsync(nested, """{"models":[]}""");

        await store.ApplyThirdPartyAsync(MakeProvider(), nested, null);

        var snapshot = await store.LoadAsync();
        Assert.Equal(nested, snapshot.ModelCatalogJson);
        Assert.True(snapshot.CatalogFileExists);
    }

    [Fact]
    public async Task ApplyThirdParty_RejectsProviderWithoutModels()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var catalog = MakeCatalog(profile);
        using var store = new CodexConfigStore(profile.Path);
        var empty = new ProviderDefinition
        {
            Id = "empty",
            DisplayName = "Empty",
            BaseUrl = "https://api.test/v1",
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ApplyThirdPartyAsync(empty, catalog, null));
    }

    [Fact]
    public async Task Snapshot_ReportsTokenCommandPathAndValidity()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var snapshot = await store.LoadAsync();

        // 命令式条目里记的必须是**注入的**路径。这里断言的是"注入生效"，
        // 而不是"等于某个进程路径"——后者正是会把辅助程序写进用户配置的那个 bug。
        Assert.False(string.IsNullOrWhiteSpace(snapshot.StoredTokenCommandPath));
        Assert.Contains("ChatGptAccountManager.exe", snapshot.StoredTokenCommandPath!);

        // 这个临时目录里没有真的放一个 exe，所以"文件存在"应为 false；
        // 快照报 false 才能让界面提示用户"程序被移动了，请重新应用"。
        Assert.False(snapshot.TokenCommandExists);
    }

    /// <summary>exe 确实存在时，快照要报"有效"。</summary>
    [Fact]
    public async Task Snapshot_ReportsTokenCommandValid_WhenExecutableExists()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var appPath = System.IO.Path.Combine(profile.Path, "App", "ChatGptAccountManager.exe");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(appPath)!);
        await File.WriteAllTextAsync(appPath, string.Empty);

        using var store = new CodexConfigStore(profile.Path, appPath);
        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var snapshot = await store.LoadAsync();
        Assert.True(snapshot.TokenCommandExists);
        Assert.True(snapshot.TokenCommandMatchesCurrentApp);
    }

    /// <summary>
    /// 注入的 exe 路径必须原样写进 <c>auth.command</c>，且快照把它报成"与当前应用一致"。
    /// </summary>
    [Fact]
    public async Task ApplyThirdParty_UsesInjectedExecutablePath_NotTheHostProcessPath()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var appPath = System.IO.Path.Combine(profile.Path, "App", "ChatGptAccountManager.exe");
        using var store = new CodexConfigStore(profile.Path, appPath);

        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var text = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.Contains(appPath.Replace("\\", "\\"), text);

        // 关键：绝不能出现测试宿主自己的 exe（testhost / dotnet）。
        Assert.DoesNotContain("testhost", text, StringComparison.OrdinalIgnoreCase);

        var snapshot = await store.LoadAsync();
        Assert.Equal(appPath, snapshot.StoredTokenCommandPath);
        Assert.True(snapshot.TokenCommandMatchesCurrentApp);
    }

    /// <summary>程序被移动后，快照要能把路径不一致报出来，供界面提示修复。</summary>
    [Fact]
    public async Task Snapshot_FlagsTokenCommandNotMatchingInjectedPath()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var moved = System.IO.Path.Combine(profile.Path, "moved", "ChatGptAccountManager.exe");

        using (var original = new CodexConfigStore(profile.Path,
                   System.IO.Path.Combine(profile.Path, "old", "ChatGptAccountManager.exe")))
        {
            await original.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);
        }

        using var current = new CodexConfigStore(profile.Path, moved);
        var snapshot = await current.LoadAsync();

        Assert.False(snapshot.TokenCommandMatchesCurrentApp);
        Assert.Equal(moved, snapshot.ExpectedTokenCommandPath);
    }

    [Fact]
    public async Task Snapshot_FlagsTokenCommandThatNoLongerExists()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using (var store = new CodexConfigStore(profile.Path))
        {
            await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);
        }

        // Simulate the app having been moved: rewrite the recorded command to a dead path.
        var configPath = Path.Combine(profile.Path, ".codex", "config.toml");
        var text = await File.ReadAllTextAsync(configPath);
        var patched = System.Text.RegularExpressions.Regex.Replace(
            text, @"command = '[^']*'", @"command = 'C:\gone\missing\App.exe'");
        Assert.NotEqual(text, patched);
        await File.WriteAllTextAsync(configPath, patched);

        using var reopened = new CodexConfigStore(profile.Path);
        var snapshot = await reopened.LoadAsync();

        // Codex would only report a baffling "failed to resolve external auth" here,
        // so the snapshot must be able to tell the UI the path is dead.
        Assert.Equal(@"C:\gone\missing\App.exe", snapshot.StoredTokenCommandPath);
        Assert.False(snapshot.TokenCommandExists);
    }

    [Fact]
    public async Task Snapshot_OfficialModeHasNoTokenCommand()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        var snapshot = await store.LoadAsync();

        Assert.Null(snapshot.StoredTokenCommandPath);
        Assert.True(snapshot.TokenCommandExists);
    }

    [Fact]
    public async Task Snapshot_DetectsTokenCommandNotPointingAtCurrentApp()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);
        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var configPath = Path.Combine(profile.Path, ".codex", "config.toml");
        var text = await File.ReadAllTextAsync(configPath);
        await File.WriteAllTextAsync(configPath, System.Text.RegularExpressions.Regex.Replace(
            text, @"command = '[^']*'", @"command = 'C:\somewhere\else\Old.exe'"));

        using var reopened = new CodexConfigStore(profile.Path);
        var snapshot = await reopened.LoadAsync();

        // This is what tells the UI "the app moved; re-apply to fix it".
        Assert.False(snapshot.TokenCommandMatchesCurrentApp);
    }

    [Fact]
    public async Task RepairTokenCommandPath_RewritesStalePathToCurrentApp()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var catalog = MakeCatalog(profile);
        using var store = new CodexConfigStore(profile.Path);
        await store.ApplyThirdPartyAsync(MakeProvider(), catalog, null);

        var configPath = Path.Combine(profile.Path, ".codex", "config.toml");
        var text = await File.ReadAllTextAsync(configPath);
        await File.WriteAllTextAsync(configPath, System.Text.RegularExpressions.Regex.Replace(
            text, @"command = '[^']*'", @"command = 'C:\somewhere\else\Old.exe'"));

        var backup = await store.RepairTokenCommandPathAsync();

        Assert.NotNull(backup);
        var snapshot = await store.LoadAsync();
        Assert.True(snapshot.TokenCommandMatchesCurrentApp);
        Assert.NotEqual(@"C:\somewhere\else\Old.exe", snapshot.StoredTokenCommandPath);

        // Repair must not disturb anything else in the file.
        var repaired = await File.ReadAllTextAsync(configPath);
        Assert.Contains("[model_providers.acme]", repaired);
        Assert.Contains("[profiles.work]", repaired);
        Assert.Contains("model = \"acme-large\"", repaired);
    }

    [Fact]
    public async Task RepairTokenCommandPath_NoOpWhenAlreadyCorrect()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);
        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var backup = await store.RepairTokenCommandPathAsync();

        // Nothing to fix -> no write, no backup.
        Assert.Null(backup);
    }

    [Fact]
    public async Task RepairTokenCommandPath_NoOpInOfficialMode()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);

        var backup = await store.RepairTokenCommandPathAsync();

        Assert.Null(backup);
    }

    /// <summary>
    /// <b>防污染回归</b>：记录的路径有效时，即使它与注入路径不一致也绝不改写。
    ///
    /// <para>这条来自一次真实事故：辅助程序（测试 / 调试脚手架）注入自己的 exe 路径后
    /// 调用了修复，把用户配置里正确的路径覆盖成了临时程序的路径，Codex 随即报
    /// "wrote non-UTF-8 data"，用户完全看不出是路径问题。只要"路径指向存在的文件"
    /// 就说明配置是好的，没有可修的东西。</para>
    /// </summary>
    [Fact]
    public async Task RepairTokenCommandPath_DoesNotClobberAWorkingPath()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);

        // 用户配置里指向一个真实存在的程序。
        var realApp = System.IO.Path.Combine(profile.Path, "Real", "ChatGptAccountManager.exe");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(realApp)!);
        await File.WriteAllTextAsync(realApp, string.Empty);

        using (var original = new CodexConfigStore(profile.Path, realApp))
        {
            await original.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);
        }

        // 某个辅助程序带着自己的路径调用修复。
        var scaffolding = System.IO.Path.Combine(profile.Path, "_src", "s.exe");
        using var intruder = new CodexConfigStore(profile.Path, scaffolding);
        var backup = await intruder.RepairTokenCommandPathAsync();

        // 什么都没改：没有写入、没有备份，用户配置保持指向那个真实程序。
        Assert.Null(backup);
        var snapshot = await intruder.LoadAsync();
        Assert.Equal(realApp, snapshot.StoredTokenCommandPath);
    }

    [Fact]
    public async Task Snapshot_DetectsLegacyDangerousBlockLayout()
    {
        using var profile = new TemporaryDirectory();
        var dir = Path.Combine(profile.Path, ".codex");
        Directory.CreateDirectory(dir);

        // Reproduce the real accident: the begin marker sits mid-file, the desktop app
        // relocated the end marker to the very last line, so the whole user config looks
        // like it is inside the managed block.
        await File.WriteAllTextAsync(Path.Combine(dir, "config.toml"),
            "model = \"gpt-5\"\n\n" +
            "# --- GptPlus Manager managed block ---\n" +
            "[model_providers.acme]\nname = \"A\"\nwire_api = \"responses\"\n\n" +
            "[desktop]\nlocaleOverride = \"zh-CN\"\n\n" +
            "[plugins.\"x\"]\nenabled = true\n\n" +
            "[windows]\nsandbox = \"elevated\"\n" +
            "# --- end GptPlus Manager managed block ---\n");

        using var store = new CodexConfigStore(profile.Path);
        var snapshot = await store.LoadAsync();

        Assert.True(snapshot.HasLegacyBlockLayout);
    }

    [Fact]
    public async Task Snapshot_LegacyLayoutNotFlaggedForWellFormedConfig()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);
        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var snapshot = await store.LoadAsync();

        // The new format writes single-line markers only, so nothing to warn about.
        Assert.False(snapshot.HasLegacyBlockLayout);
    }

    [Fact]
    public async Task NewFormat_WritesSingleLineMarkersOnly()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);
        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var text = await File.ReadAllTextAsync(store.ConfigPath);

        // The fragile begin/end pair must be gone: one relocated marker used to swallow
        // the entire user config.
        Assert.DoesNotContain("GptPlus Manager managed block", text);
        Assert.Contains("# gptplus-provider: acme", text);
        Assert.Contains("# gptplus-head:", text);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Test-side mirror of the store's "what the app does not manage" fingerprint:
    /// drop the managed block (and the one separator blank line that belongs to it)
    /// plus the three top-level keys. A mode switch must leave this text identical,
    /// or it touched something it does not own.
    /// </summary>
    private static string UserContentOf(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var kept = new List<string>();
        var index = 0;

        while (index < lines.Count)
        {
            var trimmed = lines[index].Trim();

            // 本应用写的单行注释（含旧版的块标记，它们现在只作为单行注释处理）。
            if (trimmed is "# --- GptPlus Manager managed block ---"
                         or "# --- end GptPlus Manager managed block ---"
                || trimmed.StartsWith("# gptplus-head:", StringComparison.Ordinal)
                || trimmed.StartsWith("# gptplus-provider:", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            // 自己的 provider 表：整张跳过（表头 + 表体）。
            if (trimmed.StartsWith("[model_providers.", StringComparison.Ordinal))
            {
                index++;
                while (index < lines.Count && !lines[index].TrimStart().StartsWith('[')) index++;
                continue;
            }

            kept.Add(lines[index]);
            index++;
        }

        var limit = kept.FindIndex(l => l.TrimStart().StartsWith('['));
        if (limit < 0) limit = kept.Count;
        for (var i = limit - 1; i >= 0; i--)
        {
            var trimmed = kept[i].TrimStart();
            if (trimmed.StartsWith('#') || trimmed.Length == 0) continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            var key = trimmed[..eq].Trim();
            if (key is "model" or "model_provider" or "model_catalog_json" or "web_search") kept.RemoveAt(i);
        }
        return string.Join('\n', kept);
    }

    // ---- 回归：桌面版重写 config.toml 会搬动注释 ----

    [Fact]
    public async Task ApplyOfficial_SurvivesDesktopRelocatingTheEndMarker()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        var catalog = MakeCatalog(profile);
        using var store = new CodexConfigStore(profile.Path);
        await store.ApplyThirdPartyAsync(MakeProvider(), catalog, null);

        // 复现真实事故：桌面版重写 config.toml，把悬空注释（我们的结束标记）搬到文件末尾，
        // 于是用户整个配置都落进了 begin/end 之间。旧实现按范围删，点"切回官方"就会删光。
        var configPath = Path.Combine(profile.Path, ".codex", "config.toml");
        var text = await File.ReadAllTextAsync(configPath);
        var relocated = text
            .Replace("# --- end GptPlus Manager managed block ---", string.Empty)
            + "\n# --- end GptPlus Manager managed block ---\n";
        await File.WriteAllTextAsync(configPath, relocated);

        await store.ApplyOfficialAsync();

        var after = await File.ReadAllTextAsync(store.ConfigPath);
        // 用户的东西必须全都还在。
        Assert.Contains("[marketplaces.openai-bundled]", after);
        Assert.Contains("[mcp_servers.node_repl]", after);
        Assert.Contains("startup_timeout_sec = 120", after);
        Assert.Contains("trust_level = \"trusted\"", after);
        Assert.Contains("model = \"profile-only-model\"", after);
        // 我们写的注释标记必须清干净（provider 表按设计保留，见下方专项测试）。
        Assert.DoesNotContain("gptplus-", after);
    }

    [Fact]
    public async Task ApplyOfficial_DoesNotDeleteUserTablesEvenWithMisplacedMarkers()
    {
        using var profile = new TemporaryDirectory();
        var configPath = Path.Combine(profile.Path, ".codex");
        Directory.CreateDirectory(configPath);
        var file = Path.Combine(configPath, "config.toml");

        // 极端情形：只有开始标记、没有结束标记（桌面版把结束注释整个吃掉了）。
        // 旧实现会认为"从 begin 到文件尾都归它管"，从而删掉后面所有内容。
        await File.WriteAllTextAsync(file,
            "model = \"gpt-5\"\n\n# --- GptPlus Manager managed block ---\n" +
            "[model_providers.acme]\nname = \"A\"\nbase_url = 'https://a/v1'\nwire_api = \"responses\"\n" +
            "auth = { command = 'x.exe', args = [\"--provider-token\", \"acme\"] }\n\n" +
            "[desktop]\nlocaleOverride = \"zh-CN\"\n\n[windows]\nsandbox = \"elevated\"\n");

        using var store = new CodexConfigStore(profile.Path);
        await store.ApplyOfficialAsync();

        var after = await File.ReadAllTextAsync(file);
        Assert.Contains("[desktop]", after);
        Assert.Contains("localeOverride = \"zh-CN\"", after);
        Assert.Contains("[windows]", after);
    }

    [Fact]
    public async Task ApplyOfficial_KeepsProvidersTheUserOwns()
    {
        using var profile = new TemporaryDirectory();
        var configPath = Path.Combine(profile.Path, ".codex");
        Directory.CreateDirectory(configPath);
        var file = Path.Combine(configPath, "config.toml");

        // 用户自己也配了 provider。它不是我们写的，切回官方时绝不能动它。
        await File.WriteAllTextAsync(file,
            "model = \"mine\"\nmodel_provider = \"my-own\"\n\n" +
            "[model_providers.my-own]\nname = \"Mine\"\nbase_url = 'https://mine/v1'\nwire_api = \"responses\"\n");

        using var store = new CodexConfigStore(profile.Path);
        using (var thirdParty = new CodexConfigStore(profile.Path))
        {
            await thirdParty.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);
        }
        await store.ApplyOfficialAsync();

        var after = await File.ReadAllTextAsync(file);

        // 用户自己的 provider 必须原样保留——我们无权动它。
        Assert.Contains("[model_providers.my-own]", after);
        Assert.Contains("base_url = 'https://mine/v1'", after);

        // 我方写的那张表也保留（旧对话可能引用它），但顶层已回到用户自己的设置。
        Assert.Contains("model = \"mine\"", after);
        Assert.Contains("model_provider = \"my-own\"", after);
    }
}
