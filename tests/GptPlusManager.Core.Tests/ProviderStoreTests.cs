using System.Text.Json;
using System.Text.Json.Nodes;
using GptPlusManager.Core.Codex;

namespace GptPlusManager.Core.Tests;

public sealed class ProviderStoreTests
{
    private static ProviderDefinition Sample() => new()
    {
        Id = "acme",
        DisplayName = "Acme Gateway",
        BaseUrl = "https://api.acme.test/v1/",
        ContextWindow = 200_000,
        SupportsImages = true,
        Models =
        {
            new ProviderModel { Slug = "acme-large", DisplayName = "[第三方] Acme Large" },
            new ProviderModel { Slug = "  acme-small  ", DisplayName = "" },
        },
    };

    [Fact]
    public async Task ProviderRegistry_RoundTripsDefinitionsAndNormalizes()
    {
        using var data = new TemporaryDirectory();
        var registry = new ProviderRegistry(data.Path);

        await registry.SaveAsync([Sample()], "acme");

        var loaded = Assert.Single(await registry.LoadAsync());
        Assert.Equal("acme", loaded.Id);
        Assert.Equal("https://api.acme.test/v1", loaded.BaseUrl); // trailing slash trimmed
        Assert.Equal("acme", await registry.GetActiveProviderIdAsync());

        // Whitespace trimmed, and a blank display name falls back to the slug.
        Assert.Equal("acme-large", loaded.Models[0].Slug);
        Assert.Equal("acme-small", loaded.Models[1].Slug);
        Assert.Equal("acme-small", loaded.Models[1].DisplayName);
    }

    [Fact]
    public async Task ProviderRegistry_DropsBlankModelsAndDeduplicatesProviders()
    {
        using var data = new TemporaryDirectory();
        var registry = new ProviderRegistry(data.Path);
        var first = Sample();
        var second = Sample();
        second.DisplayName = "Acme Gateway (updated)";
        var modelLess = new ProviderDefinition
        {
            Id = "model-less",
            BaseUrl = "https://blank.test/v1",
            Models = { new ProviderModel { Slug = "   " } },
        };

        await registry.SaveAsync([first, second, modelLess]);

        var loaded = await registry.LoadAsync();
        Assert.Equal(2, loaded.Count);

        // Same id saved twice collapses to the last definition.
        var acme = Assert.Single(loaded, p => p.Id == "acme");
        Assert.Equal("Acme Gateway (updated)", acme.DisplayName);

        // A provider whose only model was blank loses that model; the provider survives
        // so the user does not silently lose the entry, but the store never re-emits
        // a whitespace-only slug into the catalog.
        var empty = Assert.Single(loaded, p => p.Id == "model-less");
        Assert.Empty(empty.Models);
    }

    [Fact]
    public async Task ProviderRegistry_StoresNoSecretsInDefinitionsFile()
    {
        using var data = new TemporaryDirectory();
        var registry = new ProviderRegistry(data.Path);
        var secrets = new ProviderSecrets(data.Path);
        await secrets.SetAsync("acme", "sk-super-secret");

        await registry.SaveAsync([Sample()], "acme");

        // providers.json is meant to be shareable; the key must not be in it.
        var text = await File.ReadAllTextAsync(registry.FilePath);
        Assert.DoesNotContain("sk-super-secret", text);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderSecrets_EncryptsAndDecryptsRoundTrip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI is Windows-only.
        }

        using var data = new TemporaryDirectory();
        var secrets = new ProviderSecrets(data.Path);

        await secrets.SetAsync("acme", "sk-live-abcdef123456");

        Assert.Equal("sk-live-abcdef123456", await secrets.GetAsync("acme"));
        Assert.True(await secrets.HasAsync("acme"));

        // The key must not be legible on disk.
        var raw = await File.ReadAllTextAsync(secrets.FilePath);
        Assert.DoesNotContain("sk-live-abcdef123456", raw);
    }

    [Fact]
    public async Task ProviderSecrets_MissingKeyReturnsNull()
    {
        using var data = new TemporaryDirectory();
        var secrets = new ProviderSecrets(data.Path);

        Assert.Null(await secrets.GetAsync("never-configured"));
        Assert.False(await secrets.HasAsync("never-configured"));
    }

    [Fact]
    public async Task ProviderSecrets_RemoveDeletesOnlyThatProvider()
    {
        using var data = new TemporaryDirectory();
        var secrets = new ProviderSecrets(data.Path);
        await secrets.SetAsync("one", "key-one");
        await secrets.SetAsync("two", "key-two");

        await secrets.RemoveAsync("one");

        Assert.Null(await secrets.GetAsync("one"));
        Assert.Equal("key-two", await secrets.GetAsync("two"));
    }

    [Fact]
    public async Task ProviderRegistry_ClearsActiveIdThatNoLongerExists()
    {
        using var data = new TemporaryDirectory();
        var registry = new ProviderRegistry(data.Path);
        await registry.SaveAsync([Sample()], "acme");

        // Renaming a provider changes its id; the old active id becomes a dangling
        // reference that would show the wrong "currently active" provider in the UI.
        var renamed = Sample();
        renamed.Id = "acme-renamed";
        await registry.SaveAsync([renamed], "acme");

        Assert.Null(await registry.GetActiveProviderIdAsync());

        // And a valid id still sticks.
        await registry.SaveAsync([renamed], "acme-renamed");
        Assert.Equal("acme-renamed", await registry.GetActiveProviderIdAsync());
    }

    // ---- ModelCatalogBuilder ----

    private static string WriteSampleCache(TemporaryDirectory profile)
    {
        // Mirrors the shape of the real ~/.codex/models_cache.json: a models array whose
        // entries carry the required non-empty model_messages object. The builder clones
        // one of these, so the fixture must look like the real thing.
        var messages = new JsonObject
        {
            ["persistent_instructions"] = "fixture instructions",
            ["instructions_template"] = "fixture template {{ model }}",
            ["approvals"] = "fixture approvals",
        };
        var model = new JsonObject
        {
            ["slug"] = "official-fixture",
            ["display_name"] = "Official Fixture",
            ["description"] = "Fixture entry.",
            ["default_reasoning_level"] = "medium",
            ["shell_type"] = "unified_exec",
            ["visibility"] = "list",
            ["supported_in_api"] = true,
            ["priority"] = 1,
            ["context_window"] = 272_000,
            ["max_context_window"] = 872_000,
            ["model_messages"] = messages,
            ["experimental_supported_tools"] = new JsonArray("clock"),
            ["supports_image_detail_original"] = true,
            ["supports_search_tool"] = true,
            ["input_modalities"] = new JsonArray("text", "image"),
            ["comp_hash"] = "3000",
            ["service_tiers"] = new JsonArray(new JsonObject { ["id"] = "priority" }),
            ["availability_nux"] = new JsonObject { ["message"] = "official upsell" },
            ["multi_agent_version"] = "v2",
            // Protocol selectors: these must survive cloning (see the regression test).
            ["use_responses_lite"] = true,
            ["effective_context_window_percent"] = 95,
        };

        var cachePath = Path.Combine(profile.Path, ".codex", "models_cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(cachePath, new JsonObject { ["models"] = new JsonArray(model) }.ToJsonString());
        return cachePath;
    }

    private static ProviderDefinition CatalogProvider() => new()
    {
        Id = "acme",
        DisplayName = "Acme Gateway",
        BaseUrl = "https://api.acme.test/v1",
        ContextWindow = 200_000,
        SupportsImages = false,
        Models =
        {
            new ProviderModel { Slug = "acme-large", DisplayName = "[第三方] Acme Large" },
            new ProviderModel { Slug = "acme-small", DisplayName = "[第三方] Acme Small" },
        },
    };

    [Fact]
    public async Task ModelCatalogBuilder_ClonesTemplateAndOverridesIdentity()
    {
        using var profile = new TemporaryDirectory();
        WriteSampleCache(profile);
        var builder = new ModelCatalogBuilder(profile.Path);
        var output = Path.Combine(profile.Path, "out", "catalog.json");

        var result = await builder.BuildAsync(CatalogProvider(), output);

        Assert.True(File.Exists(output));
        Assert.Equal(2, result.Slugs.Count);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsObject();
        var models = root["models"]!.AsArray();
        Assert.Equal(2, models.Count);

        var first = models[0]!.AsObject();
        Assert.Equal("acme-large", first["slug"]!.GetValue<string>());
        Assert.Equal("[第三方] Acme Large", first["display_name"]!.GetValue<string>());
        Assert.Equal(1, first["priority"]!.GetValue<int>());
        Assert.Equal(2, models[1]!.AsObject()["priority"]!.GetValue<int>());
    }

    [Fact]
    public async Task ModelCatalogBuilder_KeepsRequiredFieldsAndStripsOfficialOnlyOnes()
    {
        using var profile = new TemporaryDirectory();
        WriteSampleCache(profile);
        var builder = new ModelCatalogBuilder(profile.Path);
        var output = Path.Combine(profile.Path, "catalog.json");

        await builder.BuildAsync(CatalogProvider(), output);

        var model = JsonNode.Parse(await File.ReadAllTextAsync(output))!
            .AsObject()["models"]!.AsArray()[0]!.AsObject();

        // These two are hard requirements: omitting model_messages or
        // experimental_supported_tools makes Codex fail to parse the catalog.
        Assert.NotNull(model["model_messages"]);
        Assert.True(model["model_messages"]!.AsObject().Count > 0);
        Assert.NotNull(model["experimental_supported_tools"]);

        // Official-only fields must be gone so no official upsell or tier leaks in.
        Assert.Null(model["availability_nux"]);
        Assert.Null(model["service_tiers"]);
        Assert.Null(model["comp_hash"]);
        Assert.Null(model["multi_agent_version"]);
    }

    [Fact]
    public async Task ModelCatalogBuilder_KeepsProtocolFields()
    {
        using var profile = new TemporaryDirectory();
        WriteSampleCache(profile);
        var builder = new ModelCatalogBuilder(profile.Path);
        var output = Path.Combine(profile.Path, "catalog.json");

        await builder.BuildAsync(CatalogProvider(), output);

        var model = JsonNode.Parse(await File.ReadAllTextAsync(output))!
            .AsObject()["models"]!.AsArray()[0]!.AsObject();

        // use_responses_lite is a protocol selector, not official branding. Stripping it
        // made the Xiaomi endpoint reject every request with
        // "custom tools require MiMo freeform Responses lite mode", so it must survive.
        Assert.NotNull(model["use_responses_lite"]);

        // Same reasoning: these describe how to talk, not whose service it is.
        Assert.NotNull(model["effective_context_window_percent"]);
    }

    [Fact]
    public async Task ModelCatalogBuilder_AppliesContextWindowAndModalities()
    {
        using var profile = new TemporaryDirectory();
        WriteSampleCache(profile);
        var builder = new ModelCatalogBuilder(profile.Path);
        var output = Path.Combine(profile.Path, "catalog.json");

        await builder.BuildAsync(CatalogProvider(), output);

        var model = JsonNode.Parse(await File.ReadAllTextAsync(output))!
            .AsObject()["models"]!.AsArray()[0]!.AsObject();

        // Inheriting the official 272k would blow up requests against a 200k endpoint.
        Assert.Equal(200_000, model["context_window"]!.GetValue<int>());
        Assert.Equal(200_000, model["max_context_window"]!.GetValue<int>());
        Assert.Single(model["input_modalities"]!.AsArray());
        Assert.False(model["supports_image_detail_original"]!.GetValue<bool>());
        Assert.False(model["supports_search_tool"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ModelCatalogBuilder_FallsBackToConservativeContextWindow()
    {
        using var profile = new TemporaryDirectory();
        WriteSampleCache(profile);
        var builder = new ModelCatalogBuilder(profile.Path);
        var output = Path.Combine(profile.Path, "catalog.json");
        var provider = CatalogProvider();
        provider.ContextWindow = null;

        await builder.BuildAsync(provider, output);

        var model = JsonNode.Parse(await File.ReadAllTextAsync(output))!
            .AsObject()["models"]!.AsArray()[0]!.AsObject();
        Assert.Equal(ModelCatalogBuilder.DefaultContextWindow, model["context_window"]!.GetValue<int>());
    }

    [Fact]
    public async Task ModelCatalogBuilder_MissingOfficialCache_FailsWithActionableMessage()
    {
        using var profile = new TemporaryDirectory();
        var builder = new ModelCatalogBuilder(profile.Path);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => builder.BuildAsync(CatalogProvider(), Path.Combine(profile.Path, "catalog.json")));

        Assert.Contains("Codex", exception.Message);
    }

    [Fact]
    public async Task ModelCatalogBuilder_UnusableTemplate_FailsInsteadOfEmittingBadCatalog()
    {
        using var profile = new TemporaryDirectory();
        // Every entry has empty model_messages — Codex would reject this catalog, so the
        // builder must refuse rather than write a file that bricks startup.
        var cachePath = Path.Combine(profile.Path, ".codex", "models_cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(cachePath, """
            {"models":[{"slug":"x","model_messages":{},"experimental_supported_tools":[]}]}
            """);
        var builder = new ModelCatalogBuilder(profile.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.BuildAsync(CatalogProvider(), Path.Combine(profile.Path, "catalog.json")));
    }
}
