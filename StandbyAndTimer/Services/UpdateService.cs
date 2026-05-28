using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services;

internal sealed class UpdateService : IUpdateService
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/Layellie/StandbyAndTimer/releases/latest";

    // Separate clients: short timeout for the JSON metadata fetch, no timeout for the
    // large binary download (it can legitimately take minutes on slow connections).
    private static readonly HttpClient _meta     = CreateMetaClient();
    private static readonly HttpClient _download = CreateDownloadClient();

    private static HttpClient CreateMetaClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StandbyAndTimer", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    private static HttpClient CreateDownloadClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StandbyAndTimer", "1.0"));
        return c;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _meta.GetAsync(ReleasesUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return UpdateCheckResult.None();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            string? tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            string? url = doc.RootElement.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return UpdateCheckResult.None();

            var latest  = ParseVersion(tag);
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
            bool newer  = latest > current;

            string assetUrl = newer ? FindSetupAssetUrl(doc.RootElement) : string.Empty;

            Logger.Info($"UpdateCheck: current={current} latest={latest} newer={newer} asset={(assetUrl.Length > 0)}");
            return new UpdateCheckResult(newer, tag!, url ?? string.Empty, assetUrl);
        }
        catch (Exception ex)
        {
            Logger.Warn($"UpdateCheck failed: {ex.Message}");
            return UpdateCheckResult.None();
        }
    }

    public async Task DownloadAsync(
        string             assetUrl,
        string             targetPath,
        IProgress<double>? progress = null,
        CancellationToken  ct       = default)
    {
        using var resp = await _download
            .GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var  buffer    = new byte[81920];
        long downloaded = 0;
        int  read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            if (total is > 0)
                progress?.Report((double)downloaded / total.Value * 100.0);
        }

        Logger.Info($"Update download finished: {downloaded:N0} bytes -> {targetPath}");
    }

    private static string FindSetupAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var asset in assets.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (asset.TryGetProperty("browser_download_url", out var dlEl))
                return dlEl.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static Version ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0, 0);
    }
}
