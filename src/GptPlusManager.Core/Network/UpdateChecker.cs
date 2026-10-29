using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace GptPlusManager.Core.Network;

/// <summary>检查更新的结果。任何失败都落到 <see cref="Error"/>，绝不抛异常打断界面。</summary>
public sealed record UpdateCheckResult(
    bool IsSuccess,
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    DateTimeOffset? PublishedAt,
    string? Error)
{
    public static UpdateCheckResult Failed(string currentVersion, string error) =>
        new(false, false, currentVersion, null, null, null, error);

    public static UpdateCheckResult UpToDate(string currentVersion, string latestVersion) =>
        new(true, false, currentVersion, latestVersion, null, null, null);
}

/// <summary>
/// 从 GitHub Releases 检查是否有新版本。
///
/// <para>只读 <c>releases/latest</c> 这一个公开端点（无需令牌），拿 tag / 发布页链接 /
/// 发布时间三个字段与当前版本比较。下载交给浏览器——自替换正在运行的 exe
/// 需要额外的重启与文件替换编排，而「打开发布页点下载」可靠得多。</para>
///
/// <para>网络失败不抛异常：国内到 GitHub 的连接时常不稳，报一句清楚的原因
/// 比让界面崩掉重要。</para>
/// </summary>
public sealed class UpdateChecker
{
    public const string ReleasesLatestUrl =
        "https://api.github.com/repos/lcyyun/chatgpt-account-manager/releases/latest";

    private readonly HttpClient _http;

    public UpdateChecker(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API 要求 User-Agent，缺了会 403。
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChatGptAccountManager", "1.0"));
        }
    }

    /// <summary>
    /// 检查更新。<paramref name="currentVersion"/> 传当前版本号（如 "0.7.1" 或 "0.7.1.0"）。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return UpdateCheckResult.Failed(currentVersion, "无法确定当前版本号。");
        }

        JsonNode? root;
        try
        {
            using var response = await _http.GetAsync(ReleasesLatestUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 403 + rate limit 是最常见的：未认证 60 次/小时/出口 IP。
                var hint = (int)response.StatusCode == 403
                    ? "GitHub 限流（未认证接口每小时 60 次），稍后再试。"
                    : $"GitHub 返回 HTTP {(int)response.StatusCode}。";
                return UpdateCheckResult.Failed(currentVersion, hint);
            }

            root = JsonNode.Parse(body);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // 超时/断网/DNS 失败/响应不是 JSON：一句人话，不崩界面。
            return UpdateCheckResult.Failed(currentVersion,
                "无法连接 GitHub，检查网络或代理后重试。（" + exception.Message + "）");
        }

        var tag = root?["tag_name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tag))
        {
            return UpdateCheckResult.Failed(currentVersion, "GitHub 响应里没有版本号（tag_name）。");
        }

        var url = root?["html_url"]?.GetValue<string>();
        DateTimeOffset? published = null;
        if (root?["published_at"]?.GetValue<string>() is { } publishedText &&
            DateTimeOffset.TryParse(publishedText, out var parsed))
        {
            published = parsed;
        }

        var latest = NormalizeVersion(tag);
        if (!TryParseVersion(latest, out var latestParts))
        {
            return UpdateCheckResult.Failed(currentVersion, $"无法解析最新版本号：{tag}");
        }

        var current = NormalizeVersion(currentVersion);
        if (!TryParseVersion(current, out var currentParts))
        {
            return UpdateCheckResult.Failed(currentVersion, $"无法解析当前版本号：{currentVersion}");
        }

        return Compare(currentParts, latestParts) switch
        {
            >= 0 => UpdateCheckResult.UpToDate(current, latest),
            _ => new UpdateCheckResult(true, true, current, latest, url, published, null),
        };
    }

    /// <summary>
    /// 版本号归一化：去掉 v 前缀、去掉构建元数据（0.7.1+abc → 0.7.1）、
    /// 丢弃预发布后缀（只比数字部分）。
    /// </summary>
    public static string NormalizeVersion(string version)
    {
        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        if (dash >= 0) v = v[..dash];
        return v.Trim();
    }

    /// <summary>解析 "x.y.z[.w]" 为定长 4 的数字元组；非数字段一律按 0。</summary>
    public static bool TryParseVersion(string version, out int[] parts)
    {
        parts = new int[4];
        var pieces = NormalizeVersion(version).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0) return false;

        for (var i = 0; i < pieces.Length && i < 4; i++)
        {
            if (!int.TryParse(pieces[i], out parts[i])) return false;
        }
        return true;
    }

    /// <summary>逐段比较（缺省补 0）。返回 &gt;0 表示 a 更新。</summary>
    public static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < 4; i++)
        {
            var diff = a[i] - b[i];
            if (diff != 0) return diff;
        }
        return 0;
    }
}
