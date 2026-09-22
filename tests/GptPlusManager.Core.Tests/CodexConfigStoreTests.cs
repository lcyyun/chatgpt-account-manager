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
    public async Task ApplyOfficial_RemovesManagedBlockAndTopLevelKeysOnly()
    {
        using var profile = new TemporaryDirectory();
        await SeedAsync(profile);
        using var store = new CodexConfigStore(profile.Path);
        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);

        var backup = await store.ApplyOfficialAsync();

        Assert.NotNull(backup);
        var text = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.DoesNotContain("model_providers.acme", text);
        Assert.DoesNotContain("GptPlus Manager managed block", text);
        Assert.DoesNotContain("--provider-token", text);
        Assert.DoesNotContain("model_catalog_json = '", text);

        // Top-level model is gone, but the profile's identically-named keys survive.
        Assert.DoesNotContain("\nmodel = \"acme-large\"", text);
        Assert.Contains("model = \"profile-only-model\"", text);
        Assert.Contains("[profiles.work]", text);
        Assert.Contains("trust_level = \"trusted\"", text);

        var snapshot = await store.LoadAsync();
        Assert.Equal(CodexRoutingMode.Official, snapshot.Mode);
        Assert.False(snapshot.HasManagedBlock);
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
        // Count the begin marker exactly; the end marker also contains this substring.
        Assert.Equal(1, CountOccurrences(text, "# --- GptPlus Manager managed block ---"));
        Assert.Equal(1, CountOccurrences(text, "[model_providers.acme]"));
        Assert.Equal(1, CountOccurrences(text, "model = \"acme-large\""));
        Assert.Equal(1, CountOccurrences(text, "# gptplus-head:"));
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
        Assert.DoesNotContain("--provider-token", text);
        Assert.DoesNotContain("gptplus-head", text);
        Assert.Equal("model = \"gpt-6-astra\"", text.Split('\n')[1].TrimEnd());
    }

    [Fact]
    public async Task RepeatedThirdPartyWithoutSwitching_StillRoundTripsToOriginal()
    {
        using var profile = new TemporaryDirectory();
        var path = await SeedAsync(profile);
        var original = await File.ReadAllBytesAsync(path);
        var catalog = MakeCatalog(profile);
        using var store = new CodexConfigStore(profile.Path);

        for (var i = 0; i < 4; i++)
        {
            await store.ApplyThirdPartyAsync(MakeProvider(), catalog, null);
        }
        await store.ApplyOfficialAsync();

        Assert.Equal(original, await File.ReadAllBytesAsync(store.ConfigPath));
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
    public async Task SwitchBackAndForth_RoundTripsToOriginalBytes()
    {
        using var profile = new TemporaryDirectory();
        var path = await SeedAsync(profile);
        var original = await File.ReadAllBytesAsync(path);
        using var store = new CodexConfigStore(profile.Path);

        await store.ApplyThirdPartyAsync(MakeProvider(), MakeCatalog(profile), null);
        await store.ApplyOfficialAsync();

        // Official mode must reproduce the user's file exactly — this is what makes
        // switching back risk-free.
        Assert.Equal(original, await File.ReadAllBytesAsync(store.ConfigPath));
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

        // The command points at the running test host, which exists -> valid.
        Assert.False(string.IsNullOrWhiteSpace(snapshot.StoredTokenCommandPath));
        Assert.True(snapshot.TokenCommandExists);
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
        var inside = false;
        var afterEnd = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == "# --- GptPlus Manager managed block ---") { inside = true; afterEnd = false; continue; }
            if (trimmed == "# --- end GptPlus Manager managed block ---") { inside = false; afterEnd = true; continue; }
            if (inside) continue;
            if (afterEnd && trimmed.Length == 0) { afterEnd = false; continue; }
            afterEnd = false;
            kept.Add(line);
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
            if (key is "model" or "model_provider" or "model_catalog_json") kept.RemoveAt(i);
        }
        return string.Join('\n', kept);
    }
}
