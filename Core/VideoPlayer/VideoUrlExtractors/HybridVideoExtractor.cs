using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TerraVision.Core.VideoPlayer.VideoUrlExtractors;

/// <summary>
/// Hybrid video extractor that routes between BilibiliExtractor (direct DASH API)
/// and yt-dlp (everything else). YoutubeExplode has been removed — yt-dlp handles
/// all YouTube extraction in a single invocation that also returns livestream status,
/// title, and chapter data, eliminating the previous double-invocation overhead.
/// </summary>
public class HybridVideoExtractor : IVideoUrlExtractor
{
    private readonly YtDlExtractor _ytdlp;
    private readonly BilibiliExtractor _bilibili;

    private bool _isInitialized = false;

    public string Name => "Hybrid";
    public bool SupportsLivestreams => _ytdlp.IsAvailable;
    public bool IsAvailable => _ytdlp.IsAvailable;

    public HybridVideoExtractor()
    {
        _ytdlp = new();
        _bilibili = new();
    }

    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return true;

        TerraVision.instance.Logger.Info("Initializing hybrid video extractor...");

        bool ytdlpSuccess = await _ytdlp.InitializeAsync();

        if (!ytdlpSuccess)
        {
            TerraVision.instance.Logger.Error("yt-dlp initialization failed!");
            return false;
        }

        TerraVision.instance.Logger.Info("yt-dlp initialized successfully — livestream support enabled");

        _isInitialized = true;
        TerraVision.instance.Logger.Info($"Hybrid extractor initialized ({GetActiveExtractor()})");
        return true;
    }

    public async Task<VideoStreamResult> GetDirectUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Error("No video extractors available");
            return null;
        }

        if (url.Contains("bilibili.com"))
        {
            // Strategy 1: BilibiliExtractor — direct DASH stream URLs via API.
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

            // Strategy 2: yt-dlp download + merge (guaranteed to work)
            TerraVision.instance.Logger.Info("Bilibili URL — falling back to yt-dlp download");
            string localPath = await _ytdlp.DownloadToTempAsync(url, cancellationToken);
            if (localPath != null)
                return new VideoStreamResult { VideoUrl = localPath };

            TerraVision.instance.Logger.Error("yt-dlp download failed for Bilibili");
            return null;
        }

        // All other URLs — yt-dlp handles YouTube, Twitch, Vimeo, TikTok, etc.
        // Livestream detection, title, and chapter data are all returned from this
        // single invocation, eliminating the previous separate IsLivestreamAsync pre-check.
        return await _ytdlp.GetDirectUrlAsync(url, cancellationToken);
    }

    public async Task<string> SearchAsync(string searchQuery, int resultIndex, int maxResults, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Error("No video extractors available");
            return null;
        }

        try
        {
            TerraVision.instance.Logger.Info($"Searching with yt-dlp: '{searchQuery}'");
            return await _ytdlp.SearchAsync(searchQuery, resultIndex, maxResults, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TerraVision.instance.Logger.Info("Search cancelled");
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Search failed: {ex.Message}");
            return null;
        }
    }

    public async Task<List<string>> GetPlaylistVideosAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Error("No video extractors available");
            return [];
        }

        try
        {
            TerraVision.instance.Logger.Info($"Fetching playlist with yt-dlp: {playlistId}");
            return await _ytdlp.GetPlaylistVideosAsync(playlistId, cancellationToken);
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

    public async Task<bool> IsLivestreamAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!_ytdlp.IsAvailable)
            return false;

        try
        {
            return await _ytdlp.IsLivestreamAsync(url, cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    public string GetActiveExtractor() => _ytdlp.IsAvailable ? "BilibiliExtractor (Bilibili) + yt-dlp (all other platforms)" : "None available";
}