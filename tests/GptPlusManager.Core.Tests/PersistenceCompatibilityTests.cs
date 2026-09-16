using System.Text;
using GptPlusManager.Core.Models;
using GptPlusManager.Core.Persistence;

namespace GptPlusManager.Core.Tests;

public sealed class PersistenceCompatibilityTests
{
    [Fact]
    public async Task AccountRepository_LoadsLegacyPascalCaseFieldsAndNormalizesNullStrings()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        const string json = """
            [
              {
                "Email": "fixture.user@example.test",
                "Password": null,
                "Secret": "JBSWY3DPEHPK3PXP",
                "Note": null,
                "PurchasedAt": "2026-01-02",
                "UsageInfo": "fixture summary",
                "UsageCheckedAt": "01-02 03:04",
                "UsageP1": 12.5,
                "UsageP1Desc": "primary fixture",
                "UsageP1ResetAt": 1700000100,
                "UsageP2": 34.5,
                "UsageP2Desc": null,
                "UsageP2ResetAt": 1700000200,
                "UsagePlan": "fixture-plan",
                "SubscriptionUntil": "2026-02-03",
                "SubscriptionUntilUnix": 1700000300,
                "ResetCreditsAvailable": 2,
                "IsInvalid": true,
                "CodexSwitchedAt": null,
                "CreatedAt": "2026-01-01 00:00:00",
                "LegacyOnlyField": "ignored"
              },
              {
                "Email": "minimal@example.test"
              }
            ]
            """;
        await File.WriteAllTextAsync(paths.AccountsFile, json, new UTF8Encoding(false));
        using var repository = new AccountRepository(paths);

        var accounts = await repository.LoadAsync();

        var complete = Assert.IsType<AccountRecord>(accounts[0]);
        Assert.Equal("fixture.user@example.test", complete.Email);
        Assert.Equal(string.Empty, complete.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", complete.Secret);
        Assert.Equal(string.Empty, complete.Note);
        Assert.Equal(12.5, complete.UsageP1);
        Assert.Equal(34.5, complete.UsageP2);
        Assert.Equal(string.Empty, complete.UsageP2Desc);
        Assert.Equal(2, complete.ResetCreditsAvailable);
        Assert.True(complete.IsInvalid);
        Assert.Equal(string.Empty, complete.CodexSwitchedAt);

        var minimal = Assert.IsType<AccountRecord>(accounts[1]);
        Assert.Equal("minimal@example.test", minimal.Email);
        Assert.Equal(string.Empty, minimal.Password);
        Assert.Equal(string.Empty, minimal.Secret);
        Assert.Equal(-1, minimal.UsageP1);
        Assert.Equal(-1, minimal.UsageP2);
        Assert.Equal(0, minimal.ResetCreditsAvailable);
        Assert.False(minimal.IsInvalid);
    }

    [Theory]
    [InlineData("{\"Enabled\":false,\"IntervalHours\":12.5,\"LastKeepAlive\":1700000000}", false, 12.5, 1700000000)]
    [InlineData("{\"Enabled\":true,\"IntervalHours\":2,\"LastKeepAlive\":-10}", true, 6, 0)]
    [InlineData("{\"Enabled\":false,\"IntervalHours\":48}", false, 24, 0)]
    [InlineData("{}", true, 8, 0)]
    public async Task SettingsRepository_LoadsLegacySettingsAndAppliesDefaults(
        string json,
        bool expectedEnabled,
        double expectedInterval,
        double expectedLastKeepAlive)
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        await File.WriteAllTextAsync(paths.SettingsFile, json, new UTF8Encoding(false));
        using var repository = new SettingsRepository(paths);

        var settings = await repository.LoadAsync();

        Assert.Equal(expectedEnabled, settings.Enabled);
        Assert.Equal(expectedInterval, settings.IntervalHours);
        Assert.Equal(expectedLastKeepAlive, settings.LastKeepAlive);
    }

    [Fact]
    public async Task Repositories_AtomicallyWriteAndRoundTripWithoutTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        using var accounts = new AccountRepository(paths);
        using var settings = new SettingsRepository(paths);
        var expectedAccount = new AccountRecord
        {
            Email = "roundtrip@example.test",
            Password = "fixture-password",
            Secret = "JBSWY3DPEHPK3PXP",
            Note = "fixture note",
            UsageP1 = 7.5,
            ResetCreditsAvailable = 3,
            CreatedAt = "2026-01-01 00:00:00"
        };
        var expectedSettings = new SettingsRecord
        {
            Enabled = false,
            IntervalHours = 10.5,
            LastKeepAlive = 1_700_000_000
        };

        await accounts.SaveAsync([expectedAccount]);
        await settings.SaveAsync(expectedSettings);

        var actualAccount = Assert.Single(await accounts.LoadAsync());
        var actualSettings = await settings.LoadAsync();
        Assert.Equal(expectedAccount.Email, actualAccount.Email);
        Assert.Equal(expectedAccount.Password, actualAccount.Password);
        Assert.Equal(expectedAccount.Secret, actualAccount.Secret);
        Assert.Equal(expectedAccount.Note, actualAccount.Note);
        Assert.Equal(expectedAccount.UsageP1, actualAccount.UsageP1);
        Assert.Equal(expectedAccount.ResetCreditsAvailable, actualAccount.ResetCreditsAvailable);
        Assert.Equal(expectedAccount.CreatedAt, actualAccount.CreatedAt);
        Assert.Equal(expectedSettings.Enabled, actualSettings.Enabled);
        Assert.Equal(expectedSettings.IntervalHours, actualSettings.IntervalHours);
        Assert.Equal(expectedSettings.LastKeepAlive, actualSettings.LastKeepAlive);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }
}
