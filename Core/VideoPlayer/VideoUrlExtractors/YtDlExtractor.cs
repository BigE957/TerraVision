using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace TerraVision.Core.VideoPlayer.VideoUrlExtractors;

/// <summary>
/// Video URL extractor using yt-dlp.
/// Supports livestreams.
/// </summary>
public class YtDlExtractor : IVideoUrlExtractor
{
    private YoutubeDL _ytdl;
    private static string _ytdlpPath;
    public static string GetYtDlpPath() => _ytdlpPath;
    public string FfmpegPath => File.Exists(_ffmpegPath) ? _ffmpegPath : null;

    private string _ffmpegPath;
    private bool _isInitialized = false;

    // yt-dlp release info
    private const string YTDLP_DOWNLOAD_URL_WINDOWS = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YTDLP_DOWNLOAD_URL_LINUX = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp";
    private const string YTDLP_VERSION_CHECK_URL = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    public string Name => "yt-dlp";
    public bool SupportsLivestreams => true;
    public bool IsAvailable => _isInitialized && _ytdl != null;

    public YtDlExtractor()
    {
        // Set up path for yt-dlp binary
        string ytdlpFolder = Path.Combine(ModLoader.ModPath, "..", "yt-dlp");
        Directory.CreateDirectory(ytdlpFolder);

        _ytdlpPath = Path.Combine(ytdlpFolder, GetBinaryName());
        _ffmpegPath = Path.Combine(ytdlpFolder, GetFfmpegBinaryName());
    }

    private static string GetBinaryName() => Environment.OSVersion.Platform == PlatformID.Win32NT ? "yt-dlp.exe" : "yt-dlp";

    private static string GetFfmpegBinaryName() => Environment.OSVersion.Platform == PlatformID.Win32NT ? "ffmpeg.exe" : "ffmpeg";
    private static string BuildFormatSelector(int? maxHeight)
    {
        if (maxHeight == null)
            return "bestvideo+bestaudio/best";

        return $"bestvideo[height<={maxHeight}]+bestaudio/best[height<={maxHeight}]";
    }

    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return true;

        try
        {
            // Check if yt-dlp binary exists
            if (!File.Exists(_ytdlpPath))
            {
                TerraVision.instance.Logger.Info("yt-dlp not found, downloading...");
                bool downloaded = await DownloadYtDlpAsync();
                if (!downloaded)
                {
                    TerraVision.instance.Logger.Error("Failed to download yt-dlp");
                    return false;
                }
            }
            else
            {
                // Fire and forget — don't block initialization on an update check
                _ = Task.Run(CheckAndUpdateYtDlpAsync);
            }

            // Make executable on Linux/Mac
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                try
                {
                    var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "chmod",
                            Arguments = $"+x \"{_ytdlpPath}\"",
                            UseShellExecute = false
                        }
                    };
                    process.Start();
                    await process.WaitForExitAsync();
                }
                catch (Exception ex)
                {
                    TerraVision.instance.Logger.Warn($"Could not make yt-dlp executable: {ex.Message}");
                }
            }

            // Initialize YoutubeDL instance
            _ytdl = new()
            {
                YoutubeDLPath = _ytdlpPath,
                OutputFolder = Path.GetTempPath()
            };

            TerraVision.instance.Logger.Info($"yt-dlp initialized successfully");

            if (!File.Exists(_ffmpegPath))
            {
                TerraVision.instance.Logger.Info("ffmpeg not found, downloading...");
                bool ffmpegDownloaded = await DownloadFfmpegAsync();
                if (!ffmpegDownloaded)
                    TerraVision.instance.Logger.Warn("ffmpeg download failed — Bilibili and other DASH sites will not work");
                else
                    TerraVision.instance.Logger.Info("ffmpeg downloaded successfully");
            }
            else
            {
                _ = Task.Run(CheckAndUpdateFfmpegAsync);
            }

            _ytdl.FFmpegPath = _ffmpegPath;

            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Failed to initialize yt-dlp: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> DownloadFfmpegAsync()
    {
        try
        {
            bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

            if (isWindows)
            {
                return await DownloadFfmpegWindowsAsync();
            }
            else
            {
                // On Linux/Mac, prefer the system ffmpeg to avoid downloading a large archive.
                // If it's on PATH, just point to it directly.
                string systemFfmpeg = await FindSystemFfmpegAsync();
                if (systemFfmpeg != null)
                {
                    // Symlink or copy the system binary path into our expected location
                    // by writing a tiny shell wrapper so _ffmpegPath is a valid executable.
                    await File.WriteAllTextAsync(_ffmpegPath, $"#!/bin/sh\nexec \"{systemFfmpeg}\" \"$@\"\n");
                    var chmod = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "chmod",
                            Arguments = $"+x \"{_ffmpegPath}\"",
                            UseShellExecute = false
                        }
                    };
                    chmod.Start();
                    await chmod.WaitForExitAsync();
                    TerraVision.instance.Logger.Info($"Using system ffmpeg at: {systemFfmpeg}");
                    return true;
                }

                return await DownloadFfmpegLinuxAsync();
            }
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"ffmpeg download failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Returns the path to a system ffmpeg if one is on PATH, otherwise null.</summary>
    private static async Task<string> FindSystemFfmpegAsync()
    {
        try
        {
            var tcs = new TaskCompletionSource<string>();
            var output = new System.Text.StringBuilder();

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "ffmpeg",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.Exited += (_, _) => { process.WaitForExit(); tcs.TrySetResult(output.ToString().Trim()); process.Dispose(); };
            process.Start();
            process.BeginOutputReadLine();

            string result = await tcs.Task;
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch { return null; }
    }

    private async Task<bool> DownloadFfmpegWindowsAsync()
    {
        try
        {
            const string url = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TerraVision Mod");

            TerraVision.instance.Logger.Info("Downloading ffmpeg (Windows)...");
            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var zipBytes = await response.Content.ReadAsByteArrayAsync();
            string tempZip = Path.Combine(Path.GetTempPath(), "ffmpeg_temp.zip");
            await File.WriteAllBytesAsync(tempZip, zipBytes);

            string tempExtract = Path.Combine(Path.GetTempPath(), "ffmpeg_extract_" + Guid.NewGuid());
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract);

            string extracted = Directory.GetFiles(tempExtract, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (extracted == null)
            {
                TerraVision.instance.Logger.Error("ffmpeg.exe not found in downloaded zip");
                return false;
            }

            File.Move(extracted, _ffmpegPath, true);
            try { Directory.Delete(tempExtract, true); } catch { }
            try { File.Delete(tempZip); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"ffmpeg Windows download failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> DownloadFfmpegLinuxAsync()
    {
        try
        {
            const string url = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz";

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TerraVision Mod");

            TerraVision.instance.Logger.Info("Downloading ffmpeg (Linux)...");
            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            string tempArchive = Path.Combine(Path.GetTempPath(), "ffmpeg_temp.tar.xz");
            string tempExtract = Path.Combine(Path.GetTempPath(), "ffmpeg_extract_" + Guid.NewGuid());
            Directory.CreateDirectory(tempExtract);

            await File.WriteAllBytesAsync(tempArchive, await response.Content.ReadAsByteArrayAsync());

            // Use system tar to extract — available on all Linux/Mac systems
            var tar = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"-xf \"{tempArchive}\" -C \"{tempExtract}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            tar.Start();
            await tar.WaitForExitAsync();

            string extracted = Directory.GetFiles(tempExtract, "ffmpeg", SearchOption.AllDirectories).FirstOrDefault(f => !f.EndsWith(".so") && !Path.GetFileName(f).Contains('.'));

            if (extracted == null)
            {
                TerraVision.instance.Logger.Error("ffmpeg binary not found in downloaded archive");
                return false;
            }

            File.Move(extracted, _ffmpegPath, true);

            var chmod = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{_ffmpegPath}\"",
                    UseShellExecute = false
                }
            };
            chmod.Start();
            await chmod.WaitForExitAsync();

            try { Directory.Delete(tempExtract, true); } catch { }
            try { File.Delete(tempArchive); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"ffmpeg Linux download failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> DownloadYtDlpAsync()
    {
        try
        {
            string downloadUrl = Environment.OSVersion.Platform == PlatformID.Win32NT ? YTDLP_DOWNLOAD_URL_WINDOWS : YTDLP_DOWNLOAD_URL_LINUX;

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            TerraVision.instance.Logger.Info($"Downloading yt-dlp from {downloadUrl}");

            var response = await httpClient.GetAsync(downloadUrl);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(_ytdlpPath, bytes);

            TerraVision.instance.Logger.Info($"yt-dlp downloaded successfully to {_ytdlpPath}");
            return true;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Failed to download yt-dlp: {ex.Message}");
            return false;
        }
    }

    private async Task CheckAndUpdateYtDlpAsync()
    {
        try
        {
            // Get installed version
            string installedVersion = await GetInstalledVersionAsync(_ytdlpPath, "--version");
            if (installedVersion == null)
            {
                TerraVision.instance.Logger.Warn("Could not determine installed yt-dlp version, skipping update check");
                return;
            }

            // Get latest release tag from GitHub
            string latestVersion = await GetLatestGitHubReleaseTagAsync("yt-dlp/yt-dlp");
            if (latestVersion == null)
            {
                TerraVision.instance.Logger.Warn("Could not fetch latest yt-dlp version, skipping update check");
                return;
            }

            TerraVision.instance.Logger.Info($"yt-dlp: installed={installedVersion.Trim()}, latest={latestVersion.Trim()}");

            if (installedVersion.Trim() != latestVersion.Trim())
            {
                TerraVision.instance.Logger.Info("yt-dlp update available, downloading...");
                await DownloadYtDlpAsync();
                TerraVision.instance.Logger.Info("yt-dlp updated successfully");
            }
            else
            {
                TerraVision.instance.Logger.Info("yt-dlp is up to date");
            }
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Warn($"yt-dlp update check failed: {ex.Message}");
        }
    }

    private async Task CheckAndUpdateFfmpegAsync()
    {
        try
        {
            // ffmpeg version output looks like "ffmpeg version N-xxxxxx-gxxxxxxx"
            // We key off the build hash since yt-dlp's ffmpeg builds use git hashes
            string installedVersion = await GetInstalledVersionAsync(_ffmpegPath, "-version");
            if (installedVersion == null)
            {
                TerraVision.instance.Logger.Warn("Could not determine installed ffmpeg version, skipping update check");
                return;
            }

            // Extract the build tag from the version string
            var match = Regex.Match(installedVersion, @"version\s+(\S+)");
            string installedTag = match.Success ? match.Groups[1].Value : installedVersion.Trim();

            string latestTag = await GetLatestGitHubReleaseTagAsync("yt-dlp/FFmpeg-Builds");
            if (latestTag == null)
            {
                TerraVision.instance.Logger.Warn("Could not fetch latest ffmpeg version, skipping update check");
                return;
            }

            TerraVision.instance.Logger.Info($"ffmpeg: installed={installedTag}, latest release={latestTag}");

            // ffmpeg-builds uses a rolling 'latest' tag so we compare dates instead
            // Check if the installed file is older than 30 days as a pragmatic update trigger
            var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(_ffmpegPath);
            if (fileAge.TotalDays > 30)
            {
                TerraVision.instance.Logger.Info("ffmpeg is over 30 days old, updating...");
                await DownloadFfmpegAsync();
                TerraVision.instance.Logger.Info("ffmpeg updated successfully");
            }
            else
            {
                TerraVision.instance.Logger.Info("ffmpeg is recent enough, skipping update");
            }
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Warn($"ffmpeg update check failed: {ex.Message}");
        }
    }

    private static async Task<string> GetInstalledVersionAsync(string binaryPath, string versionArg)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var tcs = new TaskCompletionSource<string>();
            var output = new System.Text.StringBuilder();

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = binaryPath,
                    Arguments = versionArg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.Exited += (_, _) =>
            {
                process.WaitForExit();
                tcs.TrySetResult(output.ToString());
                process.Dispose();
            };

            process.Start();
            process.BeginOutputReadLine();

            using (timeoutCts.Token.Register(() =>
            {
                try { process.Kill(); } catch { }
                tcs.TrySetCanceled();
            }))
            {
                return await tcs.Task;
            }
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> GetLatestGitHubReleaseTagAsync(string repo)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TerraVision Mod");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            string json = await httpClient.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("tag_name").GetString();
        }
        catch
        {
            return null;
        }
    }

    public async Task<VideoStreamResult> GetDirectUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Warn("yt-dlp not available for GetDirectUrlAsync");
            return null;
        }

        try
        {
            var options = new OptionSet() { NoPlaylist = true };

            // Inject cookies if available — unlocks age-restricted/members content
            // and higher resolutions for Premium accounts (e.g. Bilibili 1080p+)
            string cookiesPath = CookieManager.GetFolderCookiesPath(url);
            if (cookiesPath != null)
            {
                options.Cookies = cookiesPath;
                TerraVision.instance.Logger.Debug($"[YtDlp] Using saved cookies for {CookieManager.ExtractDomain(url)}");
            }

            int? maxHeight = ModContent.GetInstance<TerraVisionConfig>()?.MaxVideoHeight();
            if (maxHeight.HasValue)
                options.Format = BuildFormatSelector(maxHeight.Value);

            var result = await _ytdl.RunVideoDataFetch(url, ct: cancellationToken, overrideOptions: options);

            if (!result.Success || result.Data == null)
            {
                TerraVision.instance.Logger.Warn($"yt-dlp failed to fetch data: {string.Join(", ", result.ErrorOutput ?? Array.Empty<string>())}");
                return null;
            }

            var streamResult = new VideoStreamResult();

            // Bilibili CDN requires Referer on every request or returns 403
            if (url.Contains("bilibili.com"))
                streamResult.HttpHeaders["Referer"] = "https://www.bilibili.com";

            // Case 1: single combined stream URL (YouTube, most other sites)
            if (!string.IsNullOrWhiteSpace(result.Data.Url))
            {
                streamResult.VideoUrl = result.Data.Url;
                TerraVision.instance.Logger.Info("yt-dlp: using top-level combined URL");
                return streamResult;
            }

            // Case 2: separate DASH streams (Bilibili and others)
            if (result.Data.Formats != null)
            {
                TerraVision.instance.Logger.Debug("Top-level URL null, selecting from DASH formats...");

                var formats = result.Data.Formats;

                // Prefer a format with both codecs in one stream
                var combined = formats
                    .Where(f => !string.IsNullOrWhiteSpace(f.Url)
                             && f.VideoCodec != "none" && !string.IsNullOrWhiteSpace(f.VideoCodec)
                             && f.AudioCodec != "none" && !string.IsNullOrWhiteSpace(f.AudioCodec)
                             && (maxHeight == null || (f.Height ?? 0) <= maxHeight))
                    .OrderByDescending(f => f.Height ?? 0)
                    .FirstOrDefault();

                if (combined != null)
                {
                    streamResult.VideoUrl = combined.Url;
                    TerraVision.instance.Logger.Info($"yt-dlp: combined DASH format {combined.FormatId} ({combined.Width}x{combined.Height})");
                    return streamResult;
                }

                // Separate video and audio tracks
                var bestVideo = formats
                    .Where(f => !string.IsNullOrWhiteSpace(f.Url)
                             && f.VideoCodec != "none" && !string.IsNullOrWhiteSpace(f.VideoCodec)
                             && (maxHeight == null || (f.Height ?? 0) <= maxHeight))
                    .OrderByDescending(f => f.Height ?? 0)
                    .FirstOrDefault();

                var bestAudio = formats
                    .Where(f => !string.IsNullOrWhiteSpace(f.Url)
                             && f.AudioCodec != "none" && !string.IsNullOrWhiteSpace(f.AudioCodec)
                             && (f.VideoCodec == "none" || string.IsNullOrWhiteSpace(f.VideoCodec)))
                    .OrderByDescending(f => f.AudioBitrate ?? 0)
                    .FirstOrDefault();

                if (bestVideo != null)
                {
                    streamResult.VideoUrl = bestVideo.Url;
                    streamResult.AudioUrl = bestAudio?.Url;
                    TerraVision.instance.Logger.Info(
                        $"yt-dlp: DASH video={bestVideo.FormatId} ({bestVideo.Width}x{bestVideo.Height})" +
                        (bestAudio != null ? $", audio={bestAudio.FormatId}" : ", NO audio track found"));
                    return streamResult;
                }
            }

            TerraVision.instance.Logger.Warn("yt-dlp: no usable URL in top-level data or formats");
            return null;
        }
        catch (OperationCanceledException)
        {
            TerraVision.instance.Logger.Info("yt-dlp URL extraction cancelled");
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"yt-dlp URL extraction failed: {ex.Message}");
            return null;
        }
    }

    public async Task<string> SearchAsync(string searchQuery, int resultIndex, int maxResults, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Warn("yt-dlp not available for SearchAsync");
            return null;
        }

        try
        {
            // Use yt-dlp to search YouTube
            var options = new OptionSet()
            {
                FlatPlaylist = true,
                SkipDownload = true
            };
            options.AddCustomOption("--get-id", "");

            // Search and get video IDs
            var result = await _ytdl.RunVideoPlaylistDownload($"ytsearch{maxResults}:{searchQuery}", ct: cancellationToken, overrideOptions: options);

            if (!result.Success || result.Data == null || result.Data.Length == 0)
            {
                TerraVision.instance.Logger.Warn("yt-dlp search returned no results");
                return null;
            }

            var videoResults = result.Data;

            // Select based on index
            int selectedIndex;
            if (resultIndex == -1)
                selectedIndex = Main.rand.Next(videoResults.Length);
            else if (resultIndex >= videoResults.Length)
                selectedIndex = videoResults.Length - 1;
            else
                selectedIndex = resultIndex;
            
            string videoIdOrUrl = videoResults[selectedIndex];

            // Check if it's already a full URL or just an ID
            string videoUrl;
            if (videoIdOrUrl.StartsWith("http"))
            {
                // It's already a URL, extract the ID
                var match = Regex.Match(videoIdOrUrl, @"[?&]v=([^&]+)");
                if (match.Success)
                    videoUrl = $"https://youtube.com/watch?v={match.Groups[1].Value}";
                else
                    videoUrl = videoIdOrUrl; // Use as-is
            }
            else // It's just an ID
                videoUrl = $"https://youtube.com/watch?v={videoIdOrUrl}";

            TerraVision.instance.Logger.Info($"yt-dlp search found video: {videoUrl}");
            return videoUrl;
        }
        catch (OperationCanceledException)
        {
            TerraVision.instance.Logger.Info("yt-dlp search cancelled");
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"yt-dlp search failed: {ex.Message}");
            return null;
        }
    }

    public async Task<List<string>> GetPlaylistVideosAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            TerraVision.instance.Logger.Warn("yt-dlp not available for GetPlaylistVideosAsync");
            return [];
        }

        try
        {
            string playlistUrl = $"https://youtube.com/playlist?list={playlistId}";

            var options = new OptionSet()
            {
                FlatPlaylist = true,
                SkipDownload = true
            };
            options.AddCustomOption("--get-id", "");

            var result = await _ytdl.RunVideoPlaylistDownload(
                playlistUrl,
                ct: cancellationToken,
                overrideOptions: options
            );

            if (!result.Success || result.Data == null || result.Data.Length == 0)
            {
                TerraVision.instance.Logger.Warn("yt-dlp playlist fetch returned no results");
                return [];
            }

            List<string> videoUrls = [];
            foreach (var item in result.Data)
            {
                string videoUrl;
                if (item.StartsWith("http"))
                {
                    // Already a URL, extract ID and normalize
                    var match = Regex.Match(item, @"[?&]v=([^&]+)");
                    if (match.Success)
                        videoUrl = $"https://youtube.com/watch?v={match.Groups[1].Value}";
                    else
                        videoUrl = item;
                }
                else // Just an ID
                    videoUrl = $"https://youtube.com/watch?v={item}";

                videoUrls.Add(videoUrl);
            }

            TerraVision.instance.Logger.Info($"yt-dlp extracted {videoUrls.Count} videos from playlist");
            return videoUrls;
        }
        catch (OperationCanceledException)
        {
            TerraVision.instance.Logger.Info("yt-dlp playlist fetch cancelled");
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"yt-dlp playlist fetch failed: {ex.Message}");
            return [];
        }
    }

    public async Task<bool> IsLivestreamAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return false;

        try
        {
            var options = new OptionSet()
            {
                DumpSingleJson = true,
                NoPlaylist = true
            };

            var result = await _ytdl.RunVideoDataFetch(url, ct: cancellationToken, overrideOptions: options);

            if (!result.Success || result.Data == null)
                return false;

            // Check if it's a live stream
            bool isLive = result.Data.IsLive ?? false;
            return isLive;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads a video (merging video+audio) to a temp file and returns the local path.
    /// Use this for sites like Bilibili where DASH streaming via VLC is unreliable.
    /// </summary>
    public async Task<string> DownloadToTempAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return null;

        string tempDir = Path.Combine(Path.GetTempPath(), "TerraVision_Download");
        Directory.CreateDirectory(tempDir);
        string outputTemplate = Path.Combine(tempDir, "%(id)s.%(ext)s");

        // Extract video ID from URL to check if already downloaded
        // Bilibili IDs look like BV1xx... or av123...
        var idMatch = Regex.Match(url, @"(BV[\w]+|av\d+)", RegexOptions.IgnoreCase);

        if (idMatch.Success)
        {
            string cachedPath = Path.Combine(tempDir, $"{idMatch.Value}.mp4");
            if (File.Exists(cachedPath))
            {
                TerraVision.instance.Logger.Info($"Using cached download for {idMatch.Value}: {cachedPath}");
                return cachedPath;
            }
        }

        try
        {
            TerraVision.instance.Logger.Info($"Downloading and merging {url} to temp file...");

            // Record timestamps of existing files so we can identify the new one
            var existingFiles = Directory.GetFiles(tempDir).ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));

            string cookieArg = CookieManager.GetFolderCookiesArg(url) ?? "";
            int? maxHeight = ModContent.GetInstance<TerraVisionConfig>()?.MaxVideoHeight();
            string args = $"{cookieArg} --no-playlist -f \"{BuildFormatSelector(maxHeight)}\" " +
                          $"--merge-output-format mp4 " +
                          $"-o \"{outputTemplate}\" " +
                          $"--print after_move:filepath " +
                          $"-- \"{url}\"";

            string output = await RunRawProcessAsync(_ytdlpPath, args, cancellationToken);

            TerraVision.instance.Logger.Debug($"yt-dlp stdout: {output.Trim()}");

            // Strategy 1: parse the filepath from --print output
            string filePath = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => !string.IsNullOrWhiteSpace(l));

            if (filePath != null && !File.Exists(filePath))
            {
                TerraVision.instance.Logger.Error($"yt-dlp reported '{filePath}' but file does not exist — ffmpeg may still be missing");
                return null;
            }

            // Strategy 2: find any file in tempDir that didn't exist before the download
            if (filePath == null)
            {
                TerraVision.instance.Logger.Debug("--print filepath parse failed, scanning output dir for new files...");
                filePath = Directory.GetFiles(tempDir, "*.mp4")
                    .Concat(Directory.GetFiles(tempDir, "*.mkv"))
                    .Where(f => !existingFiles.ContainsKey(f) || File.GetLastWriteTimeUtc(f) > existingFiles[f])
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault();
            }

            if (filePath == null)
            {
                TerraVision.instance.Logger.Error($"yt-dlp download completed but output file not found. yt-dlp output was: {output.Trim()}");
                return null;
            }

            TerraVision.instance.Logger.Info($"Downloaded to: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"yt-dlp download failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> RunRawProcessAsync(string fileName, string arguments, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<string>();
        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };
        process.Exited += (_, _) =>
        {
            process.WaitForExit();
            if (process.ExitCode == 0)
                tcs.TrySetResult(output.ToString());
            else
                tcs.TrySetException(new Exception($"yt-dlp exited with code {process.ExitCode}\n{error}"));
            process.Dispose();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (token.Register(() => { try { process.Kill(); } catch { } tcs.TrySetCanceled(); }))
            return await tcs.Task;
    }
}