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
using YoutubeExplode.Common;

namespace TerraVision.Core.VideoPlayer;

public static class VideoUrlHelper
{
    private static readonly ConcurrentDictionary<string, (VideoStreamResult Result, DateTime Timestamp, bool IsLivestream)> _urlCache = new();
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeRequests = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LivestreamCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim _requestLock = new(1, 1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SemaphoreTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    private static HybridVideoExtractor _extractor;
    private static bool _isInitialized = false;

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
            TerraVision.instance.Logger.Info($"Video extraction system ready: {(_extractor as HybridVideoExtractor)?.GetActiveExtractor()}");
            TerraVision.instance.Logger.Info($"Livestream support: {(_extractor.SupportsLivestreams ? "ENABLED" : "DISABLED")}");
        }

        _isInitialized = success;
    }

    public static bool IsReady => _isInitialized && _extractor != null && _extractor.IsAvailable;
    public static bool SupportsLivestreams => IsReady && _extractor.SupportsLivestreams;

    public static bool IsYouTubePlaylist(string url)
    {
        return (url.Contains("youtube.com") || url.Contains("youtu.be")) &&
               (url.Contains("list=") || url.Contains("/playlist?"));
    }

    public static bool IsYouTubeUrl(string url) => url.Contains("youtube.com") || url.Contains("youtu.be");

    public static bool IsSupportedVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        string[] supportedDomains = new[]
        {
            "youtube.com", "youtu.be",
            "twitch.tv",
            "vimeo.com",
            "twitter.com", "x.com",
            "tiktok.com",
            "reddit.com",
            "bilibili.com",
        };

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

            string afterList = url.Substring(listIndex + 5);
            int ampersandIndex = afterList.IndexOf('&');
            return ampersandIndex != -1
                ? afterList.Substring(0, ampersandIndex)
                : afterList;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Failed to extract playlist ID: {ex.Message}");
            return null;
        }
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

    public static async Task<VideoStreamResult> GetDirectUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            TerraVision.instance.Logger.Error("Video extraction system not ready");
            return null;
        }

        // FIX #17: use UtcNow for cache timestamp comparisons
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
                return cached.Result;
            }
            else
            {
                _urlCache.TryRemove(url, out _);
                TerraVision.instance.Logger.Debug("Cached URL expired, fetching new one");
            }
        }

        bool lockAcquired = false;
        try
        {
            lockAcquired = await _requestLock.WaitAsync(SemaphoreTimeout, cancellationToken);
            if (!lockAcquired)
            {
                TerraVision.instance.Logger.Error("Timed out waiting for request lock");
                return null;
            }

            // FIX #7: double-check cache after acquiring the lock — a concurrent request
            // for the same URL may have populated it while we were waiting.
            if (_urlCache.TryGetValue(url, out var cachedAfterLock))
            {
                if (!cachedAfterLock.IsLivestream && IsCachedResultStillValid(cachedAfterLock.Result, cachedAfterLock.Timestamp))
                {
                    TerraVision.instance.Logger.Debug("Using cached URL (populated while waiting for lock)");
                    return cachedAfterLock.Result;
                }
            }

            bool isDownload = url.Contains("bilibili.com");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(isDownload ? DownloadTimeout : RequestTimeout);

            // FIX #5: removed the separate IsLivestreamAsync call that was here.
            // HybridVideoExtractor now sets result.IsLivestream = true internally when it
            // detects and routes a livestream, so we get that information for free from
            // the single GetDirectUrlAsync call below — no redundant yt-dlp metadata fetch.
            VideoStreamResult streamResult = await _extractor.GetDirectUrlAsync(url, linkedCts.Token);

            if (streamResult != null)
            {
                if (!streamResult.IsLivestream)
                {
                    // FIX #17: store UtcNow so comparisons are timezone-safe
                    _urlCache[url] = (streamResult, DateTime.UtcNow, false);
                    TerraVision.instance.Logger.Debug("Cached URL for regular video");
                }
                else
                {
                    TerraVision.instance.Logger.Debug("Livestream URL not cached (expires too quickly)");
                }
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
        finally
        {
            if (lockAcquired) _requestLock.Release();
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
            return new List<string>();
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            TerraVision.instance.Logger.Error("Empty playlist ID");
            return new List<string>();
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
            return new List<string>();
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

            if (platform == "Bilibili")
                Main.NewText("Downloading Bilibili video...", Color.LightBlue);
            else
                Main.NewText($"Extracting {platform} stream URL...", Color.LightBlue);

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
        Main.NewText("Searching YouTube...", Color.LightBlue);

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

                Main.QueueMainThreadAction(() => Main.NewText("Found video, extracting stream...", Color.LightGreen));

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