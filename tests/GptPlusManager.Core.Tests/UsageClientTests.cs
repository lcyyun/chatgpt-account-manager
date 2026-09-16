using System.Net;
using System.Text;
using GptPlusManager.Core.Models;
using GptPlusManager.Core.Network;
using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Tests;

public sealed class UsageClientTests
{
    [Fact]
    public void ParseUsage_ParsesNestedPrimarySecondaryResetTimesAndCredits()
    {
        using var fixture = new UsageClientFixture(new StaticResponseHandler(HttpStatusCode.OK, "{}"));
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        const string json = """
            {
              "fixture_wrapper": {
                "plan_type": "fixture-plan",
                "rate_limit": {
                  "primary_window": {
                    "used_percent": 12.5,
                    "limit_window_seconds": 18000,
                    "reset_after_seconds": 300
                  },
                  "secondary_window": {
                    "used_percent": 67,
                    "limit_window_seconds": 604800,
                    "reset_at": 1700604800
                  }
                },
                "rate_limit_reset_credits": {
                  "available_count": 3
                }
              }
            }
            """;

        var result = fixture.Client.ParseUsage(json, now);

        Assert.True(result.Succeeded, result.Message);
        var usage = Assert.IsType<UsageSnapshot>(result.Value);
        Assert.Equal("fixture-plan", usage.PlanType);
        Assert.Equal(12.5, usage.Primary!.UsedPercent);
        Assert.Equal(18_000, usage.Primary.LimitWindowSeconds);
        Assert.Equal(1_700_000_300, usage.Primary.ResetAtUnix);
        Assert.Equal("5小时窗口已用 12%", usage.Primary.Description);
        Assert.Equal(67, usage.Secondary!.UsedPercent);
        Assert.Equal(604_800, usage.Secondary.LimitWindowSeconds);
        Assert.Equal(1_700_604_800, usage.Secondary.ResetAtUnix);
        Assert.Equal("每周窗口已用 67%", usage.Secondary.Description);
        Assert.Equal(3, usage.ResetCreditsAvailable);
        Assert.Equal("套餐:fixture-plan · 5小时窗口已用 12% · 每周窗口已用 67%", usage.Summary);
        Assert.Equal(now, usage.CheckedAt);
    }

    [Fact]
    public async Task ConsumeResetCreditAsync_ParsesSuccessfulResetResponse()
    {
        const string json = "{\"fixture\":{\"code\":\"reset\",\"windows_reset\":2}}";
        using var fixture = new UsageClientFixture(new StaticResponseHandler(HttpStatusCode.OK, json));
        var account = new AccountRecord
        {
            Email = "fixture.user@example.test",
            ResetCreditsAvailable = 4
        };
        await fixture.SaveFixtureTokensAsync(account.Email);

        var result = await fixture.Client.ConsumeResetCreditAsync(account);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.Value);
        Assert.Equal("reset", result.Value.Code);
        Assert.Equal(2, result.Value.WindowsReset);
        Assert.Equal(3, result.Value.AvailableCredits);
    }

    [Theory]
    [InlineData("nothing_to_reset", 4)]
    [InlineData("no_credit", 0)]
    [InlineData("already_redeemed", 4)]
    public async Task ConsumeResetCreditAsync_ParsesKnownNonSuccessCode(
        string code,
        int expectedAvailableCredits)
    {
        var json = $"{{\"fixture\":{{\"code\":\"{code}\",\"windows_reset\":0}}}}";
        using var fixture = new UsageClientFixture(new StaticResponseHandler(HttpStatusCode.OK, json));
        var account = new AccountRecord
        {
            Email = "fixture.user@example.test",
            ResetCreditsAvailable = 4
        };
        await fixture.SaveFixtureTokensAsync(account.Email);

        var result = await fixture.Client.ConsumeResetCreditAsync(account);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(code, result.Value.Code);
        Assert.Equal(0, result.Value.WindowsReset);
        Assert.Equal(expectedAvailableCredits, result.Value.AvailableCredits);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    private static string CreateUnsignedJwt(string email, string accountId, long expiration)
    {
        var header = Base64Url("{\"alg\":\"none\"}");
        var payload = Base64Url(
            $"{{\"email\":\"{email}\",\"chatgpt_account_id\":\"{accountId}\",\"exp\":{expiration}}}");
        return $"{header}.{payload}.fixture-signature";
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class UsageClientFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly HttpClient _httpClient;
        private readonly AuthClient _authClient;

        public UsageClientFixture(HttpMessageHandler handler)
        {
            TokenStore = new LegacyTokenStore(_directory.Path);
            _httpClient = new HttpClient(handler);
            _authClient = new AuthClient(_httpClient, TokenStore);
            Client = new UsageClient(_httpClient, _authClient);
        }

        public LegacyTokenStore TokenStore { get; }
        public UsageClient Client { get; }

        public Task SaveFixtureTokensAsync(string email)
        {
            var jwt = CreateUnsignedJwt("fixture.user@example.test", "fixture-account-id", 4_102_444_800);
            return TokenStore.SaveAsync(email, new TokenSet
            {
                AccessToken = jwt,
                RefreshToken = "fixture-refresh",
                IdToken = jwt
            });
        }

        public void Dispose()
        {
            _authClient.Dispose();
            _httpClient.Dispose();
            TokenStore.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }
}
