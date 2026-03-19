using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TerraVision.Core.VideoPlayer.VideoUrlExtractors;

/// <summary>
/// Hybrid video extractor that tries multiple extraction methods with intelligent fallback.
/// Prefers YoutubeExplode (faster, lightweight) for regular videos, uses yt-dlp for livestreams.
/// </summary>
public class HybridVideoExtractor : IVideoUrlExtractor
{
    private readonly YtDlExtractor _ytdlp;
    private readonly YoutubeExplodeExtractor _youtubeExplode;
    private readonly BilibiliExtractor _bilibili;

    private bool _isInitialized = false;

    // Timeout for fast method before falling back
    private static readonly TimeSpan FastMethodTimeout = TimeSpan.FromSeconds(10);

    public string Name => "Hybrid";
    public bool SupportsLivestreams => _ytdlp.IsAvailable; // Only if yt-dlp is available
    public bool IsAvailable => _ytdlp.IsAvailable || _youtubeExplode.IsAvailable;

    public HybridVideoExtractor()
    {
        _ytdlp = new();
        _youtubeExplode = new();
        _bilibili = new();
    }

    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return true;

        TerraVision.instance.Logger.Info("Initializing hybrid video extractor...");

        bool ytdlpSuccess = await _ytdlp.InitializeAsync();
        if (ytdlpSuccess)
            TerraVision.instance.Logger.Info("yt-dlp initialized successfully - livestream support enabled");
        else
            TerraVision.instance.Logger.Warn("yt-dlp initialization failed - livestream support disabled");

        bool explodeSuccess = await _youtubeExplode.InitializeAsync();
        if (!explodeSuccess && !ytdlpSuccess)
        {
            TerraVision.instance.Logger.Error("Both yt-dlp and YoutubeExplode failed to initialize!");
            return false;
        }

        _isInitialized = true;
        TerraVision.instance.Logger.Info($"Hybrid extractor initialized (yt-dlp: {ytdlpSuccess}, YoutubeExplode: {explodeSuccess})");
        return true;
    }

    public async Task<VideoStreamResult> GetDirectUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Error("No video extractors available");
            return null;
        }

        bool isBilibili = url.Contains("bilibili.com");

        if (isBilibili)
        {
            // Strategy 1: BilibiliExtractor — direct DASH stream URLs via API
            // Fast path: no download or merge needed, VLC plays the streams directly.
            // Requires saved cookies (SESSDATA) for best quality; degrades to 720p without.
            try
            {
                TerraVision.instance.Logger.Info("Bilibili URL — attempting direct stream extraction");
                VideoStreamResult streamResult = await _bilibili.GetDirectUrlAsync(url, cancellationToken);
                if (streamResult != null)
                {
                    TerraVision.instance.Logger.Info("Bilibili direct stream extraction successful");
                    return streamResult;
                }
                TerraVision.instance.Logger.Debug("Bilibili direct extraction returned null, falling back to download");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Debug($"Bilibili direct extraction failed: {ex.Message}, falling back to download");
            }

            // Strategy 2: yt-dlp download + merge (original slow path, guaranteed to work)
            if (_ytdlp.IsAvailable)
            {
                TerraVision.instance.Logger.Info("Bilibili URL — falling back to yt-dlp download");
                string localPath = await _ytdlp.DownloadToTempAsync(url, cancellationToken);
                if (localPath != null)
                    return new VideoStreamResult { VideoUrl = localPath };

                TerraVision.instance.Logger.Error("yt-dlp download failed for Bilibili");
            }
            else
            {
                TerraVision.instance.Logger.Error("yt-dlp not available for Bilibili URL");
            }
            return null;
        }

        // Check if it's a YouTube URL
        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");

        // For non-YouTube URLs, skip YoutubeExplode entirely and go straight to yt-dlp
        if (!isYouTube && _ytdlp.IsAvailable)
        {
            TerraVision.instance.Logger.Info("Non-YouTube URL detected, using yt-dlp directly");
            return await _ytdlp.GetDirectUrlAsync(url, cancellationToken);
        }

        // Check if it might be a livestream using URL patterns
        bool mightBeLivestream = url.Contains("/live/") || url.Contains("&live=") || url.Contains("/live");

        // Strategy 1: If might be livestream by URL, ONLY use yt-dlp
        if (mightBeLivestream)
        {
            if (_ytdlp.IsAvailable)
            {
                TerraVision.instance.Logger.Info("Potential livestream detected by URL, using yt-dlp");
                return await _ytdlp.GetDirectUrlAsync(url, cancellationToken);
            }
            else
            {
                TerraVision.instance.Logger.Warn("Livestream detected but yt-dlp not available");
                return null;
            }
        }

        // Strategy 1.5: Quick livestream check if yt-dlp is available
        // This catches livestreams with normal /watch URLs
        if (_ytdlp.IsAvailable)
        {
            try
            {
                bool isLive = await _ytdlp.IsLivestreamAsync(url, cancellationToken);
                if (isLive)
                {
                    TerraVision.instance.Logger.Info("Livestream detected by metadata check, using yt-dlp");
                    return await _ytdlp.GetDirectUrlAsync(url, cancellationToken);
                }
            }
            catch
            {
                // If livestream check fails, continue to normal extraction
                TerraVision.instance.Logger.Debug("Livestream check failed, treating as regular video");
            }
        }

        // Strategy 2: Try YoutubeExplode first for regular videos (faster and more reliable)
        if (_youtubeExplode.IsAvailable)
        {
            try
            {
                TerraVision.instance.Logger.Info("Attempting URL extraction with YoutubeExplode (fast method)...");

                using var timeoutCts = new CancellationTokenSource(FastMethodTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                VideoStreamResult result = await _youtubeExplode.GetDirectUrlAsync(url, linkedCts.Token);
                if (result != null)
                {
                    TerraVision.instance.Logger.Info("YoutubeExplode extraction successful");
                    return result;
                }
                TerraVision.instance.Logger.Debug("YoutubeExplode extraction returned null, trying yt-dlp fallback");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User cancelled, propagate
                throw;
            }
            catch (OperationCanceledException)
            {
                // Timeout, try fallback - this is expected, use Debug level
                TerraVision.instance.Logger.Debug("YoutubeExplode timed out, trying yt-dlp fallback");
            }
            catch (Exception ex)
            {
                // Expected for livestreams - use Debug level instead of Warn
                string errorMsg = ex.Message?.ToLower() ?? "";
                if (errorMsg.Contains("unplayable") || errorMsg.Contains("cipher") || errorMsg.Contains("livestream") || errorMsg.Contains("live"))
                {
                    TerraVision.instance.Logger.Debug($"YoutubeExplode extraction failed (expected for livestream/special videos), trying yt-dlp fallback");
                }
                else
                {
                    TerraVision.instance.Logger.Warn($"YoutubeExplode extraction failed: {ex.Message}, trying yt-dlp fallback");
                }
            }
        }

        // Strategy 3: Fallback to yt-dlp (slower but more robust)
        if (_ytdlp.IsAvailable)
        {
            try
            {
                TerraVision.instance.Logger.Info("Attempting URL extraction with yt-dlp (fallback)...");
                VideoStreamResult result = await _ytdlp.GetDirectUrlAsync(url, cancellationToken);
                if (result != null)
                {
                    TerraVision.instance.Logger.Info("yt-dlp extraction successful");
                    return result;
                }
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"yt-dlp extraction also failed: {ex.Message}");
            }
        }

        TerraVision.instance.Logger.Error("All extraction methods failed");
        return null;
    }

    public async Task<string> SearchAsync(string searchQuery, int resultIndex, int maxResults, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Error("No video extractors available");
            return null;
        }

        // Try YoutubeExplode first (faster for searches)
        if (_youtubeExplode.IsAvailable)
        {
            try
            {
                TerraVision.instance.Logger.Info($"Searching with YoutubeExplode (fast method): '{searchQuery}'");

                using var timeoutCts = new CancellationTokenSource(FastMethodTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                string result = await _youtubeExplode.SearchAsync(searchQuery, resultIndex, maxResults, linkedCts.Token);
                if (result != null)
                {
                    TerraVision.instance.Logger.Info("YoutubeExplode search successful");
                    return result;
                }
                TerraVision.instance.Logger.Debug("YoutubeExplode search returned no results, trying yt-dlp fallback");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User cancelled, propagate
                throw;
            }
            catch (OperationCanceledException)
            {
                // Timeout, try fallback - use Debug level
                TerraVision.instance.Logger.Debug("YoutubeExplode search timed out, trying yt-dlp fallback");
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Debug($"YoutubeExplode search failed: {ex.Message}, trying yt-dlp fallback");
            }
        }

        // Fallback to yt-dlp
        if (_ytdlp.IsAvailable)
        {
            try
            {
                TerraVision.instance.Logger.Info($"Searching with yt-dlp (fallback): '{searchQuery}'");
                string result = await _ytdlp.SearchAsync(searchQuery, resultIndex, maxResults, cancellationToken);
                if (result != null)
                {
                    TerraVision.instance.Logger.Info("yt-dlp search successful");
                    return result;
                }
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"yt-dlp search also failed: {ex.Message}");
            }
        }

        TerraVision.instance.Logger.Error("All search methods failed");
        return null;
    }

    public async Task<List<string>> GetPlaylistVideosAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Error("No video extractors available");
            return new List<string>();
        }

        // Try YoutubeExplode first (faster for playlists)
        if (_youtubeExplode.IsAvailable)
        {
            try
            {
                TerraVision.instance.Logger.Info($"Fetching playlist with YoutubeExplode (fast method): {playlistId}");

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)); // Playlists may take longer
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var result = await _youtubeExplode.GetPlaylistVideosAsync(playlistId, linkedCts.Token);
                if (result.Count > 0)
                {
                    TerraVision.instance.Logger.Info($"YoutubeExplode playlist fetch successful: {result.Count} videos");
                    return result;
                }
                TerraVision.instance.Logger.Debug("YoutubeExplode playlist fetch returned no videos, trying yt-dlp fallback");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User cancelled, propagate
                throw;
            }
            catch (OperationCanceledException)
            {
                // Timeout, try fallback - use Debug level
                TerraVision.instance.Logger.Debug("YoutubeExplode playlist fetch timed out, trying yt-dlp fallback");
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Debug($"YoutubeExplode playlist fetch failed: {ex.Message}, trying yt-dlp fallback");
            }
        }

        // Fallback to yt-dlp
        if (_ytdlp.IsAvailable)
        {
            try
            {
                TerraVision.instance.Logger.Info($"Fetching playlist with yt-dlp (fallback): {playlistId}");
                var result = await _ytdlp.GetPlaylistVideosAsync(playlistId, cancellationToken);
                if (result.Count > 0)
                {
                    TerraVision.instance.Logger.Info($"yt-dlp playlist fetch successful: {result.Count} videos");
                    return result;
                }
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"yt-dlp playlist fetch also failed: {ex.Message}");
            }
        }

        TerraVision.instance.Logger.Error("All playlist fetch methods failed");
        return new List<string>();
    }

    public async Task<bool> IsLivestreamAsync(string url, CancellationToken cancellationToken = default)
    {
        // Only yt-dlp can detect livestreams
        if (_ytdlp.IsAvailable)
        {
            try
            {
                return await _ytdlp.IsLivestreamAsync(url, cancellationToken);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Get which extractor is currently being used (for debugging/status).
    /// </summary>
    public string GetActiveExtractor()
    {
        if (_ytdlp.IsAvailable && _youtubeExplode.IsAvailable)
            return "YoutubeExplode (YouTube) + BilibiliExtractor (Bilibili) + yt-dlp (fallback/livestreams)";
        if (_ytdlp.IsAvailable)
            return "BilibiliExtractor (Bilibili) + yt-dlp only";
        if (_youtubeExplode.IsAvailable)
            return "YoutubeExplode only";
        return "None available";
    }
}