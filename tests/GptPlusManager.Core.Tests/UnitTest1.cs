using GptPlusManager.Core.Models;
using GptPlusManager.Core.Network;
using GptPlusManager.Core.Persistence;
using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Tests;

public sealed class CoreCompatibilityTests
{
    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void TotpMatchesRfc6238(long timestamp, string expected)
    {
        var service = new TotpService();
        var actual = service.GenerateCode(
            "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ",
            timestamp,
            digits: 8);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AccountRepositoryReadsLegacyShapeAndNormalizesNullStrings()
    {
        using var temp = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temp.Path, "accounts.json"),
            "[{\"email\":null,\"Password\":null,\"Secret\":null,\"Note\":null," +
            "\"PurchasedAt\":null,\"UsageInfo\":null,\"UsageCheckedAt\":null," +
            "\"UsageP1\":1,\"UsageP1Desc\":null,\"UsageP1ResetAt\":2," +
            "\"UsageP2\":3,\"UsageP2Desc\":null,\"UsageP2ResetAt\":4," +
            "\"UsagePlan\":null,\"SubscriptionUntil\":null,\"SubscriptionUntilUnix\":5," +
            "\"ResetCreditsAvailable\":6,\"IsInvalid\":true,\"CodexSwitchedAt\":null," +
            "\"CreatedAt\":null}]");
        using var repository = new AccountRepository(temp.Path);

        var account = Assert.Single(await repository.LoadAsync());

        Assert.Equal(string.Empty, account.Email);
        Assert.Equal(string.Empty, account.UsageP2Desc);
        Assert.Equal(string.Empty, account.CreatedAt);
        Assert.Equal(6, account.ResetCreditsAvailable);
        Assert.True(account.IsInvalid);
    }

    [Fact]
    public async Task SettingsRepositoryReadsLegacySettings()
    {
        using var temp = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temp.Path, "settings.json"),
            "{\"enabled\":true,\"INTERVALHOURS\":30,\"LastKeepAlive\":123}");
        using var repository = new SettingsRepository(temp.Path);

        var settings = await repository.LoadAsync();

        Assert.True(settings.Enabled);
        Assert.Equal(24, settings.IntervalHours);
        Assert.Equal(123, settings.LastKeepAlive);
    }

    [Fact]
    public void LegacyTokenPathUsesOriginalFnvRule()
    {
        using var temp = new TemporaryDirectory();
        using var store = new LegacyTokenStore(temp.Path);

        Assert.EndsWith("hello-4f9f2cab.token", store.GetTokenPath("hello"));
    }

    [Fact]
    public async Task LegacyTokenStoreRoundTripsDpapiPayload()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TemporaryDirectory();
        using var store = new LegacyTokenStore(temp.Path);
        var expected = new TokenSet
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            IdToken = "id",
            Email = "test@example.invalid"
        };

        await store.SaveAsync(expected.Email, expected);
        var actual = await store.LoadAsync(expected.Email);

        Assert.NotNull(actual);
        Assert.Equal(expected.AccessToken, actual.AccessToken);
        Assert.Equal(expected.RefreshToken, actual.RefreshToken);
        Assert.Equal(expected.IdToken, actual.IdToken);
        Assert.Equal(expected.Email, actual.Email);
    }

    [Fact]
    public void UsageParserMapsWhamFields()
    {
        using var temp = new TemporaryDirectory();
        using var store = new LegacyTokenStore(temp.Path);
        using var auth = new AuthClient(new HttpClient(), store);
        var client = new UsageClient(new HttpClient(), auth);
        const string json = """
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": {
                  "used_percent": 25,
                  "limit_window_seconds": 18000,
                  "reset_after_seconds": 60
                },
                "secondary_window": {
                  "used_percent": 50,
                  "limit_window_seconds": 604800,
                  "reset_at": 2000000000
                }
              },
              "rate_limit_reset_credits": { "available_count": 2 }
            }
            """;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

        var result = client.ParseUsage(json, now);

        Assert.True(result.Succeeded);
        Assert.Equal("plus", result.Value!.PlanType);
        Assert.Equal(25, result.Value.Primary!.UsedPercent);
        Assert.Equal(1_900_000_060, result.Value.Primary.ResetAtUnix);
        Assert.Equal(50, result.Value.Secondary!.UsedPercent);
        Assert.Equal(2, result.Value.ResetCreditsAvailable);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "GptPlusManager.Core.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
