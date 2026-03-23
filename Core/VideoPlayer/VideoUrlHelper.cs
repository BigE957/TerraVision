using Microsoft.Xna.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;
using TerraVision.Core.VideoPlayer.VideoUrlExtractors;

namespace TerraVision.Core.VideoPlayer;

public static class VideoUrlHelper
{
    private static readonly ConcurrentDictionary<string, (VideoStreamResult Result, DateTime Timestamp, bool IsLivestream)> _urlCache = new();
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeRequests = new();

    // Per-URL in-flight task deduplication — two requests for the same URL share one
    // extraction task rather than spawning two yt-dlp processes. Requests for different
    // URLs run fully in parallel, eliminating the previous single-semaphore bottleneck
    // that caused the spinner to freeze while any other request was in progress.
    private static readonly ConcurrentDictionary<string, Task<VideoStreamResult>> _inFlightRequests = new();

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LivestreamCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    private static HybridVideoExtractor _extractor;
    private static bool _isInitialized;

    /// <summary>
    /// Initialize the video extractor system. Call this during mod load.
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        TerraVision.instance.Logger.Info("Initializing video URL extraction system...");

        _extractor = new HybridVideoExtractor();
        bool success = await _extractor.InitializeAsync();

        if (!success)
        {
            TerraVision.instance.Logger.Error("Failed to initialize video extraction system!");
        }
        else
        {
            TerraVision.instance.Logger.Info($"Video extraction system ready: {_extractor.GetActiveExtractor()}");
            TerraVision.instance.Logger.Info($"Livestream support: {(_extractor.SupportsLivestreams ? "ENABLED" : "DISABLED")}");
        }

        _isInitialized = success;
    }

    public static bool IsReady => _isInitialized && _extractor != null && _extractor.IsAvailable;
    public static bool SupportsLivestreams => IsReady && _extractor.SupportsLivestreams;

    public static bool IsYouTubePlaylist(string url) => (url.Contains("youtube.com") || url.Contains("youtu.be")) && (url.Contains("list=") || url.Contains("/playlist?"));

    /// <summary>
    /// Returns true for all YouTube URL forms: standard watch, shorts, music, and youtu.be short links.
    /// </summary>
    public static bool IsYouTubeUrl(string url) => url.Contains("youtube.com") || url.Contains("youtu.be");

    public static bool IsSupportedVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        string[] supportedDomains =
        [
            "youtube.com", "youtu.be",
            "twitch.tv", "clips.twitch.tv",
            "vimeo.com",
            "twitter.com", "x.com",
            "tiktok.com",
            "reddit.com",
            "bilibili.com",
        ];

        string lowerUrl = url.ToLower();
        return supportedDomains.Any(domain => lowerUrl.Contains(domain));
    }

    public static string ExtractPlaylistId(string url)
    {
        try
        {
            int listIndex = url.IndexOf("list=");
            if (listIndex == -1)
                return null;

            string afterList = url[(listIndex + 5)..];
            int ampersandIndex = afterList.IndexOf('&');
            return ampersandIndex != -1 ? afterList[..ampersandIndex] : afterList;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Failed to extract playlist ID: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the zero-based video index from a YouTube URL's &amp;index= parameter, or null if absent.
    /// YouTube's index parameter is 1-based and is converted here for direct use with playlist arrays.
    /// </summary>
    public static int? ExtractPlaylistIndex(string url)
    {
        string value = GetQueryParam(url, "index");
        if (value != null && int.TryParse(value, out int idx) && idx > 0)
            return idx - 1;

        return null;
    }

    /// <summary>
    /// Parses a URL and extracts any embedded playback hints without modifying the caller's input.
    /// Returns a <see cref="UrlMetadata"/> containing a clean URL (timestamp params stripped so
    /// yt-dlp isn't confused by them) plus any start time and playlist index that were present.
    ///
    /// Supported timestamp formats across platforms:
    ///   YouTube / Bilibili  — ?t=90, ?t=90s, ?t=1h30m45s, ?start=90 (embed URLs)
    ///   Twitch VODs         — ?t=1h30m45s
    /// </summary>
    public static UrlMetadata ParseUrlMetadata(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new UrlMetadata { CleanUrl = url };

        bool isYouTube = IsYouTubeUrl(url);
        bool isTwitch = url.Contains("twitch.tv");
        bool isBilibili = url.Contains("bilibili.com");

        long? startTimeMs = null;
        int? playlistIndex = null;

        if (isYouTube || isTwitch || isBilibili)
        {
            // ?start= is used by YouTube embed URLs; ?t= covers everything else
            string timeValue = GetQueryParam(url, "t") ?? (isYouTube ? GetQueryParam(url, "start") : null);

            if (timeValue != null)
            {
                long parsed = ParseStartTimeMs(timeValue);
                if (parsed > 0)
                    startTimeMs = parsed;
            }
        }

        if (isYouTube)
            playlistIndex = ExtractPlaylistIndex(url);

        // Strip timestamp params so extractors receive a clean URL; seek is handled after PlaybackReady
        string cleanUrl = startTimeMs.HasValue ? StripQueryParams(url, "t", "start") : url;

        return new UrlMetadata
        {
            CleanUrl = cleanUrl,
            StartTimeMs = startTimeMs,
            PlaylistIndex = playlistIndex
        };
    }

    public static void ClearCache()
    {
        _urlCache.Clear();
        TerraVision.instance.Logger.Info("Video URL cache cleared");
    }

    public static void CleanupCache()
    {
        var keysToRemove = new List<string>();

        foreach (var kvp in _urlCache)
        {
            bool expired = kvp.Value.IsLivestream
                ? DateTime.UtcNow - kvp.Value.Timestamp >= LivestreamCacheDuration
                : !IsCachedResultStillValid(kvp.Value.Result, kvp.Value.Timestamp);

            if (expired)
                keysToRemove.Add(kvp.Key);
        }

        foreach (var key in keysToRemove)
            _urlCache.TryRemove(key, out _);

        if (keysToRemove.Count > 0)
            TerraVision.instance.Logger.Info($"Cleaned up {keysToRemove.Count} expired cache entries");
    }

    /// <summary>
    /// Returns true if a cached stream result is still safe to use.
    /// For URLs containing a Bilibili-style &amp;deadline= parameter, the deadline
    /// is parsed and used directly — this prevents serving a URL whose CDN auth
    /// has expired even if our own cache duration hasn't elapsed yet.
    /// Falls back to CacheDuration for all other URLs.
    /// </summary>
    private static bool IsCachedResultStillValid(VideoStreamResult result, DateTime cachedAt)
    {
        if (result?.VideoUrl != null)
        {
            long deadline = ExtractDeadlineFromUrl(result.VideoUrl);
            if (deadline > 0)
            {
                // Give a 60-second buffer before the CDN deadline to avoid serving
                // a URL that expires mid-playback
                var expiry = DateTimeOffset.FromUnixTimeSeconds(deadline - 60).UtcDateTime;
                return DateTime.UtcNow < expiry;
            }
        }

        return DateTime.UtcNow - cachedAt < CacheDuration;
    }

    /// <summary>
    /// Extracts the &amp;deadline=UNIX_TIMESTAMP parameter from a URL, or returns 0.
    /// </summary>
    private static long ExtractDeadlineFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return 0;

        int idx = url.IndexOf("deadline=", StringComparison.Ordinal);
        if (idx < 0)
            return 0;

        idx += "deadline=".Length;
        int end = url.IndexOf('&', idx);
        string value = end < 0 ? url[idx..] : url[idx..end];

        return long.TryParse(value, out long ts) ? ts : 0;
    }

    /// <summary>
    /// Returns the value of a single query parameter from a URL, or null if not present.
    /// </summary>
    private static string GetQueryParam(string url, string paramName)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        int queryStart = url.IndexOf('?');
        if (queryStart < 0)
            return null;

        string query = url[(queryStart + 1)..];

        // Ignore fragment
        int fragmentStart = query.IndexOf('#');
        if (fragmentStart >= 0)
            query = query[..fragmentStart];

        foreach (string param in query.Split('&'))
        {
            if (string.IsNullOrEmpty(param))
                continue;

            int eq = param.IndexOf('=');
            if (eq < 0)
                continue;

            if (string.Equals(param[..eq], paramName, StringComparison.OrdinalIgnoreCase))
                return param[(eq + 1)..];
        }

        return null;
    }

    /// <summary>
    /// Returns a copy of the URL with the specified query parameters removed.
    /// Preserves all other parameters and any fragment identifier.
    /// </summary>
    private static string StripQueryParams(string url, params string[] paramsToRemove)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        try
        {
            int queryStart = url.IndexOf('?');
            if (queryStart < 0)
                return url;

            string baseUrl = url[..queryStart];
            string query = url[(queryStart + 1)..];

            string fragment = "";
            int fragmentStart = query.IndexOf('#');
            if (fragmentStart >= 0)
            {
                fragment = query[fragmentStart..];
                query = query[..fragmentStart];
            }

            var kept = new List<string>();
            foreach (string param in query.Split('&'))
            {
                if (string.IsNullOrEmpty(param))
                    continue;

                int eq = param.IndexOf('=');
                string key = eq >= 0 ? param[..eq] : param;

                if (!paramsToRemove.Contains(key, StringComparer.OrdinalIgnoreCase))
                    kept.Add(param);
            }

            string newQuery = kept.Count > 0 ? "?" + string.Join("&", kept) : "";
            return baseUrl + newQuery + fragment;
        }
        catch
        {
            return url;
        }
    }

    /// <summary>
    /// Parses a timestamp string into milliseconds.
    ///
    /// Handles all common URL timestamp formats:
    ///   90       — plain seconds
    ///   90s      — seconds with suffix
    ///   30m45s   — minutes and seconds
    ///   1h30m45s — hours, minutes, and seconds (any combination)
    /// </summary>
    private static long ParseStartTimeMs(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        // Plain seconds, possibly with a trailing 's'
        string stripped = value.TrimEnd('s');
        if (long.TryParse(stripped, out long plainSeconds))
            return plainSeconds * 1000;

        // hms format: walk through digit runs followed by a unit character
        long totalSeconds = 0;
        int i = 0;

        while (i < value.Length)
        {
            int numStart = i;
            while (i < value.Length && char.IsDigit(value[i]))
                i++;

            if (i == numStart || i >= value.Length)
                break;

            if (!long.TryParse(value[numStart..i], out long num))
                break;

            totalSeconds += value[i] switch
            {
                'h' => num * 3600,
                'm' => num * 60,
                's' => num,
                _ => 0
            };

            i++;
        }

        return totalSeconds * 1000;
    }

    public static Task<VideoStreamResult> GetDirectUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            TerraVision.instance.Logger.Error("Video extraction system not ready");
            return Task.FromResult<VideoStreamResult>(null);
        }

        // Check cache first — no async needed if the result is already valid
        if (_urlCache.TryGetValue(url, out var cached))
        {
            if (cached.IsLivestream)
            {
                _urlCache.TryRemove(url, out _);
                TerraVision.instance.Logger.Debug("Livestream in cache - fetching fresh URL");
            }
            else if (IsCachedResultStillValid(cached.Result, cached.Timestamp))
            {
                TerraVision.instance.Logger.Debug("Using cached URL for regular video");
                return Task.FromResult(cached.Result);
            }
            else
            {
                _urlCache.TryRemove(url, out _);
                TerraVision.instance.Logger.Debug("Cached URL expired, fetching new one");
            }
        }

        // If a request for this exact URL is already in flight, return the same task
        // rather than spawning a second yt-dlp process for the same URL
        if (_inFlightRequests.TryGetValue(url, out var existingTask))
        {
            TerraVision.instance.Logger.Debug($"Joining in-flight request for {url}");
            return existingTask;
        }

        var task = FetchAndCacheAsync(url, cancellationToken);
        _inFlightRequests[url] = task;

        // Remove from in-flight tracking once the task completes regardless of outcome
        _ = task.ContinueWith(_ => _inFlightRequests.TryRemove(url, out _),
            TaskContinuationOptions.ExecuteSynchronously);

        return task;
    }

    private static async Task<VideoStreamResult> FetchAndCacheAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            bool isDownload = url.Contains("bilibili.com");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(isDownload ? DownloadTimeout : RequestTimeout);

            VideoStreamResult streamResult = await _extractor.GetDirectUrlAsync(url, linkedCts.Token);

            if (streamResult != null && !streamResult.IsLivestream)
            {
                _urlCache[url] = (streamResult, DateTime.UtcNow, false);
                TerraVision.instance.Logger.Debug("Cached URL for regular video");
            }
            else if (streamResult?.IsLivestream == true)
            {
                TerraVision.instance.Logger.Debug("Livestream URL not cached (expires too quickly)");
            }

            return streamResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TerraVision.instance.Logger.Info("URL extraction cancelled by user");
            throw;
        }
        catch (OperationCanceledException)
        {
            TerraVision.instance.Logger.Error("URL extraction timed out");
            return null;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"URL extraction failed: {ex.Message}");
            return null;
        }
    }

    public static async Task<string> SearchYouTubeAsync(string searchQuery, int resultIndex = 0, int maxResults = 10, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            TerraVision.instance.Logger.Error("Video extraction system not ready");
            return null;
        }

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            TerraVision.instance.Logger.Error("Empty search query");
            return null;
        }

        try
        {
            return await _extractor.SearchAsync(searchQuery, resultIndex, maxResults, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TerraVision.instance.Logger.Info("YouTube search cancelled");
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"YouTube search failed: {ex.Message}");
            return null;
        }
    }

    public static async Task<List<string>> GetPlaylistVideosAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            TerraVision.instance.Logger.Error("Video extraction system not ready");
            return [];
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            TerraVision.instance.Logger.Error("Empty playlist ID");
            return [];
        }

        try
        {
            return await _extractor.GetPlaylistVideosAsync(playlistId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TerraVision.instance.Logger.Info("Playlist fetch cancelled");
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Playlist fetch failed: {ex.Message}");
            return [];
        }
    }

    public static async Task<bool> IsLivestreamAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
            return false;

        try
        {
            return await _extractor.IsLivestreamAsync(url, cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    public static void ProcessUrlAsync(string url, Action<VideoStreamResult> onComplete, Guid requestId)
    {
        if (IsSupportedVideoUrl(url))
        {
            string platform = "video";
            if (IsYouTubeUrl(url)) platform = "YouTube";
            else if (url.Contains("twitch.tv")) platform = "Twitch";
            else if (url.Contains("vimeo.com")) platform = "Vimeo";
            else if (url.Contains("tiktok.com")) platform = "TikTok";
            else if (url.Contains("twitter.com") || url.Contains("x.com")) platform = "Twitter";
            else if (url.Contains("reddit.com")) platform = "Reddit";
            else if (url.Contains("bilibili.com")) platform = "Bilibili";

            ShowStatus($"Extracting {platform} stream URL...", Color.LightBlue);

            var cts = new CancellationTokenSource();
            _activeRequests[requestId] = cts;

            Task.Run(async () =>
            {
                try
                {
                    VideoStreamResult result = await GetDirectUrlAsync(url, cts.Token);
                    _activeRequests.TryRemove(requestId, out _);
                    Main.QueueMainThreadAction(() => onComplete?.Invoke(result));
                }
                catch (OperationCanceledException)
                {
                    TerraVision.instance.Logger.Info($"URL request {requestId} cancelled");
                    _activeRequests.TryRemove(requestId, out _);
                }
                catch (Exception ex)
                {
                    TerraVision.instance.Logger.Error($"ProcessUrlAsync failed: {ex.Message}");
                    _activeRequests.TryRemove(requestId, out _);
                    Main.QueueMainThreadAction(() => onComplete?.Invoke(null));
                }
            }, cts.Token);
        }
        else
        {
            // Direct .mp4 / .m3u8 links — wrap in a plain result, no headers needed
            onComplete?.Invoke(new VideoStreamResult { VideoUrl = url });
        }
    }

    public static void ProcessSearchAsync(string searchQuery, Action<VideoStreamResult> onComplete, Guid requestId, int resultIndex = 0, int maxResults = 10)
    {
        ShowStatus("Searching YouTube...", Color.LightBlue);

        var cts = new CancellationTokenSource();
        _activeRequests[requestId] = cts;

        Task.Run(async () =>
        {
            try
            {
                string videoUrl = await SearchYouTubeAsync(searchQuery, resultIndex, maxResults, cts.Token);

                if (videoUrl == null)
                {
                    if (!cts.Token.IsCancellationRequested)
                        Main.QueueMainThreadAction(() => Main.NewText("No search results found!", Color.Orange));

                    _activeRequests.TryRemove(requestId, out _);
                    Main.QueueMainThreadAction(() => onComplete?.Invoke(null));
                    return;
                }

                Main.QueueMainThreadAction(() => ShowStatus("Found video, extracting stream...", Color.LightGreen));

                VideoStreamResult result = await GetDirectUrlAsync(videoUrl, cts.Token);
                _activeRequests.TryRemove(requestId, out _);
                Main.QueueMainThreadAction(() => onComplete?.Invoke(result));
            }
            catch (OperationCanceledException)
            {
                TerraVision.instance.Logger.Info($"Search request {requestId} cancelled");
                _activeRequests.TryRemove(requestId, out _);
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"Search processing failed: {ex.Message}");
                _activeRequests.TryRemove(requestId, out _);
                Main.QueueMainThreadAction(() => onComplete?.Invoke(null));
            }
        }, cts.Token);
    }

    public static void CancelRequest(Guid requestId)
    {
        if (_activeRequests.TryRemove(requestId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private static void ShowStatus(string message, Color color)
    {
        if (ModContent.GetInstance<TerraVisionConfig>()?.ShowLoadingMessages ?? true)
            Main.NewText(message, color);
    }
}

public class VideoUrlHelperSystem : ModSystem
{
    public override void OnModLoad()
    {
        Task.Run(async () =>
        {
            await VideoUrlHelper.InitializeAsync();
        });
    }

    public override void PostUpdateEverything()
    {
        if ((int)(Main.GlobalTimeWrappedHourly * 60) % 36000 == 0)
            VideoUrlHelper.CleanupCache();
    }

    public override void OnModUnload()
    {
        VideoUrlHelper.ClearCache();
    }
}

public class VideoStreamResult
{
    public string Title { get; set; }
    public List<VideoChapter> Chapters { get; set; } = [];

    /// <summary>Primary video (or combined) stream URL.</summary>
    public string VideoUrl { get; set; }

    /// <summary>
    /// Separate audio stream URL, if the site uses DASH (e.g. Bilibili).
    /// Pass to VLC via :input-slave= option.
    /// Null if audio is muxed into VideoUrl.
    /// </summary>
    public string AudioUrl { get; set; }

    /// <summary>
    /// HTTP headers required by the CDN (e.g. Referer for Bilibili).
    /// Pass each one to VLC via :http-header-add= option.
    /// </summary>
    public Dictionary<string, string> HttpHeaders { get; set; } = new();

    /// <summary>
    /// True if this stream is a live broadcast.
    /// Set by HybridVideoExtractor when it detects and routes a livestream,
    /// so callers can make caching decisions without a separate metadata fetch.
    /// </summary>
    public bool IsLivestream { get; set; }
}

/// <summary>
/// Playback hints extracted from a user-supplied URL by <see cref="VideoUrlHelper.ParseUrlMetadata"/>.
/// </summary>
public class UrlMetadata
{
    /// <summary>
    /// The original URL with timestamp and other playback-hint parameters stripped.
    /// Safe to pass directly to yt-dlp and the extractor pipeline.
    /// </summary>
    public string CleanUrl { get; set; }

    /// <summary>
    /// Requested start position in milliseconds, if a timestamp parameter was present in the URL.
    /// Null if no timestamp was found.
    /// Seek to this position after <c>PlaybackReady</c> fires — LibVLC cannot seek before media loads.
    /// </summary>
    public long? StartTimeMs { get; set; }

    /// <summary>
    /// Zero-based playlist video index, if the URL included an &amp;index= parameter.
    /// YouTube's index parameter is 1-based and is converted here for direct use with playlist arrays.
    /// Null if no index was present.
    /// </summary>
    public int? PlaylistIndex { get; set; }
}

public class VideoChapter
{
    public float StartTime { get; set; }  // seconds
    public float EndTime { get; set; }    // seconds
    public string Title { get; set; }
}