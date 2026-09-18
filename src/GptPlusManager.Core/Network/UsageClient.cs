using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GptPlusManager.Core.Models;
using GptPlusManager.Core.Persistence;
using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Network;

public sealed class UsageClient
{
    private static readonly string[] SubscriptionDateKeys =
    [
        "chatgpt_subscription_active_until",
        "subscription_active_until",
        "subscription_expires_at",
        "expires_at",
        "active_until",
        "subscription_until",
        "renewal_date"
    ];

    private readonly HttpClient _httpClient;
    private readonly AuthClient _authClient;

    public UsageClient(HttpClient httpClient, AuthClient authClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
    }

    public async Task<OperationResult<UsageSnapshot>> GetUsageAsync(
        AccountRecord account,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetUsageCoreAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return OperationResult<UsageSnapshot>.Failure(exception.Message, "network_error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationResult<UsageSnapshot>.Failure("The usage request timed out.", "request_timeout");
        }
    }

    private async Task<OperationResult<UsageSnapshot>> GetUsageCoreAsync(
        AccountRecord account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var tokenResult = await _authClient.GetValidTokensAsync(account, false, cancellationToken)
            .ConfigureAwait(false);
        if (!tokenResult.Succeeded)
        {
            return OperationResult<UsageSnapshot>.Failure(
                tokenResult.Message,
                tokenResult.ErrorCode,
                tokenResult.StatusCode);
        }

        var tokens = tokenResult.Value!;
        var response = await SendAuthorizedAsync(
            HttpMethod.Get,
            OpenAiEndpoints.UsageApiUrl,
            tokens,
            null,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            var refreshResult = await _authClient.GetValidTokensAsync(account, true, cancellationToken)
                .ConfigureAwait(false);
            if (!refreshResult.Succeeded)
            {
                return OperationResult<UsageSnapshot>.Failure(
                    refreshResult.Message,
                    refreshResult.ErrorCode,
                    refreshResult.StatusCode);
            }

            tokens = refreshResult.Value!;
            response = await SendAuthorizedAsync(
                HttpMethod.Get,
                OpenAiEndpoints.UsageApiUrl,
                tokens,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OperationResult<UsageSnapshot>.Failure(
                    $"Usage request failed with HTTP {(int)response.StatusCode}.",
                    "usage_request_failed",
                    response.StatusCode);
            }

            var parsed = ParseUsage(body, DateTimeOffset.UtcNow);
            if (!parsed.Succeeded)
            {
                return parsed;
            }

            var subscription = await GetSubscriptionUntilAsync(tokens, cancellationToken).ConfigureAwait(false);
            var snapshot = parsed.Value! with
            {
                SubscriptionUntil = subscription.Succeeded ? subscription.Value : null
            };
            return OperationResult<UsageSnapshot>.Success(snapshot, snapshot.Summary);
        }
    }

    public OperationResult<UsageSnapshot> ParseUsage(string json, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return OperationResult<UsageSnapshot>.Failure("Usage response was empty.", "empty_usage_response");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var primaryElement = FindPropertyRecursive(root, "primary_window");
            var secondaryElement = FindPropertyRecursive(root, "secondary_window");
            if (!primaryElement.HasValue && !secondaryElement.HasValue)
            {
                return OperationResult<UsageSnapshot>.Failure(
                    "No recognized usage windows were returned.",
                    "unrecognized_usage_response");
            }

            var checkedAt = now ?? DateTimeOffset.UtcNow;
            var primary = primaryElement.HasValue
                ? ParseWindow(primaryElement.Value, checkedAt)
                : null;
            var secondary = secondaryElement.HasValue
                ? ParseWindow(secondaryElement.Value, checkedAt)
                : null;
            var plan = GetText(FindPropertyRecursive(root, "plan_type")) ?? string.Empty;
            var creditsElement = FindPropertyRecursive(root, "rate_limit_reset_credits");
            var credits = creditsElement.HasValue
                ? Math.Max(0, GetInt(FindPropertyRecursive(creditsElement.Value, "available_count")) ?? 0)
                : 0;
            var summaryParts = new List<string>();
            if (!string.IsNullOrEmpty(plan))
            {
                summaryParts.Add($"套餐:{plan}");
            }

            if (primary is not null)
            {
                summaryParts.Add(primary.Description);
            }

            if (secondary is not null)
            {
                summaryParts.Add(secondary.Description);
            }

            var snapshot = new UsageSnapshot(
                plan,
                primary,
                secondary,
                credits,
                string.Join(" · ", summaryParts),
                checkedAt,
                null);
            return OperationResult<UsageSnapshot>.Success(snapshot, snapshot.Summary);
        }
        catch (JsonException exception)
        {
            return OperationResult<UsageSnapshot>.Failure(exception.Message, "invalid_usage_json");
        }
    }

    public async Task<OperationResult<DateTimeOffset?>> GetSubscriptionUntilAsync(
        TokenSet tokens,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetSubscriptionUntilCoreAsync(tokens, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return OperationResult<DateTimeOffset?>.Failure(exception.Message, "network_error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationResult<DateTimeOffset?>.Failure("The account check timed out.", "request_timeout");
        }
    }

    private async Task<OperationResult<DateTimeOffset?>> GetSubscriptionUntilCoreAsync(
        TokenSet tokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var fromClaims = FindSubscriptionDateInClaims(tokens.IdToken);
        if (fromClaims.HasValue)
        {
            return OperationResult<DateTimeOffset?>.Success(fromClaims);
        }

        using var response = await SendAuthorizedAsync(
            HttpMethod.Get,
            OpenAiEndpoints.AccountCheckUrl,
            tokens,
            null,
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return OperationResult<DateTimeOffset?>.Failure(
                $"Account check failed with HTTP {(int)response.StatusCode}.",
                "account_check_failed",
                response.StatusCode);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return OperationResult<DateTimeOffset?>.Success(FindSubscriptionDate(document.RootElement));
        }
        catch (JsonException exception)
        {
            return OperationResult<DateTimeOffset?>.Failure(exception.Message, "invalid_account_check_json");
        }
    }

    public async Task<OperationResult<ResetCreditResult>> ConsumeResetCreditAsync(
        AccountRecord account,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ConsumeResetCreditCoreAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return OperationResult<ResetCreditResult>.Failure(exception.Message, "network_error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationResult<ResetCreditResult>.Failure("The reset-credit request timed out.", "request_timeout");
        }
    }

    private async Task<OperationResult<ResetCreditResult>> ConsumeResetCreditCoreAsync(
        AccountRecord account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var tokenResult = await _authClient.GetValidTokensAsync(account, false, cancellationToken)
            .ConfigureAwait(false);
        if (!tokenResult.Succeeded)
        {
            return OperationResult<ResetCreditResult>.Failure(
                tokenResult.Message,
                tokenResult.ErrorCode,
                tokenResult.StatusCode);
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tokens = tokenResult.Value!;
        var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            OpenAiEndpoints.ResetCreditConsumeUrl,
            tokens,
            JsonSerializer.Serialize(new { redeem_request_id = requestId }, JsonDefaults.Compact),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            var refreshed = await _authClient.GetValidTokensAsync(account, true, cancellationToken)
                .ConfigureAwait(false);
            if (!refreshed.Succeeded)
            {
                return OperationResult<ResetCreditResult>.Failure(
                    refreshed.Message,
                    refreshed.ErrorCode,
                    refreshed.StatusCode);
            }

            tokens = refreshed.Value!;
            response = await SendAuthorizedAsync(
                HttpMethod.Post,
                OpenAiEndpoints.ResetCreditConsumeUrl,
                tokens,
                JsonSerializer.Serialize(new { redeem_request_id = requestId }, JsonDefaults.Compact),
                cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var code = GetText(FindPropertyRecursive(root, "code")) ?? string.Empty;
                var windowsReset = GetInt(FindPropertyRecursive(root, "windows_reset")) ?? 0;
                var available = code == "reset"
                    ? Math.Max(0, account.ResetCreditsAvailable - 1)
                    : code == "no_credit" ? 0 : account.ResetCreditsAvailable;
                var result = new ResetCreditResult(code, windowsReset, available);
                return response.IsSuccessStatusCode && code == "reset"
                    ? OperationResult<ResetCreditResult>.Success(result, "Reset credit consumed.")
                    : OperationResult<ResetCreditResult>.Failure(
                        result,
                        string.IsNullOrEmpty(code) ? "Unknown reset-credit response." : code,
                        string.IsNullOrEmpty(code) ? "unknown_reset_response" : code,
                        response.StatusCode);
            }
            catch (JsonException exception)
            {
                return OperationResult<ResetCreditResult>.Failure(
                    exception.Message,
                    "invalid_reset_response",
                    response.StatusCode);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string url,
        TokenSet tokens,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.UserAgent.ParseAdd(OpenAiEndpoints.UserAgent);
        request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        // 账号 ID 嵌套在 id_token 的 https://api.openai.com/auth 命名空间里；
        // 优先用令牌自带的，回退到 claim 解析。
        var accountId = !string.IsNullOrWhiteSpace(tokens.AccountId)
            ? tokens.AccountId
            : JwtUtility.GetChatGptAccountId(tokens.IdToken);
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static UsageWindow ParseWindow(JsonElement window, DateTimeOffset now)
    {
        var usedPercent = GetDouble(FindPropertyRecursive(window, "used_percent")) ?? -1;
        var seconds = GetDouble(FindPropertyRecursive(window, "limit_window_seconds")) ?? 0;
        var resetAfter = GetDouble(FindPropertyRecursive(window, "reset_after_seconds")) ?? 0;
        var resetAt = GetDouble(FindPropertyRecursive(window, "reset_at")) ?? 0;
        if (resetAfter > 0)
        {
            resetAt = now.ToUnixTimeSeconds() + resetAfter;
        }
        else if (resetAt < 1_000_000_000)
        {
            resetAt = 0;
        }

        var description = $"{HumanDuration(seconds)}窗口已用 {Math.Round(usedPercent)}%";
        return new UsageWindow(usedPercent, description, resetAt, seconds);
    }

    private static string HumanDuration(double seconds)
    {
        if (seconds < 90) return $"{Math.Round(seconds)}秒";
        if (seconds < 5_400) return $"{Math.Round(seconds / 60)}分钟";
        if (seconds < 172_800) return $"{Math.Round(seconds / 3_600)}小时";
        if (Math.Abs(seconds - 604_800) < 3_600) return "每周";
        if (Math.Abs(seconds - 2_592_000) < 172_800) return "每月";
        return $"{Math.Round(seconds / 86_400)}天";
    }

    private static DateTimeOffset? FindSubscriptionDateInClaims(string jwt)
    {
        foreach (var value in JwtUtility.GetClaims(jwt).Values)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindSubscriptionDate(value);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        var claims = JwtUtility.GetClaims(jwt);
        foreach (var key in SubscriptionDateKeys)
        {
            if (claims.TryGetValue(key, out var value))
            {
                var parsed = ParseDate(value);
                if (parsed.HasValue)
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? FindSubscriptionDate(JsonElement root)
    {
        foreach (var key in SubscriptionDateKeys)
        {
            var value = FindPropertyRecursive(root, key);
            if (value.HasValue)
            {
                var parsed = ParseDate(value.Value);
                if (parsed.HasValue && parsed.Value.Year > 2020)
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseDate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            if (number > 1e12) return DateTimeOffset.FromUnixTimeMilliseconds((long)number);
            if (number > 1e9) return DateTimeOffset.FromUnixTimeSeconds((long)number);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericText))
            {
                if (numericText > 1e12) return DateTimeOffset.FromUnixTimeMilliseconds((long)numericText);
                if (numericText > 1e9) return DateTimeOffset.FromUnixTimeSeconds((long)numericText);
            }

            if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static JsonElement? FindPropertyRecursive(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }

                var nested = FindPropertyRecursive(property.Value, name);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindPropertyRecursive(item, name);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? GetText(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } item => item.GetString(),
        { ValueKind: JsonValueKind.Number } item => item.ToString(),
        _ => null
    };

    private static double? GetDouble(JsonElement? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var item = value.Value;
        if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number))
        {
            return number;
        }

        return item.ValueKind == JsonValueKind.String
            && double.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static int? GetInt(JsonElement? value)
    {
        var number = GetDouble(value);
        return number.HasValue ? (int)number.Value : null;
    }
}
