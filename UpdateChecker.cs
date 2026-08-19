// ============================================================================
// UpdateChecker.cs —— 从 GitHub Releases 检查公开版本更新
// ============================================================================

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MetrikLite;

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    string ReleaseUrl)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;

    public string CurrentVersionText => FormatVersion(CurrentVersion);

    public string LatestVersionText => FormatVersion(LatestVersion);

    private static string FormatVersion(Version version)
        => version.Build >= 0
            ? version.ToString(3)
            : version.ToString(2);
}

public static class UpdateChecker
{
    private const string LatestReleasePage =
        "https://github.com/mors-lee/MetrikLite/releases/latest";

    private static readonly HttpClient Client = CreateClient();

    public static async Task<UpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(LatestReleasePage, cancellationToken);
        var statusCode = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode && (statusCode < 300 || statusCode >= 400))
        {
            response.EnsureSuccessStatusCode();
        }

        var releaseUrl = response.Headers.Location?.ToString();
        var tag = ExtractTag(releaseUrl);
        if (string.IsNullOrWhiteSpace(tag))
        {
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = Regex.Match(
                html,
                "/releases/tag/(?<tag>v?[0-9]+(?:\\.[0-9]+){1,3}(?:-[^\"'/?#]+)?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            tag = match.Success ? match.Groups["tag"].Value : null;
            releaseUrl ??= "https://github.com/mors-lee/MetrikLite/releases/latest";
        }

        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(releaseUrl))
        {
            throw new InvalidOperationException("GitHub Release 页面缺少版本或下载地址。");
        }

        return new UpdateCheckResult(
            GetCurrentVersion(),
            ParseVersion(tag),
            releaseUrl);
    }

    public static void OpenReleasePage(string releaseUrl)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = releaseUrl,
            UseShellExecute = true,
        });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            // releases/latest 会返回 302，Location 中直接包含最新版本标签。
            // 不跟随重定向可以避免下载完整 HTML 页面。
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MetrikLite", GetCurrentVersion().ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string? ExtractTag(string? releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl))
        {
            return null;
        }

        var marker = "/releases/tag/";
        var index = releaseUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? null
            : Uri.UnescapeDataString(releaseUrl[(index + marker.Length)..].Trim('/'));
    }

    private static Version GetCurrentVersion()
        => typeof(UpdateChecker).Assembly.GetName().Version
           ?? Assembly.GetEntryAssembly()?.GetName().Version
           ?? new Version(0, 0, 0);

    private static Version ParseVersion(string tag)
    {
        var value = tag.Trim().TrimStart('v', 'V');
        var prerelease = value.IndexOf('-');
        if (prerelease >= 0)
        {
            value = value[..prerelease];
        }

        if (!Version.TryParse(value, out var version))
        {
            throw new InvalidOperationException($"无法解析 GitHub 版本标签：{tag}");
        }

        return version;
    }
}
