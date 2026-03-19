using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Terraria.ModLoader;

namespace TerraVision.Core.VideoPlayer.Danmaku;

/// <summary>
/// A single Bilibili danmaku comment.
/// </summary>
public class DanmakuComment
{
    /// <summary>When this comment appears, in video seconds.</summary>
    public float Time { get; init; }

    /// <summary>The comment text.</summary>
    public string Text { get; init; }

    /// <summary>Color as XNA-compatible packed int (ARGB).</summary>
    public int Color { get; init; }

    /// <summary>Font size as reported by Bilibili (usually 25).</summary>
    public int FontSize { get; init; }

    /// <summary>
    /// Scroll type.
    /// 1 = scrolling right-to-left (most common)
    /// 4 = bottom anchored
    /// 5 = top anchored
    /// </summary>
    public int Type { get; init; }

    public int Lane { get; set; } = -1; // assigned by DanmakuRenderer.LoadComments
}

/// <summary>
/// Fetches and parses Bilibili danmaku for a given video URL.
/// </summary>
public static class DanmakuFetcher
{
    private static readonly HttpClient _http;

    static DanmakuFetcher()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                   | System.Net.DecompressionMethods.Deflate,
            // tModLoader's embedded .NET runtime may have a stale certificate store.
            // We only connect to known Bilibili API endpoints so this is safe.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Fetches danmaku comments for a Bilibili video URL.
    /// Returns an empty list (not null) on any failure.
    /// </summary>
    public static async Task<List<DanmakuComment>> FetchAsync(string bilibiliUrl, CancellationToken token = default)
    {
        try
        {
            string bvid = ExtractBvid(bilibiliUrl);
            if (bvid == null)
            {
                TerraVision.instance.Logger.Warn($"Could not extract BV ID from URL: {bilibiliUrl}");
                return new List<DanmakuComment>();
            }

            TerraVision.instance.Logger.Info($"Fetching danmaku for {bvid}...");

            long cid = await ResolveCidAsync(bvid, token);
            if (cid <= 0)
            {
                TerraVision.instance.Logger.Warn($"Could not resolve CID for {bvid}");
                return new List<DanmakuComment>();
            }

            TerraVision.instance.Logger.Info($"Resolved CID {cid} for {bvid}, fetching comments...");

            List<DanmakuComment> comments = await FetchCommentsAsync(cid, token);
            TerraVision.instance.Logger.Info($"Fetched {comments.Count} danmaku comments for {bvid}");
            return comments;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Danmaku fetch failed: {ex.Message}");
            return new List<DanmakuComment>();
        }
    }

    /// <summary>
    /// Extracts the BV ID from a Bilibili URL.
    /// Handles formats like /video/BV1xx... and /video/av123...
    /// </summary>
    private static string ExtractBvid(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            url, @"(BV[\w]{10}|av\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Calls the Bilibili web API to resolve a BV/AV ID to its CID.
    /// CID is the internal content ID used for danmaku and stream lookups.
    /// </summary>
    private static async Task<long> ResolveCidAsync(string bvid, CancellationToken token)
    {
        // The view API works for both BV and AV IDs
        bool isAv = bvid.StartsWith("av", StringComparison.OrdinalIgnoreCase);
        string apiUrl = isAv
            ? $"https://api.bilibili.com/x/web-interface/view?aid={bvid[2..]}"
            : $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}";

        string json = await _http.GetStringAsync(apiUrl, token);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check API response code — 0 means success
        if (root.GetProperty("code").GetInt32() != 0)
        {
            string message = root.TryGetProperty("message", out var msg)
                ? msg.GetString() : "unknown error";
            TerraVision.instance.Logger.Warn($"Bilibili API returned error: {message}");
            return -1;
        }

        // data.cid is the CID for the first part (page 1) of the video
        return root
            .GetProperty("data")
            .GetProperty("cid")
            .GetInt64();
    }

    /// <summary>
    /// Fetches the raw danmaku XML from Bilibili's comment endpoint and parses it.
    /// </summary>
    private static async Task<List<DanmakuComment>> FetchCommentsAsync(long cid, CancellationToken token)
    {
        string xmlUrl = $"https://comment.bilibili.com/{cid}.xml";
        string xml = await _http.GetStringAsync(xmlUrl, token);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            // Log the first 200 chars so we can see what format was actually returned
            string preview = xml.Length > 200 ? xml[..200] : xml;
            TerraVision.instance.Logger.Error($"Failed to parse danmaku XML: {ex.Message}. Response preview: {preview}");
            return [];
        }

        var comments = new List<DanmakuComment>();

        foreach (var element in doc.Descendants("d"))
        {
            // The "p" attribute is a comma-separated list:
            // time, type, fontSize, color, unixTimestamp, pool, userHash, dmid
            string p = element.Attribute("p")?.Value;
            string text = element.Value;

            if (string.IsNullOrWhiteSpace(p) || string.IsNullOrWhiteSpace(text))
                continue;

            string[] parts = p.Split(',');
            if (parts.Length < 4)
                continue;

            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float time))
                continue;

            int.TryParse(parts[1], out int type);
            int.TryParse(parts[2], out int fontSize);
            int.TryParse(parts[3], out int color);

            // Only include types we can render for now (scrolling, top, bottom)
            if (type != 1 && type != 4 && type != 5)
                continue;

            comments.Add(new DanmakuComment
            {
                Time = time,
                Text = text,
                Color = color,
                FontSize = fontSize,
                Type = type,
            });
        }

        // Sort by time so the renderer can binary-search for active comments
        comments.Sort((a, b) => a.Time.CompareTo(b.Time));
        return comments;
    }
}