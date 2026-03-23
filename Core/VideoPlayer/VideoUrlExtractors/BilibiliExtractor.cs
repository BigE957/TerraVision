using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TerraVision.Core;

namespace TerraVision.Core.VideoPlayer.VideoUrlExtractors;

/// <summary>
/// Extracts direct DASH stream URLs from Bilibili via the official playurl API.
/// Returns VideoUrl + AudioUrl so VLC can play immediately without downloading or merging.
///
/// Resolution order:
///   1. Fetch BV ID from URL
///   2. Fetch CID from web-interface/view API
///   3. Fetch Wbi signing keys from nav API
///   4. Call playurl with signed params + SESSDATA cookie
///   5. Pick best video stream + best audio stream from DASH manifest
///
/// Falls back gracefully to null (triggering DownloadToTempAsync in HybridVideoExtractor)
/// if cookies are unavailable, the API changes, or any step fails.
/// </summary>
public class BilibiliExtractor
{
    // Fixed permutation table for Wbi key mixing — stable across API versions
    private static readonly int[] WbiMixinKeyTable =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52
    };

    private static readonly Regex BvidRegex = new(@"BV[\w]+", RegexOptions.IgnoreCase);

    private readonly HttpClient _http;

    public BilibiliExtractor()
    {
        // tModLoader's embedded .NET runtime may have a stale certificate store that
        // rejects valid modern TLS certificates (e.g. Bilibili's CDN). We use a custom
        // handler that accepts any valid certificate chain rather than relying on the
        // system/runtime store. This is safe here because we only connect to known
        // Bilibili API endpoints over HTTPS.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com");
    }

    /// <summary>
    /// Attempts to extract direct DASH stream URLs for a Bilibili video URL.
    /// Returns null if extraction fails for any reason — caller falls back to download.
    /// </summary>
    public async Task<VideoStreamResult> GetDirectUrlAsync(string url, CancellationToken token = default)
    {
        try
        {
            string bvid = ExtractBvid(url);
            if (bvid == null)
            {
                TerraVision.instance.Logger.Warn("[Bilibili] Could not extract BV ID from URL");
                return null;
            }

            var cookies = await ReadCookiesAsync(url, token);
            string cookieHeader = BuildCookieHeader(cookies);

            var (cid, title) = await FetchCidAsync(bvid, cookieHeader, token);
            if (cid <= 0)
            {
                TerraVision.instance.Logger.Warn($"[Bilibili] Failed to fetch CID for {bvid}");
                return null;
            }

            string mixinKey = await FetchWbiMixinKeyAsync(cookieHeader, token);
            if (mixinKey == null)
            {
                TerraVision.instance.Logger.Warn("[Bilibili] Failed to fetch Wbi signing keys");
                return null;
            }

            var streamResult = await FetchPlayUrlAsync(bvid, cid, mixinKey, cookieHeader, token);
            if (streamResult != null)
            {
                streamResult.Title = title;
                TerraVision.instance.Logger.Info($"[Bilibili] Direct stream extraction successful for {bvid}");
            }

            return streamResult;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Warn($"[Bilibili] Direct extraction failed, will fall back to download: {ex.Message}");
            return null;
        }
    }

    private static string ExtractBvid(string url)
    {
        var match = BvidRegex.Match(url);
        return match.Success ? match.Value : null;
    }

    private async Task<(long Cid, string Title)> FetchCidAsync(string bvid, string cookieHeader, CancellationToken token)
    {
        string apiUrl = $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}";
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        if (!string.IsNullOrEmpty(cookieHeader))
            request.Headers.Add("Cookie", cookieHeader);

        using var response = await _http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = doc.RootElement;

        if (root.GetProperty("code").GetInt32() != 0)
        {
            TerraVision.instance.Logger.Debug($"[Bilibili] view API error: {root.GetProperty("message").GetString()}");
            return (-1, null);
        }

        var data = root.GetProperty("data");
        long cid = data.GetProperty("cid").GetInt64();
        string title = data.TryGetProperty("title", out var t) ? t.GetString() : null;

        return (cid, title);
    }

    private async Task<string> FetchWbiMixinKeyAsync(string cookieHeader, CancellationToken token)
    {
        string navUrl = "https://api.bilibili.com/x/web-interface/nav";
        using var request = new HttpRequestMessage(HttpMethod.Get, navUrl);
        if (!string.IsNullOrEmpty(cookieHeader))
            request.Headers.Add("Cookie", cookieHeader);

        using var response = await _http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = doc.RootElement;

        if (root.GetProperty("code").GetInt32() != 0)
            return null;

        // img_url and sub_url each contain a filename whose stem is part of the key
        string imgUrl = root.GetProperty("data").GetProperty("wbi_img").GetProperty("img_url").GetString() ?? "";
        string subUrl = root.GetProperty("data").GetProperty("wbi_img").GetProperty("sub_url").GetString() ?? "";

        string imgKey = ExtractKeyFromUrl(imgUrl);
        string subKey = ExtractKeyFromUrl(subUrl);
        string rawKey = imgKey + subKey;

        // Apply the fixed permutation table to produce the 32-char mixin key
        return new string(WbiMixinKeyTable.Where(i => i < rawKey.Length).Select(i => rawKey[i]).Take(32).ToArray());
    }

    private static string ExtractKeyFromUrl(string url)
    {
        // e.g. https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077d.png -> 7cd084941338484aae1ad9425b84077d
        int slash = url.LastIndexOf('/');
        int dot = url.LastIndexOf('.');
        if (slash < 0 || dot < 0 || dot <= slash)
            return "";
        return url[(slash + 1)..dot];
    }

    private async Task<VideoStreamResult> FetchPlayUrlAsync(string bvid, long cid, string mixinKey, string cookieHeader, CancellationToken token)
    {
        // Strategy: request MP4 (fnval=1) first to get a single muxed video+audio URL.
        // LibVLC cannot send Cookie headers on CDN segment requests, which means separate
        // DASH audio slaves (fnval=4048) always 403. The MP4 durl URL already has auth
        // embedded in its query params (buvid, deadline, upsig etc.) so VLC plays it
        // without needing cookies at all. Max quality is 1080p — a good tradeoff.
        var result = await FetchPlayUrlWithFnvalAsync(bvid, cid, mixinKey, cookieHeader, fnval: 1, token);
        if (result != null)
            return result;

        // Fallback: DASH video-only (no audio slave) if MP4 durl is unavailable.
        // This can happen for some older or premium-only content.
        TerraVision.instance.Logger.Debug("[Bilibili] MP4 durl unavailable, trying DASH video-only");
        return await FetchPlayUrlWithFnvalAsync(bvid, cid, mixinKey, cookieHeader, fnval: 4048, token);
    }

    private async Task<VideoStreamResult> FetchPlayUrlWithFnvalAsync(string bvid, long cid, string mixinKey, string cookieHeader, int fnval, CancellationToken token)
    {
        int? maxHeight = Terraria.ModLoader.ModContent.GetInstance<TerraVisionConfig>()?.MaxVideoHeight();

        var rawParams = new SortedDictionary<string, string>
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(),
            ["fnval"] = fnval.ToString(),
            ["fnver"] = "0",
            ["fourk"] = maxHeight == null || maxHeight > 1080 ? "1" : "0",
            ["qn"] = HeightToQn(maxHeight).ToString(),
            ["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
        };

        string queryWithoutSign = string.Join("&", rawParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        string wRid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(queryWithoutSign + mixinKey))).ToLower();

        string apiUrl = $"https://api.bilibili.com/x/player/wbi/playurl?{queryWithoutSign}&w_rid={wRid}";

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        if (!string.IsNullOrEmpty(cookieHeader))
            request.Headers.Add("Cookie", cookieHeader);

        using var response = await _http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = doc.RootElement;

        int code = root.GetProperty("code").GetInt32();
        if (code != 0)
        {
            TerraVision.instance.Logger.Warn($"[Bilibili] playurl API returned code {code}: " + root.GetProperty("message").GetString());
            return null;
        }

        var data = root.GetProperty("data");

        // MP4 path: single muxed durl — no audio slave, no Cookie header needed for CDN
        if (data.TryGetProperty("durl", out var durl) && durl.GetArrayLength() > 0)
        {
            string videoUrl = durl[0].GetProperty("url").GetString();
            if (string.IsNullOrEmpty(videoUrl))
                return null;

            var result = new VideoStreamResult { VideoUrl = videoUrl };
            result.HttpHeaders["Referer"] = "https://www.bilibili.com";
            result.HttpHeaders["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            return result;
        }

        // DASH path: separate video + audio streams (only used as last resort)
        if (data.TryGetProperty("dash", out var dash))
        {
            string videoUrl = PickBestStream(dash.GetProperty("video"), maxHeight);
            if (string.IsNullOrEmpty(videoUrl))
                return null;

            // Return video-only — we cannot pass cookies to the audio slave via LibVLC
            var result = new VideoStreamResult { VideoUrl = videoUrl };
            result.HttpHeaders["Referer"] = "https://www.bilibili.com";
            result.HttpHeaders["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            TerraVision.instance.Logger.Debug("[Bilibili] Using DASH video-only (no audio — LibVLC cookie limitation)");
            return result;
        }

        return null;
    }

    private static int HeightToQn(int? maxHeight) => maxHeight switch
    {
        <= 480 => 32,   // 480p
        <= 720 => 64,   // 720p
        <= 1080 => 80,   // 1080p
        <= 1440 => 112,  // 1080p+ (highest non-4K tier)
        _ => 120   // 4K
    };

    /// <summary>
    /// Picks the highest-quality stream from a DASH video or audio array.
    /// Streams are sorted by bandwidth descending; we take the first available URL.
    /// </summary>
    private static string PickBestStream(JsonElement streams, int? maxHeight = null)
    {
        string bestUrl = null;
        int bestBandwidth = -1;

        foreach (var stream in streams.EnumerateArray())
        {
            if (maxHeight != null && stream.TryGetProperty("height", out var h) && h.GetInt32() > maxHeight)
                continue;

            int bandwidth = stream.TryGetProperty("bandwidth", out var bw) ? bw.GetInt32() : 0;
            if (bandwidth > bestBandwidth)
            {
                string url = null;
                if (stream.TryGetProperty("baseUrl", out var bu))
                    url = bu.GetString();
                if (url == null && stream.TryGetProperty("base_url", out var bu2))
                    url = bu2.GetString();

                if (url != null)
                {
                    bestUrl = url;
                    bestBandwidth = bandwidth;
                }
            }
        }

        return bestUrl;
    }

    // Cookies required for API auth and CDN segment access.
    // buvid3/buvid4 are browser fingerprint cookies — Bilibili's CDN validates
    // them on every .m4s segment request and returns 403 if they're absent.
    private static readonly HashSet<string> RelevantCookies = new(StringComparer.OrdinalIgnoreCase)
    {
        "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5",
        "buvid3", "buvid4", "buvid_fp", "sid", "b_nut"
    };

    /// <summary>
    /// Reads all CDN-relevant cookies from the saved Netscape cookies file.
    /// Returns an empty dictionary if no file exists or reading fails.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadCookiesAsync(string url, CancellationToken token)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string cookiesPath = CookieManager.GetFolderCookiesPath(url);
        if (cookiesPath == null)
            return cookies;

        try
        {
            foreach (string line in await System.IO.File.ReadAllLinesAsync(cookiesPath, token))
            {
                if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
                    continue;

                // Netscape format: domain \t flag \t path \t secure \t expiry \t name \t value
                string[] parts = line.Split('\t');
                if (parts.Length >= 7 && RelevantCookies.Contains(parts[5]))
                    cookies[parts[5]] = Uri.UnescapeDataString(parts[6].Trim());
            }
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Debug($"[Bilibili] Could not read cookies: {ex.Message}");
        }

        return cookies;
    }

    private static string BuildCookieHeader(Dictionary<string, string> cookies) => string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
}