using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;

namespace TerraVision.Core.VideoPlayer.VideoUrlExtractors;

/// <summary>
/// Video URL extractor using YoutubeExplode.
/// Lightweight but doesn't support livestreams.
/// </summary>
public class YoutubeExplodeExtractor : IVideoUrlExtractor
{
    private YoutubeClient _youtube;

    public string Name => "YoutubeExplode";
    public bool SupportsLivestreams => false;
    public bool IsAvailable => _youtube != null;

    public Task<bool> InitializeAsync()
    {
        try
        {
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _youtube = new YoutubeClient(httpClient);

            TerraVision.instance.Logger.Info("YoutubeExplode initialized successfully");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Failed to initialize YoutubeExplode: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public async Task<VideoStreamResult> GetDirectUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Warn("YoutubeExplode not available");
            return null;
        }

        try
        {
            var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(url, cancellationToken);

            // Muxed streams are simplest — video and audio in one URL
            var muxedStream = streamManifest
                .GetMuxedStreams()
                .OrderByDescending(s => s.VideoQuality.MaxHeight)
                .FirstOrDefault();

            if (muxedStream != null)
            {
                TerraVision.instance.Logger.Info($"YoutubeExplode: muxed stream {muxedStream.VideoQuality.Label}");
                return new VideoStreamResult { VideoUrl = muxedStream.Url };
            }

            // No muxed stream — try separate video + audio (DASH)
            var videoStream = streamManifest
                .GetVideoOnlyStreams()
                .OrderByDescending(s => s.VideoQuality.MaxHeight)
                .FirstOrDefault();

            var audioStream = streamManifest
                .GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .FirstOrDefault();

            if (videoStream != null)
            {
                TerraVision.instance.Logger.Info($"YoutubeExplode: DASH video={videoStream.VideoQuality.Label}" +
                    (audioStream != null ? $" + audio" : " (no audio)"));
                return new VideoStreamResult
                {
                    VideoUrl = videoStream.Url,
                    AudioUrl = audioStream?.Url
                };
            }

            // Last resort: audio only
            if (audioStream != null)
            {
                TerraVision.instance.Logger.Warn("YoutubeExplode: audio-only stream (no video)");
                return new VideoStreamResult { VideoUrl = audioStream.Url };
            }

            TerraVision.instance.Logger.Error("YoutubeExplode: no suitable streams found");
            return null;
        }
        catch (OperationCanceledException)
        {
            TerraVision.instance.Logger.Info("YoutubeExplode URL extraction cancelled");
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"YoutubeExplode URL extraction failed: {ex.Message}");
            return null;
        }
    }
    public Task<string> SearchAsync(string searchQuery, int resultIndex, int maxResults, CancellationToken cancellationToken = default)
    {
        // YoutubeExplode's SearchAsync returns IAsyncEnumerable<T>, whose internal async
        // iterator state machines trigger JIT compilation failures in Terraria's runtime.
        // There is no safe way to call this API in this environment — even reflection-based
        // invocation still causes the JIT to compile YoutubeExplode's own state machines
        // the moment the enumerator is advanced. Returning null here lets HybridVideoExtractor
        // fall through to yt-dlp, which handles search reliably.
        return Task.FromResult<string>(null);
    }

    public async Task<List<string>> GetPlaylistVideosAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        var videoUrls = new List<string>();

        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Warn("YoutubeExplode not available");
            return videoUrls;
        }

        try
        {
            // Fallback to web scraping method
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            string playlistUrl = $"https://www.youtube.com/playlist?list={playlistId}";
            string html = await httpClient.GetStringAsync(playlistUrl, cancellationToken);

            var videoIdMatches = System.Text.RegularExpressions.Regex.Matches(
                html,
                @"/watch\?v=([A-Za-z0-9_-]{11})"
            );

            foreach (System.Text.RegularExpressions.Match match in videoIdMatches)
            {
                if (match.Groups.Count > 1)
                {
                    string videoId = match.Groups[1].Value;
                    string videoUrl = $"https://www.youtube.com/watch?v={videoId}";
                    if (!videoUrls.Contains(videoUrl))
                    {
                        videoUrls.Add(videoUrl);
                    }
                }
            }

            videoUrls = videoUrls.Distinct().ToList();
            TerraVision.instance.Logger.Info($"YoutubeExplode extracted {videoUrls.Count} videos from playlist");
            return videoUrls;
        }
        catch (OperationCanceledException)
        {
            TerraVision.instance.Logger.Info("YoutubeExplode playlist fetch cancelled");
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"YoutubeExplode playlist fetch failed: {ex.Message}");
            return videoUrls;
        }
    }

    public Task<bool> IsLivestreamAsync(string url, CancellationToken cancellationToken = default)
    {
        // YoutubeExplode doesn't support livestreams
        return Task.FromResult(false);
    }
}