using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services;

internal sealed partial class UpdateService : IUpdateService
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/Layellie/StandbyAndTimer/releases/latest";

    // Matches "SHA256: abcdef..." or "sha-256 = abcdef..." in release body text.
    [GeneratedRegex(@"\bSHA[-\s]?256\b\s*[:=]?\s*([0-9a-fA-F]{64})", RegexOptions.IgnoreCase)]
    private static partial Regex Sha256Pattern();

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
            string sha256   = newer ? await ResolveSha256Async(doc.RootElement, ct).ConfigureAwait(false) : string.Empty;

            Logger.Info($"UpdateCheck: current={current} latest={latest} newer={newer} " +
                        $"asset={(assetUrl.Length > 0)} sha256={(sha256.Length > 0 ? "yes" : "no")}");
            return new UpdateCheckResult(newer, tag!, url ?? string.Empty, assetUrl, sha256);
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
        string             expectedSha256 = "",
        IProgress<double>? progress       = null,
        CancellationToken  ct             = default)
    {
        using var resp = await _download
            .GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        using var sha = SHA256.Create();
        long downloaded = 0;

        // Stream into the destination file while feeding the same bytes into
        // the hash transform — no second read pass needed.
        await using (var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.TransformBlock(buffer, 0, read, null, 0);
                downloaded += read;
                if (total is > 0)
                    progress?.Report((double)downloaded / total.Value * 100.0);
            }
            sha.TransformFinalBlock([], 0, 0);
        }

        string actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string expected = expectedSha256.Trim().ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                try { File.Delete(targetPath); } catch { /* best-effort cleanup */ }
                Logger.Error($"Update SHA256 mismatch — expected {expected}, got {actual}. File deleted.");
                throw new InvalidDataException(
                    $"Downloaded installer failed integrity check (SHA-256 mismatch).");
            }
            Logger.Info($"Update SHA256 verified: {actual}");
        }
        else
        {
            Logger.Warn($"Update SHA256 not published by release — installer hash is {actual}. " +
                        $"Skipping integrity verification.");
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

    // Resolution order:
    //   1. A "*.exe.sha256" or "SHA256SUMS" asset on the release (preferred).
    //   2. A "SHA256: <hex>" line inside the release body.
    private static async Task<string> ResolveSha256Async(JsonElement root, CancellationToken ct)
    {
        string fromAsset = await TryFetchSha256AssetAsync(root, ct).ConfigureAwait(false);
        if (fromAsset.Length == 64) return fromAsset;

        if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String)
        {
            string body = bodyEl.GetString() ?? string.Empty;
            var match = Sha256Pattern().Match(body);
            if (match.Success) return match.Groups[1].Value.ToLowerInvariant();
        }
        return string.Empty;
    }

    private static async Task<string> TryFetchSha256AssetAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var asset in assets.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (name is null) continue;

            bool looksLikeHashFile =
                name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeHashFile) continue;

            if (!asset.TryGetProperty("browser_download_url", out var dlEl)) continue;
            string url = dlEl.GetString() ?? string.Empty;
            if (url.Length == 0) continue;

            try
            {
                string text = await _meta.GetStringAsync(url, ct).ConfigureAwait(false);
                var match = Sha256Pattern().Match(text);
                if (match.Success) return match.Groups[1].Value.ToLowerInvariant();

                // Fallback: a bare hex line (sha256sum-style "<hash>  <filename>").
                var bare = Regex.Match(text, @"^[0-9a-fA-F]{64}\b", RegexOptions.Multiline);
                if (bare.Success) return bare.Value.ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to fetch hash asset '{name}': {ex.Message}");
            }
        }
        return string.Empty;
    }

    private static Version ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V').Trim();
        if (!Version.TryParse(s, out var v))
            return new Version(0, 0, 0, 0);
        // A 3-part tag (e.g. "v2.0.2") parses to Version(2,0,2) with
        // Revision = -1. Assembly versions always have 4 parts
        // (Version(2,0,2,0)), and Version comparison treats -1 < 0, so the
        // tag would compare as *less* than its own assembly even when both
        // are nominally "2.0.2". Normalise undefined components to 0 so the
        // comparison reflects real version equality.
        return new Version(
            v.Major,
            v.Minor,
            Math.Max(v.Build,    0),
            Math.Max(v.Revision, 0));
    }
}
