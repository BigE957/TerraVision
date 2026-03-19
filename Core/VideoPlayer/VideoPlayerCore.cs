using LibVLCSharp.Shared;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;
using TerraVision.Core.VideoPlayer.Captions;
using TerraVision.Core.VideoPlayer.Danmaku;
using TerraVision.Core.VideoPlayer.VideoUrlExtractors;

namespace TerraVision.Core.VideoPlayer;

/// <summary>
/// Core video player logic
/// Handles LibVLC media playback, frame capture, and texture management.
/// </summary>
public class VideoPlayerCore(int videoWidth = 1280, int videoHeight = 720) : IDisposable
{
    // Video playback components
    private MediaPlayer _mediaPlayer;

    private Media _currentMedia;

    private VideoFrameHandler _frameHandler;
    private Texture2D _videoTexture;
    private readonly TexturePool _texturePool = new();

    // Video dimensions
    private readonly int _videoWidth = videoWidth;
    private readonly int _videoHeight = videoHeight;

    // Playback state
    private bool _isInitialized;
    private bool _isPlaying;
    private bool _isPaused;
    private string _currentVideoPath;
    private bool _isLoading;
    private float _loadingRotation;
    private bool _isPreparing;

    // Video queue for playlists
    private Queue<string> _videoQueue = [];
    private bool _autoAdvanceQueue = false;
    private string _currentlyPlayingUrl = null;

    // Session and request tracking
    private Guid _sessionId = Guid.NewGuid();
    private Guid _currentRequestId;
    private bool _isDisposed = false;

    // Display settings
    private bool _maintainAspectRatio = true;

    // Frame rate limiting
    private double _lastFrameTime;
    private const double TARGET_FRAME_TIME = 1.0 / 30.0;

    private bool _textureNeedsUpdate;

    // Thread safety
    private readonly object _stateLock = new();

    // CDN failover — backup URLs tried in order when primary stream errors mid-play
    private List<string> _backupUrls = [];
    private int _backupUrlIndex = 0;
    private VideoStreamResult _currentStreamResult = null;

    // Danmaku
    private readonly DanmakuRenderer _danmakuRenderer = new();
    private List<DanmakuComment> _danmakuComments = null;
    private string _danmakuSourceUrl = null; // URL we fetched comments for
    private CancellationTokenSource _danmakuFetchCts = null;
    private float _danmakuTimer = 0f;

    // Captions
    private readonly CaptionRenderer _captionRenderer = new();
    private List<CaptionBlock> _captions = null;
    private string _captionSourceUrl = null;
    private bool _captionFetchInProgress = false;
    private CancellationTokenSource _captionFetchCts = null;

    #region Events

    public event EventHandler PlaybackStarted;
    public event EventHandler PlaybackReady;
    public event EventHandler PlaybackPaused;
    public event EventHandler PlaybackStopped;
    public event EventHandler PlaybackEnded;
    public event EventHandler PlaybackError;
    public event EventHandler<bool> LoadingStateChanged;

    public delegate void AudioDataCallback(byte[] audioData, int sampleRate, int channels, long pts);
    public event AudioDataCallback OnAudioDataProduced;

    #endregion

    #region Properties

    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;
    public bool IsInitialized => _isInitialized;
    public bool IsLoading => _isLoading;
    public bool IsPreparing => _isPreparing;
    public int QueuedVideoCount => _videoQueue.Count;
    public bool HasQueuedVideos => _videoQueue.Count > 0;
    public string CurrentVideoPath => _currentVideoPath;
    public Texture2D CurrentTexture => _videoTexture;
    public int VideoWidth => _videoWidth;
    public int VideoHeight => _videoHeight;
    public float LoadingRotation => _loadingRotation;
    public Guid SessionId => _sessionId;

    #endregion

    #region Initialization
    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized || TerraVision.instance == null || TerraVision.LibVLCInstance == null)
            return;

        try
        {
            Main.QueueMainThreadAction(() => {
                try
                {
                    _frameHandler = new VideoFrameHandler(_videoWidth, _videoHeight);
                    _mediaPlayer = new MediaPlayer(TerraVision.LibVLCInstance);
                    _frameHandler.SetupMediaPlayer(_mediaPlayer);
                    _mediaPlayer.Playing += OnPlaying;
                    _mediaPlayer.Paused += OnPaused;
                    _mediaPlayer.Stopped += OnStopped;
                    _mediaPlayer.EndReached += OnEndReached;
                    _mediaPlayer.EncounteredError += OnError;

                    _isInitialized = true;

                    TerraVision.instance.Logger.Info($"VideoPlayerCore initialized (session {_sessionId})");
                }
                catch (Exception ex)
                {
                    TerraVision.instance.Logger.Error($"VideoPlayerCore initialization failed: {ex}");
                }
            });
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"VideoPlayerCore async initialization failed: {ex}");
        }
    }
    #endregion

    #region Playback Control

    /// <summary>
    /// Play media from any source. Supports YouTube, Twitch, Vimeo, TikTok, and many more platforms.
    /// </summary>
    /// <param name="forcePlay">If true, clears queue and plays immediately</param>
    public void Play(string input, bool forcePlay = true)
    {
        if (!_isInitialized)
        {
            Task.Run(() => EnsureInitializedAsync());
            return;
        }

        if (string.IsNullOrWhiteSpace(input))
            return;

        try
        {
            lock (_stateLock)
            {
                if (VideoUrlHelper.IsYouTubePlaylist(input))
                {
                    PlayPlaylist(input, forcePlay);
                    return;
                }

                if (forcePlay)
                {
                    if (_isPlaying || _isPaused)
                        _mediaPlayer?.Stop();

                    VideoUrlHelper.CancelRequest(_currentRequestId);
                    ClearVideoTexture();

                    _videoQueue.Clear();
                    _autoAdvanceQueue = false;
                    _videoQueue.Enqueue(input);
                    PlayNextInQueue();
                }
                else
                {
                    _videoQueue.Enqueue(input);

                    if (!_isPlaying && !_isLoading && !_isPreparing)
                    {
                        _autoAdvanceQueue = false;
                        PlayNextInQueue();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Play failed: {ex.Message}");
        }
    }

    public static bool IsFilePath(string input)
    {
        if (System.IO.Path.IsPathRooted(input))
            return true;

        if ((input.Contains('/') || input.Contains('\\')) && HasVideoExtension(input))
            return true;

        return false;
    }

    private static readonly string[] VideoExtensions = [".mp4", ".avi", ".mkv", ".mov", ".flv", ".webm", ".m4v"];

    private static bool HasVideoExtension(string input)
    {
        string lower = input.ToLowerInvariant();
        return VideoExtensions.Any(ext => lower.EndsWith(ext));
    }

    public static bool IsMediaLink(string input) => input.Contains(".mp4") || input.Contains(".avi") || input.Contains(".mkv") || input.Contains(".mov") || input.Contains(".flv") || input.Contains(".m3u8");

    private void PlayLocalFile(string filePath)
    {
        string resolvedPath = ResolveVideoPath(filePath);
        if (resolvedPath == null)
        {
            TerraVision.instance.Logger.Error($"Video file not found: {filePath}");
            return;
        }
        _isPreparing = true;
        _currentMedia = new Media(TerraVision.LibVLCInstance, resolvedPath, FromType.FromPath);
        _currentVideoPath = filePath;
        _mediaPlayer.Play(_currentMedia);
    }

    private static string ResolveVideoPath(string filePath)
    {
        if (System.IO.Path.IsPathRooted(filePath))
        {
            return System.IO.File.Exists(filePath) ? filePath : null;
        }

        string[] extensionsToTry = filePath.Contains('.')
            ? [""]
            : [".mp4", ".avi", ".mkv", ".mov"];

        foreach (string ext in extensionsToTry)
        {
            string testPath = filePath + ext;

            // Try to extract mod name from path (e.g., "ModName/path/to/video.mp4")
            string[] pathParts = testPath.Split('/');
            if (pathParts.Length > 1)
            {
                string modName = pathParts[0];
                if (ModLoader.TryGetMod(modName, out Mod source))
                {
                    string relativePath = string.Join("/", pathParts.Skip(1));
                    if (source.FileExists(relativePath))
                    {
                        // Extract to temp file since VLC needs a real file path
                        return ExtractModFileToTemp(source, relativePath);
                    }
                }
            }

            // Check if it's in the current mod
            if (TerraVision.instance.FileExists(testPath))
            {
                return ExtractModFileToTemp(TerraVision.instance, testPath);
            }

            // Try ModSources folder (for development)
            string fullPath = ModLoader.ModPath.Replace("Mods", "ModSources") +
                             System.IO.Path.DirectorySeparatorChar + testPath.Replace('/', '\\');
            if (System.IO.File.Exists(fullPath))
            {
                return fullPath;
            }

            // Try as absolute path
            if (System.IO.File.Exists(testPath))
            {
                return System.IO.Path.GetFullPath(testPath);
            }
        }
        return null;
    }

    private static string ExtractModFileToTemp(Mod mod, string internalPath)
    {
        try
        {
            // Create a temp directory for extracted videos
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TerrariaVideos", mod.Name);
            System.IO.Directory.CreateDirectory(tempDir);

            // Create temp file path
            string fileName = System.IO.Path.GetFileName(internalPath);
            string tempPath = System.IO.Path.Combine(tempDir, fileName);

            // Extract if not already extracted or if file is outdated
            if (!System.IO.File.Exists(tempPath))
            {
                using (var stream = mod.GetFileStream(internalPath))
                using (var fileStream = System.IO.File.Create(tempPath))
                {
                    stream.CopyTo(fileStream);
                }
            }

            return tempPath;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Failed to extract video file: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Play video from a supported platform URL.
    /// Works with YouTube, Twitch, Vimeo, TikTok, Twitter, Reddit, and many more.
    /// </summary>
    private void PlayVideoUrlInternal(string url)
    {
        _currentRequestId = Guid.NewGuid();
        var requestId = _currentRequestId;
        var sessionId = _sessionId;

        TerraVision.instance.Logger.Info($"PlayVideoUrlInternal called for session {_sessionId}, request {requestId}");
        _isPreparing = true;
        SetLoadingState(true);

        // Fetch captions for all supported platforms — YouTube, Bilibili, Vimeo, and generic.
        // Local file paths are excluded since there's nothing to fetch for those.
        bool isLocalFile = IsFilePath(url);
        if (!isLocalFile)
            FetchCaptionsAsync(url);
        else
            _captions = null;

        // Kick off danmaku fetch for Bilibili videos
        if (url.Contains("bilibili.com"))
            FetchDanmakuAsync(url);
        else
            ClearExtraRenderers();

        VideoUrlHelper.ProcessUrlAsync(url, (streamResult) =>
        {
            try
            {
                Main.QueueMainThreadAction(() =>
                {
                    try
                    {
                        TerraVision.instance.Logger.Info($"Processed url found: {streamResult?.VideoUrl}");
                        SetLoadingState(false);

                        if (_sessionId != sessionId || _currentRequestId != requestId || _isDisposed)
                        {
                            _isPreparing = false;
                            TerraVision.instance.Logger.Warn("Invalid Session ID!");
                            return;
                        }

                        if (streamResult == null)
                        {
                            TerraVision.instance.Logger.Error("Failed to extract video URL");
                            _isPreparing = false;
                            PlayNextInQueue();
                            return;
                        }

                        _currentStreamResult = streamResult;
                        _backupUrls = streamResult.HttpHeaders.TryGetValue("X-Backup-Urls", out string rawBU)
                            ? [.. rawBU.Split('|', StringSplitOptions.RemoveEmptyEntries)]
                            : [];
                        _backupUrlIndex = 0;
                        _currentMedia = BuildMedia(streamResult);
                        _currentVideoPath = url;
                        _mediaPlayer.Play(_currentMedia);
                    }
                    catch (Exception ex)
                    {
                        TerraVision.instance.Logger.Error($"Failed to play video URL: {ex.Message}");
                        _isPreparing = false;
                        PlayNextInQueue();
                    }
                });
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"Error in PlayVideoUrlInternal callback: {ex.Message}");
            }
        }, requestId);
    }

    private void PlayMediaUrlInternal(string url)
    {
        SetLoadingState(false);

        if (url == null)
        {
            TerraVision.instance.Logger.Error("Invalid media URL");
            PlayNextInQueue();
            return;
        }

        try
        {
            _currentMedia = new Media(TerraVision.LibVLCInstance, url, FromType.FromLocation);
            _currentVideoPath = url;
            _mediaPlayer.Play(_currentMedia);
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Failed to play media URL: {ex.Message}");
            PlayNextInQueue();
        }
    }

    private void PlayYouTubeSearchInternal(string query, int resultIndex, int maxResults)
    {
        _currentRequestId = Guid.NewGuid();
        var requestId = _currentRequestId;
        var sessionId = _sessionId;

        _isPreparing = true;
        SetLoadingState(true);

        VideoUrlHelper.ProcessSearchAsync(query, (streamResult) =>
        {
            try
            {
                Main.QueueMainThreadAction(() =>
                {
                    try
                    {
                        SetLoadingState(false);

                        if (_sessionId != sessionId || _currentRequestId != requestId || _isDisposed)
                        {
                            _isPreparing = false;
                            return;
                        }

                        if (streamResult == null)
                        {
                            _isPreparing = false;
                            PlayNextInQueue();
                            return;
                        }

                        _currentStreamResult = streamResult;
                        _backupUrls = streamResult.HttpHeaders.TryGetValue("X-Backup-Urls", out string rawBU2)
                            ? [.. rawBU2.Split('|', StringSplitOptions.RemoveEmptyEntries)]
                            : [];
                        _backupUrlIndex = 0;
                        _currentMedia = BuildMedia(streamResult);
                        _currentVideoPath = query;
                        _mediaPlayer.Play(_currentMedia);
                    }
                    catch (Exception ex)
                    {
                        TerraVision.instance.Logger.Error($"Failed to play search result: {ex.Message}");
                        _isPreparing = false;
                        PlayNextInQueue();
                    }
                });
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"Error in PlayYouTubeSearchInternal callback: {ex.Message}");
            }
        }, requestId, resultIndex, maxResults);
    }

    private static Media BuildMedia(VideoStreamResult streamResult)
    {
        // Local file path (e.g. downloaded Bilibili temp file) — no headers needed
        if (Path.IsPathRooted(streamResult.VideoUrl) && File.Exists(streamResult.VideoUrl))
        {
            TerraVision.instance.Logger.Debug($"Playing from local file: {streamResult.VideoUrl}");
            return new Media(TerraVision.LibVLCInstance, streamResult.VideoUrl, FromType.FromPath);
        }

        // Remote URL
        var media = new Media(TerraVision.LibVLCInstance, streamResult.VideoUrl, FromType.FromLocation);

        if (streamResult.HttpHeaders.TryGetValue("Referer", out string referer))
            media.AddOption($":http-referrer={referer}");

        if (streamResult.HttpHeaders.TryGetValue("User-Agent", out string userAgent))
            media.AddOption($":http-user-agent={userAgent}");

        // Bilibili CDN resilience options.
        // Important: do NOT use prefetch-buffer-size — it opens parallel HTTP connections
        // which Bilibili's CDN rate-limits immediately, causing the burst of Stream closed
        // errors visible at the start of playback. Single-connection streaming is required.
        bool isBilibili = streamResult.VideoUrl.Contains("bilivideo.com") ||
                          streamResult.VideoUrl.Contains("akamaized.net");
        if (isBilibili)
        {
            // Buffer 5 seconds of data — enough to absorb brief CDN throttling without
            // opening extra connections the way prefetch-buffer-size does
            media.AddOption(":network-caching=5000");

            // Automatically retry broken connections instead of treating them as EOF
            media.AddOption(":http-reconnect");

            // Tolerate timestamp discontinuities after reconnections — without this,
            // VLC desynchronizes its internal clock on reconnect and drops frames
            media.AddOption(":clock-jitter=0");
            media.AddOption(":clock-synchro=0");
        }

        // Backup CDN URLs — passed as additional input slaves so VLC can fall back
        // if the primary node drops the connection mid-stream
        if (streamResult.HttpHeaders.TryGetValue("X-Backup-Urls", out string backupUrlsRaw))
        {
            foreach (string backupUrl in backupUrlsRaw.Split('|', StringSplitOptions.RemoveEmptyEntries))
                media.AddOption($":input-slave={backupUrl}");
        }

        // Note: LibVLC cannot send Cookie headers on CDN requests — this is a hard
        // limitation of the library. Bilibili streams use fnval=1 (MP4/durl) which
        // embeds auth in the URL query params, so no Cookie header is needed.
        if (!string.IsNullOrWhiteSpace(streamResult.AudioUrl))
            media.AddOption($":input-slave={streamResult.AudioUrl}");

        return media;
    }

    private void FetchCaptionsAsync(string url)
    {
        _captionFetchCts?.Cancel();

        if (_captionSourceUrl == url && _captions != null)
        {
            // Same URL, already have data — just reload the renderer
            _captionRenderer.LoadCaptions(_captions);
            TerraVision.instance.Logger.Debug("Captions reloaded from cache");
            return;
        }

        _captionFetchCts = new CancellationTokenSource();
        var cts = _captionFetchCts;
        _captionSourceUrl = url;
        _captions = null;

        string ytdlpPath = YtDlExtractor.GetYtDlpPath();
        if (ytdlpPath == null)
        {
            TerraVision.instance.Logger.Warn("yt-dlp path not available for caption fetch");
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                var entries = await CaptionFetcher.FetchAsync(url, ytdlpPath, cts.Token);
                if (cts.IsCancellationRequested) return;
                Main.QueueMainThreadAction(() =>
                {
                    _captions = entries;
                    _captionRenderer.LoadCaptions(entries);
                    TerraVision.instance.Logger.Info(
                        $"Captions ready: {entries.Count} entries loaded");
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"Caption fetch error: {ex.Message}");
            }
        }, cts.Token);
    }

    private void FetchDanmakuAsync(string url)
    {
        _danmakuFetchCts?.Cancel();

        if (_danmakuSourceUrl == url && _danmakuComments != null)
        {
            // Same URL, already have data — just reload the renderer
            _danmakuRenderer.LoadComments(_danmakuComments);
            TerraVision.instance.Logger.Debug("Danmaku reloaded from cache");
            return;
        }

        _danmakuFetchCts = new CancellationTokenSource();
        var cts = _danmakuFetchCts;
        _danmakuSourceUrl = url;
        ClearExtraRenderers();

        Task.Run(async () =>
        {
            try
            {
                var comments = await DanmakuFetcher.FetchAsync(url, cts.Token);
                if (cts.IsCancellationRequested) return;
                Main.QueueMainThreadAction(() =>
                {
                    _danmakuComments = comments;
                    _danmakuRenderer.LoadComments(comments);
                    TerraVision.instance.Logger.Info(
                        $"Danmaku ready: {comments.Count} comments loaded");
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"Danmaku fetch error: {ex.Message}");
            }
        }, cts.Token);
    }

    private void ClearExtraRenderers()
    {
        _danmakuComments = null;
        _danmakuRenderer.Clear();
        _danmakuTimer = 0f;

        _captionRenderer.Clear();
    }

    /// <summary>
    /// Play a YouTube search with specific result parameters.
    /// </summary>
    public void PlayWithSearchParams(string searchQuery, int resultIndex = 0, int maxResults = 10, bool forcePlay = true)
    {
        if (!_isInitialized)
        {
            Task.Run(() => EnsureInitializedAsync());
            return;
        }

        if (string.IsNullOrWhiteSpace(searchQuery))
            return;

        try
        {
            lock (_stateLock)
            {
                if (forcePlay)
                {
                    if (_isPlaying || _isPaused)
                    {
                        _mediaPlayer?.Stop();
                    }

                    VideoUrlHelper.CancelRequest(_currentRequestId);
                    ClearVideoTexture();

                    _videoQueue.Clear();
                    _autoAdvanceQueue = false;

                    PlayYouTubeSearchInternal(searchQuery, resultIndex, maxResults);
                }
                else
                {
                    _videoQueue.Enqueue(searchQuery);

                    if (!_isPlaying && !_isLoading && !_isPreparing)
                    {
                        _autoAdvanceQueue = false;
                        PlayNextInQueue();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"PlayWithSearchParams failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Play a YouTube playlist. Queues all videos.
    /// </summary>
    private void PlayPlaylist(string playlistUrl, bool forcePlay)
    {
        if (forcePlay)
        {
            if (_isPlaying || _isPaused)
            {
                _mediaPlayer?.Stop();
            }
            VideoUrlHelper.CancelRequest(_currentRequestId);
            ClearVideoTexture();
        }

        string playlistId = VideoUrlHelper.ExtractPlaylistId(playlistUrl);
        if (string.IsNullOrEmpty(playlistId))
        {
            TerraVision.instance.Logger.Error("Failed to extract playlist ID");
            return;
        }

        _currentRequestId = Guid.NewGuid();
        var requestId = _currentRequestId;
        var sessionId = _sessionId;

        _isPreparing = true;
        SetLoadingState(true);

        Task.Run(async () =>
        {
            try
            {
                var cts = new CancellationTokenSource();
                List<string> videoUrls = await VideoUrlHelper.GetPlaylistVideosAsync(playlistId, cts.Token);

                Main.QueueMainThreadAction(() =>
                {
                    try
                    {
                        SetLoadingState(false);

                        if (_sessionId != sessionId || _currentRequestId != requestId || _isDisposed)
                        {
                            TerraVision.instance.Logger.Error("Invalid Session");
                            _isPreparing = false;
                            return;
                        }

                        if (videoUrls.Count == 0)
                        {
                            TerraVision.instance.Logger.Error("No videos found in playlist");
                            _isPreparing = false;
                            return;
                        }

                        lock (_stateLock)
                        {
                            if (forcePlay)
                            {
                                _videoQueue.Clear();
                            }

                            _autoAdvanceQueue = true;
                            foreach (string videoUrl in videoUrls)
                            {
                                _videoQueue.Enqueue(videoUrl);
                            }

                            Main.NewText($"Loaded playlist: {videoUrls.Count} videos", Color.LightGreen);

                            if (!_isPlaying)
                            {
                                PlayNextInQueue();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TerraVision.instance.Logger.Error($"Error processing playlist results: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"Playlist loading failed: {ex.Message}");
                Main.QueueMainThreadAction(() =>
                {
                    _isPreparing = false;
                    SetLoadingState(false);
                });
            }
        });
    }

    /// <summary>
    /// Play a list of video URLs as a playlist.
    /// Useful when you've already fetched the URLs and want to reorder them.
    /// </summary>
    public void PlayPlaylistFromUrls(List<string> videoUrls, bool forcePlay = true)
    {
        if (!_isInitialized)
        {
            Task.Run(() => EnsureInitializedAsync());
            return;
        }

        if (videoUrls == null || videoUrls.Count == 0)
        {
            TerraVision.instance.Logger.Warn("PlayPlaylistFromUrls called with empty list");
            return;
        }

        try
        {
            lock (_stateLock)
            {
                if (forcePlay)
                {
                    if (_isPlaying || _isPaused)
                    {
                        _mediaPlayer?.Stop();
                    }
                    VideoUrlHelper.CancelRequest(_currentRequestId);
                    ClearVideoTexture();
                    _videoQueue.Clear();
                }

                _autoAdvanceQueue = true;
                foreach (string videoUrl in videoUrls)
                {
                    _videoQueue.Enqueue(videoUrl);
                }

                TerraVision.instance.Logger.Info($"Queued {videoUrls.Count} videos from playlist");

                if (!_isPlaying)
                {
                    PlayNextInQueue();
                }
            }
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"PlayPlaylistFromUrls failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Play the next video in the queue.
    /// </summary>
    private void PlayNextInQueue()
    {
        lock (_stateLock)
        {
            if (_videoQueue.Count == 0)
            {
                _autoAdvanceQueue = false;
                _currentlyPlayingUrl = null;
                TerraVision.instance.Logger.Info("Queue finished");
                return;
            }

            string nextVideo = _videoQueue.Dequeue();
            _currentlyPlayingUrl = nextVideo;
            TerraVision.instance.Logger.Info($"Playing next in queue ({_videoQueue.Count} remaining): {nextVideo}");

            ClearVideoTexture();

            // Check if it's a supported video platform URL (YouTube, Twitch, Vimeo, etc.)
            if (VideoUrlHelper.IsSupportedVideoUrl(nextVideo))
                PlayVideoUrlInternal(nextVideo);
            // Check if it's a local file path
            else if (IsFilePath(nextVideo))
                PlayLocalFile(nextVideo);
            // Check if it's a direct media link
            else if (IsMediaLink(nextVideo))
                PlayMediaUrlInternal(nextVideo);
            // Otherwise treat as YouTube search query
            else
                PlayYouTubeSearchInternal(nextVideo, 0, 10);
        }
    }

    public void ClearVideoQueue() => _videoQueue.Clear();

    public void Pause() => _mediaPlayer?.SetPause(true);
    public void Resume()
    {
        _mediaPlayer?.SetPause(false);
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (_isLoading)
            {
                Main.NewText("Loading cancelled", Color.Orange);
                _isLoading = false;
            }

            _isPreparing = false;
            _autoAdvanceQueue = false;
            _videoQueue.Clear();
            _currentlyPlayingUrl = null;

            VideoUrlHelper.CancelRequest(_currentRequestId);
            _mediaPlayer?.Stop();

            ClearVideoTexture();
        }
    }

    public void Seek(float position)
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Position = Math.Clamp(position, 0f, 1f);
        }
        _danmakuTimer = GetTimeSeconds();
    }

    public void SetVolume(int volume)
    {
        if (_mediaPlayer == null || _isDisposed)
            return;

        try
        {
            int clampedVolume = Math.Clamp(volume, 0, 100);
            _mediaPlayer.Volume = clampedVolume;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Error setting volume: {ex.Message}");
        }
    }

    public void SetMute(bool muted)
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Mute = muted;
        }
    }

    public float GetPosition() => _mediaPlayer?.Position ?? 0f;
    public long GetDuration() => _mediaPlayer?.Length ?? 0;
    public int GetVolume() => _mediaPlayer?.Volume ?? 0;

    public float GetTimeSeconds() => _mediaPlayer == null ? 0f : _mediaPlayer.Time / 1000f;


    #endregion

    #region Settings

    public void SetMaintainAspectRatio(bool maintain) => _maintainAspectRatio = maintain;

    #endregion

    #region Event Handlers

    private void OnPlaying(object sender, EventArgs e)
    {
        bool wasResuming = _isPaused; // capture before state update

        _isPlaying = true;
        _isPreparing = false;
        _isPaused = false;

        // Only sync on fresh start, not on unpause — resuming from pause keeps the
        // accumulated timer to avoid a visible danmaku jump.
        if (!wasResuming)
            _danmakuTimer = GetTimeSeconds();

        PlaybackStarted?.Invoke(this, EventArgs.Empty);

        var currentSessionId = _sessionId;
        Task.Run(async () =>
        {
            await Task.Delay(500);
            Main.QueueMainThreadAction(() =>
            {
                if (_isPlaying && _sessionId == currentSessionId && !_isDisposed)
                {
                    PlaybackReady?.Invoke(this, EventArgs.Empty);
                }
            });
        });
    }

    private void OnPaused(object sender, EventArgs e)
    {
        _isPaused = true;
        PlaybackPaused?.Invoke(this, EventArgs.Empty);
    }

    private void OnStopped(object sender, EventArgs e)
    {
        _isPlaying = false;
        _isPaused = false;
        _isPreparing = false;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
        _danmakuTimer = 0f;
    }

    private void OnEndReached(object sender, EventArgs e)
    {
        _isPlaying = false;
        _isPaused = false;
        _currentlyPlayingUrl = null;

        if (_autoAdvanceQueue || _videoQueue.Count > 0)
        {
            Main.QueueMainThreadAction(() =>
            {
                try
                {
                    ClearVideoTexture();
                    PlayNextInQueue();
                }
                catch (Exception ex)
                {
                    TerraVision.instance.Logger.Error($"Error advancing queue: {ex.Message}");
                }
            });
        }
        else
        {
            _autoAdvanceQueue = false;
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnError(object sender, EventArgs e)
    {
        // Attempt CDN failover before declaring playback dead.
        // Bilibili streams can fail mid-play when the CDN node drops the TLS session.
        // The API returns backup CDN URLs — try them in order before giving up.
        if (_backupUrls.Count > 0 && _backupUrlIndex < _backupUrls.Count && _currentStreamResult != null)
        {
            string backupUrl = _backupUrls[_backupUrlIndex++];
            TerraVision.instance.Logger.Warn(
                $"[Failover] Primary stream failed, trying backup URL {_backupUrlIndex}/{_backupUrls.Count}");

            var failoverResult = new VideoStreamResult
            {
                VideoUrl = backupUrl,
                AudioUrl = _currentStreamResult.AudioUrl,
                IsLivestream = _currentStreamResult.IsLivestream
            };
            foreach (var kv in _currentStreamResult.HttpHeaders)
                failoverResult.HttpHeaders[kv.Key] = kv.Value;
            // Remove backup URL list from the failover result — we manage iteration ourselves
            failoverResult.HttpHeaders.Remove("X-Backup-Urls");

            Main.QueueMainThreadAction(() =>
            {
                try
                {
                    _currentMedia?.Dispose();
                    _currentMedia = BuildMedia(failoverResult);
                    _mediaPlayer.Play(_currentMedia);
                    TerraVision.instance.Logger.Info("[Failover] Switched to backup CDN URL");
                }
                catch (Exception ex)
                {
                    TerraVision.instance.Logger.Error($"[Failover] Failed to switch to backup URL: {ex.Message}");
                    _isPlaying = false;
                    _isPreparing = false;
                    PlaybackError?.Invoke(this, EventArgs.Empty);
                }
            });
            return;
        }

        _isPlaying = false;
        _isPreparing = false;
        PlaybackError?.Invoke(this, EventArgs.Empty);
    }

    private void SetLoadingState(bool loading)
    {
        if (_isLoading != loading)
        {
            _isLoading = loading;
            LoadingStateChanged?.Invoke(this, loading);
        }
    }

    #endregion

    #region Update and Texture Management

    public void Update(GameTime gameTime)
    {
        if (!_isInitialized && TerraVision.instance != null && TerraVision.LibVLCInstance != null)
        {
            Task.Run(() => EnsureInitializedAsync());
        }

        if (_isLoading)
        {
            _loadingRotation += 0.1f;
        }

        if (!_isInitialized || _frameHandler == null) return;

        double currentTime = gameTime.TotalGameTime.TotalSeconds;
        if (currentTime - _lastFrameTime < TARGET_FRAME_TIME)
            return;

        _lastFrameTime = currentTime;

        if (!_isPlaying && !_isPaused) return;

        if (!_isPaused)
            _danmakuTimer += 1 / 60f;

        if (_frameHandler.HasNewFrame())
        {
            _textureNeedsUpdate = true;
        }

        if (_textureNeedsUpdate)
        {
            byte[] frameData = _frameHandler.GetLatestFrame(out int stride);
            if (frameData != null)
            {
                UpdateTexture(frameData, stride);
                _textureNeedsUpdate = false;
            }
        }
    }

    private void UpdateTexture(byte[] rgbaData, int stride)
    {
        if (_videoTexture == null || _videoTexture.Width != _videoWidth || _videoTexture.Height != _videoHeight)
        {
            if (_videoTexture != null)
            {
                _texturePool.ReturnTexture(_videoTexture);
            }
            _videoTexture = _texturePool.GetTexture(_videoWidth, _videoHeight);
        }

        try
        {
            _videoTexture.SetData(rgbaData);
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Texture update failed: {ex}");
        }
    }

    #endregion

    #region Rendering

    /// <summary>
    /// Draw overload for UI elements — takes a Rectangle.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle targetRect, Texture2D pixel, Color backgroundColor = default)
    {
        if (backgroundColor != default)
            spriteBatch.Draw(pixel, targetRect, backgroundColor);

        DrawCore(spriteBatch, pixel, targetRect.Location.ToVector2(), new Vector2(targetRect.Width, targetRect.Height));
    }

    /// <summary>
    /// Draw overload for world-space tile entities — takes Vector2 position and size.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Vector2 position, Vector2 size, Texture2D pixel)
    {
        DrawCore(spriteBatch, pixel, position, size);
    }

    private void DrawCore(SpriteBatch spriteBatch, Texture2D pixel, Vector2 position, Vector2 size)
    {
        Rectangle targetRect = new((int)position.X, (int)position.Y, (int)size.X, (int)size.Y);
        Rectangle videoRect = CalculateRenderRectangle(targetRect);

        if (_videoTexture != null && (_isPlaying || _isPaused))
        {
            try
            {
                // Maintain aspect ratio within the target area               
                spriteBatch.Draw(_videoTexture, videoRect, Color.White);
            }
            catch (Exception ex)
            {
                TerraVision.instance.Logger.Error($"Error drawing video texture: {ex.Message}");
            }
        }

        if (_isLoading || _isPreparing)
        {
            Vector2 center = position + size / 2f;
            // Scale spinner radius and dot size proportionally to the draw area
            float spinnerRadius = Math.Min(size.X, size.Y) * 0.07f;
            float dotSize = Math.Max(2f, spinnerRadius * 0.35f);
            DrawLoadingSpinner(spriteBatch, pixel, center, spinnerRadius, dotSize);
        }

        if (!_isPlaying && !_isPaused)
            return;

        // Danmaku overlay
        _danmakuRenderer.Draw(spriteBatch, videoRect, _danmakuTimer);

        // Caption overlay
        _captionRenderer.Draw(spriteBatch, videoRect, GetTimeSeconds(), pixel);
    }

    private void DrawLoadingSpinner(SpriteBatch spriteBatch, Texture2D pixel, Vector2 center, float radius, float dotSize)
    {
        try
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = _loadingRotation + (i * MathHelper.TwoPi / 8f);
                float alpha = 0.3f + (0.7f * (i / 8f));
                Vector2 offset = new(
                    (float)Math.Cos(angle) * radius,
                    (float)Math.Sin(angle) * radius
                );

                int d = (int)dotSize;
                Rectangle dotRect = new Rectangle(
                    (int)(center.X + offset.X - d / 2),
                    (int)(center.Y + offset.Y - d / 2),
                    d, d
                );

                spriteBatch.Draw(pixel, dotRect, Color.White * alpha);
            }
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Error drawing loading spinner: {ex.Message}");
        }
    }

    public Rectangle CalculateRenderRectangle(Rectangle containerRect)
    {
        if (!_maintainAspectRatio)
        {
            return containerRect;
        }

        float videoAspect = (float)_videoWidth / _videoHeight;
        float containerAspect = (float)containerRect.Width / containerRect.Height;

        int drawWidth, drawHeight;

        if (containerAspect > videoAspect)
        {
            drawHeight = containerRect.Height;
            drawWidth = (int)(drawHeight * videoAspect);
        }
        else
        {
            drawWidth = containerRect.Width;
            drawHeight = (int)(drawWidth / videoAspect);
        }

        int x = containerRect.X + (containerRect.Width - drawWidth) / 2;
        int y = containerRect.Y + (containerRect.Height - drawHeight) / 2;

        return new Rectangle(x, y, drawWidth, drawHeight);
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ClearVideoTexture()
    {
        if (_videoTexture != null)
        {
            _texturePool.ReturnTexture(_videoTexture);
            _videoTexture = null;
        }

        _frameHandler?.ClearBuffers();
        _currentMedia?.Dispose();
        _currentMedia = null;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        ModContent.GetInstance<TerraVision>().Logger.Info($"Disposing VideoPlayerCore (session {_sessionId})");

        if (disposing)
        {
            VideoUrlHelper.CancelRequest(_currentRequestId);

            if (_mediaPlayer != null)
            {
                // Unsubscribe events before disposing to prevent callbacks firing during teardown
                _mediaPlayer.Playing -= OnPlaying;
                _mediaPlayer.Paused -= OnPaused;
                _mediaPlayer.Stopped -= OnStopped;
                _mediaPlayer.EndReached -= OnEndReached;
                _mediaPlayer.EncounteredError -= OnError;

                try
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Dispose();
                }
                catch (Exception ex)
                {
                    TerraVision.instance.Logger.Error($"Error disposing media player: {ex.Message}");
                }
                _mediaPlayer = null;
            }

            _currentMedia?.Dispose();
            _currentMedia = null;

            _frameHandler?.Dispose();
            _frameHandler = null;

            if (_videoTexture != null)
            {
                _texturePool.ReturnTexture(_videoTexture);
                _videoTexture = null;
            }
        }

        _sessionId = Guid.Empty;
        _isDisposed = true;
        _isInitialized = false;
        _isPlaying = false;
        _isPaused = false;
        _currentVideoPath = null;

        ModContent.GetInstance<TerraVision>().Logger.Info("VideoPlayerCore disposed successfully.");
    }

    #endregion
}

public class VideoFrameHandler : IDisposable
{
    private byte[] _currentFrame;
    private byte[] _nextFrame;
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameSize;
    private bool _hasNewFrame;
    private readonly object _frameLock = new object();
    private GCHandle _frameHandle;

    private byte[] _conversionBuffer;

    public VideoFrameHandler(int width, int height)
    {
        _width = width;
        _height = height;
        _frameSize = width * height * 4;
        _currentFrame = new byte[_frameSize];
        _nextFrame = new byte[_frameSize];
        _conversionBuffer = new byte[_frameSize];
    }

    public void SetupMediaPlayer(MediaPlayer mediaPlayer)
    {
        mediaPlayer.SetVideoFormat("RGBA", (uint)_width, (uint)_height, (uint)(_width * 4));
        mediaPlayer.SetVideoCallbacks(LockCallback, null, DisplayCallback);
    }

    private nint LockCallback(nint opaque, nint planes)
    {
        // Free any handle that wasn't released by a previous DisplayCallback
        // (can happen if VLC drops a frame mid-decode)
        if (_frameHandle.IsAllocated)
            _frameHandle.Free();

        _frameHandle = GCHandle.Alloc(_nextFrame, GCHandleType.Pinned);
        nint ptr = _frameHandle.AddrOfPinnedObject();
        Marshal.WriteIntPtr(planes, ptr);
        return ptr;
    }

    private void DisplayCallback(nint opaque, nint picture)
    {
        lock (_frameLock)
        {
            (_nextFrame, _currentFrame) = (_currentFrame, _nextFrame);
            _hasNewFrame = true;
        }

        if (_frameHandle.IsAllocated)
        {
            _frameHandle.Free();
        }
    }

    public bool HasNewFrame()
    {
        lock (_frameLock)
        {
            return _hasNewFrame;
        }
    }

    public byte[] GetLatestFrame(out int stride)
    {
        lock (_frameLock)
        {
            if (!_hasNewFrame)
            {
                stride = 0;
                return null;
            }

            _hasNewFrame = false;
            stride = _width * 4;

            Buffer.BlockCopy(_currentFrame, 0, _conversionBuffer, 0, _frameSize);
            return _conversionBuffer;
        }
    }

    public void ClearBuffers()
    {
        lock (_frameLock)
        {
            Array.Clear(_currentFrame, 0, _frameSize);
            Array.Clear(_nextFrame, 0, _frameSize);
            Array.Clear(_conversionBuffer, 0, _frameSize);
            _hasNewFrame = false;
        }
    }

    public void Dispose()
    {
        if (_frameHandle.IsAllocated)
        {
            _frameHandle.Free();
        }
    }
}

public class TexturePool
{
    private readonly System.Collections.Generic.Dictionary<(int, int), System.Collections.Generic.Queue<Texture2D>> _pool = new();
    private const int MAX_POOL_SIZE = 3;

    public Texture2D GetTexture(int width, int height)
    {
        var key = (width, height);

        if (_pool.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            return queue.Dequeue();
        }

        return new Texture2D(Main.graphics.GraphicsDevice, width, height);
    }

    public void ReturnTexture(Texture2D texture)
    {
        if (texture == null || texture.IsDisposed) return;

        var key = (texture.Width, texture.Height);

        if (!_pool.ContainsKey(key))
        {
            _pool[key] = new System.Collections.Generic.Queue<Texture2D>();
        }

        if (_pool[key].Count < MAX_POOL_SIZE)
        {
            _pool[key].Enqueue(texture);
        }
        else
        {
            texture.Dispose();
        }
    }

    public void Clear()
    {
        foreach (var queue in _pool.Values)
        {
            while (queue.Count > 0)
            {
                queue.Dequeue()?.Dispose();
            }
        }
        _pool.Clear();
    }
}