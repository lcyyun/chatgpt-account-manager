using System.Net;
using System.Security.Cryptography;
using System.Text;
using GptPlusManager.Core.Network;
using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Tests;

public sealed class AuthClientPkceTests
{
    [Fact]
    public async Task LoginWithLoopbackAsync_UsesRfc7636LengthBase64UrlPkcePair()
    {
        using var directory = new TemporaryDirectory();
        using var tokenStore = new LegacyTokenStore(directory.Path);
        var tokenHandler = new RecordingTokenHandler();
        using var tokenHttpClient = new HttpClient(tokenHandler);
        using var authClient = new AuthClient(tokenHttpClient, tokenStore);
        var authorizationUriSource = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var loginTask = authClient.LoginWithLoopbackAsync(
            uri => { _ = authorizationUriSource.TrySetResult(uri); },
            openSystemBrowser: false,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: timeout.Token);
        var authorizationUri = await authorizationUriSource.Task.WaitAsync(timeout.Token);
        var query = ParseQuery(authorizationUri.Query);
        var challenge = query["code_challenge"];
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(43, challenge.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", challenge);
        Assert.DoesNotContain('=', challenge);

        var callbackUri = $"{query["redirect_uri"]}?code=fixture-code&state={Uri.EscapeDataString(query["state"])}";
        using var loopbackHandler = new HttpClientHandler { UseProxy = false };
        using var loopbackClient = new HttpClient(loopbackHandler);
        using var callbackResponse = await loopbackClient.GetAsync(callbackUri, timeout.Token);
        var result = await loginTask;

        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        Assert.True(result.Succeeded, result.Message);
        var verifier = tokenHandler.Form["code_verifier"];
        Assert.Equal(64, verifier.Length);
        Assert.InRange(verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.DoesNotContain('=', verifier);
        Assert.Equal(challenge, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair.Length == 2 ? pair[1] : string.Empty));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class RecordingTokenHandler : HttpMessageHandler
    {
        public IReadOnlyDictionary<string, string> Form { get; private set; } =
            new Dictionary<string, string>();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Form = ParseQuery(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"fixture-access\",\"refresh_token\":\"fixture-refresh\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
