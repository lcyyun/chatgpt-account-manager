using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GptPlusManager.Core.Codex;
using GptPlusManager.Core.Models;
using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Network;

public interface IAuthorizationSession : IDisposable
{
    Uri AuthorizationUri { get; }
    Task<OperationResult<OAuthLoginResult>> Completion { get; }
    bool OpenSystemBrowser();
    void Cancel();
}

public sealed class AuthorizationSession : IAuthorizationSession
{
    private readonly AuthClient _authClient;
    private readonly HttpListener _listener;
    private readonly string _codeVerifier;
    private readonly string _redirectUri;
    private readonly string _state;
    private readonly CancellationTokenSource _timeoutSource;
    private readonly CancellationTokenSource _sessionSource = new();
    private readonly CancellationTokenSource _linkedSource;
    private readonly CancellationTokenRegistration _stopRegistration;

    internal AuthorizationSession(
        AuthClient authClient,
        HttpListener listener,
        Uri authorizationUri,
        string codeVerifier,
        string redirectUri,
        string state,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _authClient = authClient;
        _listener = listener;
        AuthorizationUri = authorizationUri;
        _codeVerifier = codeVerifier;
        _redirectUri = redirectUri;
        _state = state;
        _timeoutSource = new CancellationTokenSource(timeout);
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionSource.Token,
            _timeoutSource.Token);
        _stopRegistration = _linkedSource.Token.Register(StopListener);
        Completion = WaitForCallbackAsync(cancellationToken);
    }

    public Uri AuthorizationUri { get; }
    public Task<OperationResult<OAuthLoginResult>> Completion { get; }

    public bool OpenSystemBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AuthorizationUri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Cancel()
    {
        if (Completion.IsCompleted)
        {
            return;
        }

        try
        {
            _sessionSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        StopListener();
    }

    private async Task<OperationResult<OAuthLoginResult>> WaitForCallbackAsync(
        CancellationToken callerCancellationToken)
    {
        try
        {
            while (true)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_linkedSource.Token)
                    .ConfigureAwait(false);
                var query = AuthClient.ParseQuery(context.Request.Url?.Query);
                var validState = query.TryGetValue("state", out var returnedState)
                    && returnedState.Length == _state.Length
                    && CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(_state),
                        Encoding.UTF8.GetBytes(returnedState));
                var hasCode = query.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code);
                await AuthClient.WriteCallbackResponseAsync(
                        context.Response,
                        validState && hasCode,
                        _linkedSource.Token)
                    .ConfigureAwait(false);
                if (!validState || !hasCode)
                {
                    continue;
                }

                var exchange = await _authClient.ExchangeCodeAsync(
                        code!,
                        _codeVerifier,
                        _redirectUri,
                        _linkedSource.Token)
                    .ConfigureAwait(false);
                return exchange.Succeeded
                    ? OperationResult<OAuthLoginResult>.Success(
                        new OAuthLoginResult(AuthorizationUri, exchange.Value!),
                        "Authorization completed.")
                    : OperationResult<OAuthLoginResult>.Failure(
                        exchange.Message,
                        exchange.ErrorCode,
                        exchange.StatusCode);
            }
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or HttpListenerException or ObjectDisposedException
            && _linkedSource.IsCancellationRequested)
        {
            if (_timeoutSource.IsCancellationRequested
                && !callerCancellationToken.IsCancellationRequested
                && !_sessionSource.IsCancellationRequested)
            {
                return OperationResult<OAuthLoginResult>.Failure(
                    "Timed out waiting for the OAuth callback.",
                    "oauth_timeout");
            }

            throw new OperationCanceledException(
                "OAuth authorization was canceled.",
                exception,
                _linkedSource.Token);
        }
        finally
        {
            StopListener();
            _listener.Close();
            _stopRegistration.Dispose();
            _linkedSource.Dispose();
            _timeoutSource.Dispose();
            _sessionSource.Dispose();
        }
    }

    private void StopListener()
    {
        try
        {
            _listener.Stop();
        }
        catch (Exception exception) when (exception is HttpListenerException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void Dispose() => Cancel();
}

public sealed class AuthClient : IDisposable
{
    private static readonly int[] LoopbackPorts = [1455, 1457];
    private readonly HttpClient _httpClient;
    private readonly LegacyTokenStore _tokenStore;
    private readonly CodexAuthStore? _codexAuthStore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountGates =
        new(StringComparer.OrdinalIgnoreCase);

    public AuthClient(
        HttpClient httpClient,
        LegacyTokenStore tokenStore,
        CodexAuthStore? codexAuthStore = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _codexAuthStore = codexAuthStore;
    }

    public Uri BuildAuthorizationUri(string redirectUri, string codeChallenge, string state)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = OpenAiEndpoints.OAuthClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = OpenAiEndpoints.OAuthScope,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["id_token_add_organizations"] = "true"
        };
        return new Uri($"{OpenAiEndpoints.OAuthAuthorizeUrl}?{BuildQuery(query)}");
    }

    public async Task<OperationResult<TokenSet>> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = OpenAiEndpoints.OAuthClientId,
            ["code_verifier"] = codeVerifier
        };
        return await SendTokenRequestAsync(form, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<TokenSet>> RefreshTokenAsync(
        TokenSet tokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            return OperationResult<TokenSet>.Failure("No refresh token is available.", "missing_refresh_token");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
            ["client_id"] = OpenAiEndpoints.OAuthClientId
        };
        return await SendTokenRequestAsync(form, tokens, cancellationToken).ConfigureAwait(false);
    }

    public IAuthorizationSession StartAuthorizationSession(
        Action<Uri>? authorizationUriAvailable = null,
        bool openSystemBrowser = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var codeVerifier = RandomBase64Url(48);
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var state = RandomBase64Url(16);
        Exception? lastListenerError = null;

        foreach (var port in LoopbackPorts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var redirectUri = $"http://localhost:{port}/auth/callback";
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (Exception exception) when (exception is HttpListenerException or InvalidOperationException)
            {
                lastListenerError = exception;
                listener.Close();
                continue;
            }

            var authorizationUri = BuildAuthorizationUri(redirectUri, codeChallenge, state);
            var session = new AuthorizationSession(
                this,
                listener,
                authorizationUri,
                codeVerifier,
                redirectUri,
                state,
                timeout ?? TimeSpan.FromMinutes(5),
                cancellationToken);
            authorizationUriAvailable?.Invoke(authorizationUri);
            if (openSystemBrowser)
            {
                session.OpenSystemBrowser();
            }

            return session;
        }

        throw new InvalidOperationException(
            $"Could not listen on loopback ports 1455 or 1457. {lastListenerError?.Message}",
            lastListenerError);
    }

    public async Task<OperationResult<OAuthLoginResult>> LoginWithLoopbackAsync(
        Action<Uri>? authorizationUriAvailable = null,
        bool openSystemBrowser = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        IAuthorizationSession session;
        try
        {
            session = StartAuthorizationSession(
                authorizationUriAvailable,
                openSystemBrowser,
                timeout,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return OperationResult<OAuthLoginResult>.Failure(
                exception.Message,
                "loopback_unavailable");
        }

        using (session)
        {
            return await session.Completion.ConfigureAwait(false);
        }
    }

    public async Task<OperationResult<TokenSet>> GetValidTokensAsync(
        AccountRecord account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var key = string.IsNullOrWhiteSpace(account.Email) ? "<empty>" : account.Email;
        var gate = _accountGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (tokens, fromCodex) = await LoadWorkingTokensAsync(account, cancellationToken).ConfigureAwait(false);
            if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                return OperationResult<TokenSet>.Failure(
                    "This account has not been authorized.",
                    "not_authorized");
            }

            var expiration = JwtUtility.GetExpiration(tokens.AccessToken)
                ?? JwtUtility.GetExpiration(tokens.IdToken);
            var shouldRefresh = forceRefresh
                || (expiration.HasValue && expiration.Value <= DateTimeOffset.UtcNow.AddMinutes(5));
            if (!shouldRefresh)
            {
                return OperationResult<TokenSet>.Success(tokens);
            }

            var refreshed = await RefreshTokenAsync(tokens, cancellationToken).ConfigureAwait(false);
            if (!refreshed.Succeeded)
            {
                return refreshed;
            }

            await PersistTokensAsync(account.Email, refreshed.Value!, fromCodex, cancellationToken)
                .ConfigureAwait(false);
            return refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveTokensAsync(
        string email,
        TokenSet tokens,
        CancellationToken cancellationToken = default) =>
        await _tokenStore.SaveAsync(email, tokens, cancellationToken).ConfigureAwait(false);

    private async Task<(TokenSet? Tokens, bool FromCodex)> LoadWorkingTokensAsync(
        AccountRecord account,
        CancellationToken cancellationToken)
    {
        if (_codexAuthStore is not null && !string.IsNullOrWhiteSpace(account.CodexSwitchedAt))
        {
            var codexTokens = await _codexAuthStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (codexTokens is not null
                && string.Equals(codexTokens.Email, account.Email, StringComparison.OrdinalIgnoreCase))
            {
                return (codexTokens, true);
            }
        }

        return (await _tokenStore.LoadAsync(account.Email, cancellationToken).ConfigureAwait(false), false);
    }

    private async Task PersistTokensAsync(
        string email,
        TokenSet tokens,
        bool fromCodex,
        CancellationToken cancellationToken)
    {
        if (fromCodex && _codexAuthStore is not null)
        {
            await _codexAuthStore.SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
        }

        await _tokenStore.SaveAsync(email, tokens, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult<TokenSet>> SendTokenRequestAsync(
        IReadOnlyDictionary<string, string> form,
        TokenSet? oldTokens,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiEndpoints.OAuthTokenUrl)
            {
                Content = new FormUrlEncodedContent(form)
            };
            request.Headers.UserAgent.ParseAdd(OpenAiEndpoints.UserAgent);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return OperationResult<TokenSet>.Failure(
                    "The token endpoint returned invalid JSON.",
                    "invalid_token_response",
                    response.StatusCode);
            }

            using (document)
            {
                var root = document.RootElement;
                var hasError = root.TryGetProperty("error", out var error);
                if (!response.IsSuccessStatusCode || hasError)
                {
                    var errorCode = hasError && error.ValueKind == JsonValueKind.String
                        ? error.GetString()
                        : "token_request_failed";
                    var description = root.TryGetProperty("error_description", out var detail)
                        ? detail.GetString()
                        : null;
                    return OperationResult<TokenSet>.Failure(
                        description ?? $"Token request failed with HTTP {(int)response.StatusCode}.",
                        errorCode,
                        response.StatusCode);
                }

                var tokens = new TokenSet
                {
                    AccessToken = ReadString(root, "access_token"),
                    RefreshToken = ReadString(root, "refresh_token", oldTokens?.RefreshToken),
                    IdToken = ReadString(root, "id_token", oldTokens?.IdToken)
                };
                tokens.Email = JwtUtility.GetStringClaim(tokens.IdToken, "email") ?? oldTokens?.Email ?? string.Empty;
                if (string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    return OperationResult<TokenSet>.Failure(
                        "The token endpoint did not return an access token.",
                        "missing_access_token",
                        response.StatusCode);
                }

                return OperationResult<TokenSet>.Success(tokens);
            }
        }
        catch (HttpRequestException exception)
        {
            return OperationResult<TokenSet>.Failure(exception.Message, "network_error");
        }
    }

    private static string ReadString(JsonElement element, string property, string? fallback = null) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : fallback ?? string.Empty;

    private static string RandomBase64Url(int byteCount) => Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BuildQuery(IReadOnlyDictionary<string, string?> values) =>
        string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));

    internal static Dictionary<string, string> ParseQuery(string? query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return values;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[Uri.UnescapeDataString(pair[..separator].Replace('+', ' '))] =
                Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return values;
    }

    internal static async Task WriteCallbackResponseAsync(
        HttpListenerResponse response,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        var title = succeeded ? "Login complete" : "Invalid callback";
        var message = succeeded
            ? "You can close this window and return to GptPlusManager."
            : "The callback was invalid. Return to GptPlusManager and try again.";
        var bytes = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{title}</title></head>" +
            $"<body><h1>{title}</h1><p>{message}</p></body></html>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    public void Dispose()
    {
        foreach (var gate in _accountGates.Values)
        {
            gate.Dispose();
        }
    }
}
