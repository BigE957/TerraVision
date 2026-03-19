using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace TerraVision.Core.VideoPlayer.Captions;

/// <summary>
/// A single word with its precise display timestamp.
/// </summary>
public class CaptionWord
{
    public string Text { get; init; }
    public float Timestamp { get; init; }
}

/// <summary>
/// A full caption block — contains word-level timing for the current
/// sentence build-up, and the completed previous sentence for the top line.
/// </summary>
public class CaptionBlock
{
    /// <summary>When this block starts (timestamp of the first word).</summary>
    public float StartTime { get; init; }

    /// <summary>When this block ends (start of the next block, or last word + buffer).</summary>
    public float EndTime { get; set; }

    /// <summary>Words with individual timestamps for the bottom progressive line.</summary>
    public List<CaptionWord> Words { get; init; } = [];

    /// <summary>
    /// The completed previous sentence shown statically on the top line.
    /// Null if this is the first block or follows a pause in speech.
    /// </summary>
    public string PreviousSentence { get; init; }

    /// <summary>
    /// The complete text of the sentence being built — used for stable width
    /// calculation so the caption box doesn't shift as words are revealed.
    /// </summary>
    public string FullSentence { get; set; }
}

public static partial class CaptionFetcher
{
    // -------------------------------------------------------------------------
    // Compile-time generated regexes (SYSLIB1045)
    // -------------------------------------------------------------------------

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex InlineTagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"(<(?<ts>\d+:\d{2}:\d{2}\.\d{3})>)?(?:<c>)?(?<word>[^<]+?)(?:</c>)?(?=<|$)")]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(@"<c>|<\d+:\d{2}:\d{2}\.\d{3}>")]
    private static partial Regex WordLineMarkerRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex SrtIndexRegex();

    [GeneratedRegex(@"(\d+):(\d{2}):(\d{2})[,.](\d{3})")]
    private static partial Regex SrtTimestampRegex();

    // -------------------------------------------------------------------------
    // Unified public entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches captions for any supported URL.
    /// Automatically detects the platform and uses the appropriate strategy:
    ///   YouTube  — VTT with word-level timing (progressive word reveal)
    ///   Bilibili — SRT via yt-dlp with browser cookie auth (block reveal)
    ///   Vimeo    — VTT (block reveal, no word timing)
    ///   Other    — Generic yt-dlp attempt, VTT preferred then SRT
    /// Returns an empty list if no captions are available.
    /// </summary>
    public static async Task<List<CaptionBlock>> FetchAsync(
        string url, string ytdlpPath, CancellationToken token = default)
    {
        if (url.Contains("youtube.com") || url.Contains("youtu.be"))
            return await FetchYouTubeAsync(url, ytdlpPath, token);

        if (url.Contains("bilibili.com"))
            return await FetchBilibiliAsync(url, ytdlpPath, token);

        if (url.Contains("vimeo.com"))
            return await FetchVimeoAsync(url, ytdlpPath, token);

        return await FetchGenericAsync(url, ytdlpPath, token);
    }

    // -------------------------------------------------------------------------
    // YouTube (unchanged — word-level VTT)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches captions for a YouTube URL using yt-dlp.
    /// Tries manual captions first, then auto-generated.
    /// Returns an empty list if no captions are available.
    /// </summary>
    public static async Task<List<CaptionBlock>> FetchYouTubeAsync(
        string url, string ytdlpPath, CancellationToken token = default)
    {
        try
        {
            string langCode = GetLanguageCode();
            TerraVision.instance.Logger.Info(
                $"[Captions/YouTube] Fetching for {url} (lang: {langCode})");

            string vttPath = await DownloadYouTubeSubtitleAsync(url, ytdlpPath, langCode, token);

            if (vttPath == null)
            {
                TerraVision.instance.Logger.Info("[Captions/YouTube] No captions found");
                return [];
            }

            TerraVision.instance.Logger.Info($"[Captions/YouTube] Parsing {Path.GetFileName(vttPath)}");
            var blocks = ParseYouTubeVtt(vttPath);
            TerraVision.instance.Logger.Info($"[Captions/YouTube] Loaded {blocks.Count} blocks");
            return blocks;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"[Captions/YouTube] Fetch failed: {ex.Message}");
            return [];
        }
    }

    // -------------------------------------------------------------------------
    // Bilibili — SRT with browser cookie auth
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches captions for a Bilibili URL.
    /// Bilibili subtitles require a logged-in session, so yt-dlp is invoked
    /// with --cookies-from-browser (or --cookies path) per the user's config.
    /// Falls back through browsers in Auto mode, then gives up gracefully.
    /// Output is SRT (yt-dlp converts Bilibili's JSON format automatically).
    /// </summary>
    private static async Task<List<CaptionBlock>> FetchBilibiliAsync(string url, string ytdlpPath, CancellationToken token = default)
    {
        try
        {
            // Bilibili content is predominantly Chinese; always include zh-CN as a fallback
            // even for English-language Terraria installs so users get something useful.
            // AI-generated subtitles use the codes ai-zh / ai-en — these are distinct from
            // zh-CN/zh and must be listed explicitly or the filter misses them entirely.
            string primaryLang = GetLanguageCode();
            string zhVariants = "ai-zh,zh-CN,zh";
            string langCode = primaryLang.Contains("zh")
                ? $"{primaryLang},{zhVariants}"
                : $"{primaryLang},ai-en,{zhVariants}";

            TerraVision.instance.Logger.Info(
                $"[Captions/Bilibili] Fetching for {url} (lang: {langCode})");

            string subPath = await DownloadBilibiliSubtitleAsync(url, ytdlpPath, langCode, token);

            if (subPath == null)
            {
                TerraVision.instance.Logger.Info(
                    "[Captions/Bilibili] No captions found — Bilibili subtitles require " +
                    "being logged in via a supported browser (see TerraVision mod settings)");
                return [];
            }

            string ext = Path.GetExtension(subPath).ToLowerInvariant();
            var blocks = ext == ".vtt" ? ParseSimpleVtt(subPath) : ParseSrt(subPath);
            TerraVision.instance.Logger.Info(
                $"[Captions/Bilibili] Loaded {blocks.Count} blocks from {ext.ToUpperInvariant()}");
            return blocks;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"[Captions/Bilibili] Fetch failed: {ex.Message}");
            return [];
        }
    }

    private static async Task<string> DownloadBilibiliSubtitleAsync(
        string url, string ytdlpPath, string langCode, CancellationToken token)
    {
        string tempDir = GetTempDir();

        // Bilibili uses --write-sub (not --write-auto-sub) for all subtitle types.
        // --convert-subs srt ensures consistent format regardless of yt-dlp's internal
        // representation (Bilibili's native format is JSON).
        string baseArgs =
            $"--skip-download --write-sub --convert-subs srt " +
            $"--sub-langs \"{langCode}\" " +
            $"-o \"{Path.Combine(tempDir, "%(id)s.%(ext)s")}\" " +
            $"--no-playlist -- \"{url}\"";

        var config = ModContent.GetInstance<TerraVisionConfig>();

        // An explicit cookies.txt path takes priority over browser detection
        if (!string.IsNullOrWhiteSpace(config?.CookiesFilePath))
        {
            TerraVision.instance.Logger.Debug(
                "[Captions/Bilibili] Using cookies.txt path from config");

            string cookieFlag = $"--cookies \"{config.CookiesFilePath}\"";
            await LogAvailableSubsAsync(ytdlpPath, cookieFlag, url, token);

            ClearSubtitleFiles(tempDir);
            try
            {
                await RunYtDlpAsync(
                    ytdlpPath, $"{cookieFlag} {baseArgs}", token);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            return FindSubtitleFile(tempDir);
        }

        // Browser cookie detection
        BrowserCookieSource browser = config?.BrowserForCookies ?? BrowserCookieSource.Auto;

        if (browser == BrowserCookieSource.None)
        {
            // Attempt without any auth — will almost certainly return nothing for Bilibili,
            // but worth one try in case the video has public subtitles in the future.
            TerraVision.instance.Logger.Debug(
                "[Captions/Bilibili] Cookie auth disabled in config — attempting unauthenticated");
            ClearSubtitleFiles(tempDir);
            try { await RunYtDlpAsync(ytdlpPath, baseArgs, token); }
            catch (OperationCanceledException) { throw; }
            catch { }
            return FindSubtitleFile(tempDir);
        }

        // Determine which browsers to try.
        // Each entry is resolved via ResolveBrowserCookieArg, which handles the
        // Opera GX special case (explicit profile path instead of bare browser name).
        BrowserCookieSource[] browsersToTry = browser == BrowserCookieSource.Auto
            ? [BrowserCookieSource.Chrome, BrowserCookieSource.Firefox, BrowserCookieSource.Edge]
            : [browser];

        foreach (BrowserCookieSource b in browsersToTry)
        {
            token.ThrowIfCancellationRequested();

            string cookieArg = ResolveBrowserCookieArg(b);
            TerraVision.instance.Logger.Debug(
                $"[Captions/Bilibili] Trying --cookies-from-browser {cookieArg}");

            ClearSubtitleFiles(tempDir);
            string stderr = null;
            try
            {
                stderr = await RunYtDlpCaptureStderrAsync(
                    ytdlpPath, $"--cookies-from-browser {cookieArg} {baseArgs}", token);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            string found = FindSubtitleFile(tempDir);
            if (found != null)
            {
                TerraVision.instance.Logger.Info(
                    $"[Captions/Bilibili] Got subtitles via {b} cookies");
                return found;
            }

            // Detect the Chromium database-lock error and guide the user to the
            // /tvsetup cookies command, which copies the cookies file while the
            // browser is closed and saves it persistently for future use.
            if (stderr != null && stderr.Contains("Could not copy") &&
                stderr.Contains("cookie database"))
            {
                TerraVision.instance.Logger.Warn(
                    $"[Captions/Bilibili] {b} cookie database is locked (browser is open).");

                Main.QueueMainThreadAction(() =>
                    Main.NewText(
                        "Bilibili captions: type  /tvsetup cookies  in chat to save your cookies. " +
                        $"You may need to close {b} first.",
                        Microsoft.Xna.Framework.Color.Orange));

                // No point trying further browsers if we hit a lock — this is a
                // per-machine issue, not a browser-selection issue.
                return null;
            }

            TerraVision.instance.Logger.Debug(
                $"[Captions/Bilibili] No subtitles from {b}, trying next...");
        }

        return null;
    }

    /// <summary>
    /// Resolves a BrowserCookieSource to the argument string for --cookies-from-browser.
    ///
    /// Most browsers use their lowercase name. Opera GX requires the "opera:path" syntax
    /// because yt-dlp only auto-detects regular Opera (Opera Stable) — it doesn't know
    /// about the GX profile directory. We construct the path using SpecialFolder.ApplicationData
    /// so it works regardless of Windows username.
    /// </summary>
    private static string ResolveBrowserCookieArg(BrowserCookieSource browser)
    {
        if (browser == BrowserCookieSource.OperaGX)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string gxPath = Path.Combine(appData, "Opera Software", "Opera GX Stable");
            return $"opera:\"{gxPath}\"";
        }

        return browser.ToString().ToLowerInvariant();
    }

    // -------------------------------------------------------------------------
    // Vimeo — plain VTT (user-uploaded, no word timing)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches captions for a Vimeo URL.
    /// Vimeo subtitles are user-uploaded standard VTT — no auto-generated option.
    /// No login is required for public content.
    /// </summary>
    private static async Task<List<CaptionBlock>> FetchVimeoAsync(
        string url, string ytdlpPath, CancellationToken token = default)
    {
        try
        {
            string langCode = GetLanguageCode();
            TerraVision.instance.Logger.Info(
                $"[Captions/Vimeo] Fetching for {url} (lang: {langCode})");

            string tempDir = GetTempDir();
            string args =
                $"--skip-download --write-sub --sub-format vtt/srt/best " +
                $"--sub-langs \"{langCode}\" " +
                $"-o \"{Path.Combine(tempDir, "%(id)s.%(ext)s")}\" " +
                $"--no-playlist -- \"{url}\"";

            ClearSubtitleFiles(tempDir);
            try { await RunYtDlpAsync(ytdlpPath, args, token); }
            catch (OperationCanceledException) { throw; }
            catch { }

            string subFile = FindSubtitleFile(tempDir);
            if (subFile == null)
            {
                TerraVision.instance.Logger.Info("[Captions/Vimeo] No captions found");
                return [];
            }

            string ext = Path.GetExtension(subFile).ToLowerInvariant();
            var blocks = ext == ".vtt" ? ParseSimpleVtt(subFile) : ParseSrt(subFile);
            TerraVision.instance.Logger.Info(
                $"[Captions/Vimeo] Loaded {blocks.Count} blocks");
            return blocks;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"[Captions/Vimeo] Fetch failed: {ex.Message}");
            return [];
        }
    }

    // -------------------------------------------------------------------------
    // Generic fallback — any yt-dlp-supported site
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generic caption fetch for any yt-dlp-supported URL that isn't
    /// YouTube, Bilibili, or Vimeo. No auth, VTT preferred over SRT.
    /// Low hit rate in practice but adds no complexity for the caller.
    /// </summary>
    private static async Task<List<CaptionBlock>> FetchGenericAsync(
        string url, string ytdlpPath, CancellationToken token = default)
    {
        try
        {
            string langCode = GetLanguageCode();
            TerraVision.instance.Logger.Info(
                $"[Captions/Generic] Attempting fetch for {url} (lang: {langCode})");

            string tempDir = GetTempDir();
            string[] attempts =
            [
                // Manual subs first
                $"--skip-download --write-sub --sub-format vtt/srt/best " +
                $"--sub-langs \"{langCode}\" " +
                $"-o \"{Path.Combine(tempDir, "%(id)s.%(ext)s")}\" " +
                $"--no-playlist -- \"{url}\"",

                // Auto-generated fallback
                $"--skip-download --write-auto-sub --sub-format vtt/srt/best " +
                $"--sub-langs \"{langCode}\" " +
                $"-o \"{Path.Combine(tempDir, "%(id)s.%(ext)s")}\" " +
                $"--no-playlist -- \"{url}\""
            ];

            foreach (string attempt in attempts)
            {
                token.ThrowIfCancellationRequested();
                ClearSubtitleFiles(tempDir);
                try { await RunYtDlpAsync(ytdlpPath, attempt, token); }
                catch (OperationCanceledException) { throw; }
                catch { }

                string found = FindSubtitleFile(tempDir);
                if (found != null)
                {
                    string ext = Path.GetExtension(found).ToLowerInvariant();
                    var blocks = ext == ".vtt" ? ParseSimpleVtt(found) : ParseSrt(found);
                    TerraVision.instance.Logger.Info(
                        $"[Captions/Generic] Loaded {blocks.Count} blocks");
                    return blocks;
                }
            }

            TerraVision.instance.Logger.Info("[Captions/Generic] No captions found");
            return [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"[Captions/Generic] Fetch failed: {ex.Message}");
            return [];
        }
    }

    // -------------------------------------------------------------------------
    // YouTube subtitle download helper
    // -------------------------------------------------------------------------

    private static async Task<string> DownloadYouTubeSubtitleAsync(
        string url, string ytdlpPath, string langCode, CancellationToken token)
    {
        string tempDir = GetTempDir();
        string outputTemplate = Path.Combine(tempDir, "%(id)s.%(ext)s");

        string[] attempts =
        {
            $"--skip-download --write-sub --sub-format vtt " +
            $"--sub-langs \"{langCode}\" " +
            $"-o \"{outputTemplate}\" --no-playlist -- \"{url}\"",

            $"--skip-download --write-auto-sub --sub-format vtt " +
            $"--sub-langs \"{langCode}\" " +
            $"-o \"{outputTemplate}\" --no-playlist -- \"{url}\""
        };

        string[] labels = ["manual", "auto-generated"];

        for (int i = 0; i < attempts.Length; i++)
        {
            ClearSubtitleFiles(tempDir);
            try { await RunYtDlpAsync(ytdlpPath, attempts[i], token); }
            catch (OperationCanceledException) { throw; }
            catch { }

            string vttFile = FindVttFile(tempDir);
            if (vttFile != null)
            {
                TerraVision.instance.Logger.Info(
                    $"[Captions/YouTube] Found {labels[i]} captions: {Path.GetFileName(vttFile)}");
                return vttFile;
            }

            TerraVision.instance.Logger.Debug(
                $"[Captions/YouTube] No {labels[i]} captions found, trying next...");
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Language code helper
    // -------------------------------------------------------------------------

    private static string GetLanguageCode() =>
        Terraria.Localization.Language.ActiveCulture.Name switch
        {
            "zh-Hans" => "zh-Hans,zh,zh-CN",
            "zh-Hant" => "zh-Hant,zh,zh-TW",
            "de" => "de",
            "it" => "it",
            "fr" => "fr",
            "es" => "es",
            "ru" => "ru",
            "pt-BR" => "pt-BR,pt",
            "pl" => "pl",
            _ => "en"
        };

    // -------------------------------------------------------------------------
    // File helpers
    // -------------------------------------------------------------------------

    private static string GetTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "TerraVision_Captions");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void ClearSubtitleFiles(string directory)
    {
        foreach (string pattern in new[] { "*.vtt", "*.srt" })
            foreach (string f in Directory.GetFiles(directory, pattern))
                try { File.Delete(f); } catch { }
    }

    private static string FindVttFile(string directory)
    {
        var files = Directory.GetFiles(directory, "*.vtt");
        return files.Length > 0 ? files[0] : null;
    }

    private static string FindSrtFile(string directory)
    {
        var files = Directory.GetFiles(directory, "*.srt");
        return files.Length > 0 ? files[0] : null;
    }

    /// <summary>Returns the first subtitle file found, VTT preferred over SRT.</summary>
    private static string FindSubtitleFile(string directory)
        => FindVttFile(directory) ?? FindSrtFile(directory);

    // -------------------------------------------------------------------------
    // Parser: YouTube VTT (word-level timing, rolling two-line window)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses a YouTube auto-generated WebVTT file into CaptionBlocks.
    ///
    /// YouTube's auto-generated VTT uses a rolling two-line window:
    ///   - Short cues (duration &lt; 100ms): clean completed sentence — skipped,
    ///     since PreviousSentence is read directly from long cue line 0.
    ///   - Long cues: line 0 = previous sentence (or blank line after a pause),
    ///                remaining lines = current sentence with per-word timestamps.
    /// </summary>
    private static List<CaptionBlock> ParseYouTubeVtt(string filePath)
    {
        string[] lines = File.ReadAllLines(filePath, System.Text.Encoding.UTF8);
        var longCues = new List<(float Start, float End, string PrevLine, string WordText)>();

        int i = 0;
        while (i < lines.Length && !lines[i].StartsWith("WEBVTT"))
            i++;
        i++;

        while (i < lines.Length)
        {
            string line = lines[i].Trim();

            if (string.IsNullOrEmpty(line) || line.StartsWith("NOTE") ||
                line.StartsWith("STYLE") || line.StartsWith("REGION"))
            { i++; continue; }

            if (!line.Contains("-->"))
            { i++; continue; }

            string[] arrow = line.Split(["-->"], 2, StringSplitOptions.None);
            if (arrow.Length < 2) { i++; continue; }

            float start = ParseVttTime(arrow[0].Trim().Split(' ')[0]);
            float end = ParseVttTime(arrow[1].Trim().Split(' ')[0]);
            i++;

            // CRITICAL: check raw line before trimming — a single space signals
            // a post-pause cue with no previous sentence. Trimming first collapses
            // it to "" which looks like a cue separator, losing those sentences.
            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrEmpty(lines[i]))
            {
                textLines.Add(lines[i].Trim());
                i++;
            }

            if (textLines.Count == 0)
                continue;

            // Skip short transitional cues (< 100ms)
            if ((end - start) < 0.1f)
                continue;

            string firstLine = textLines[0];
            string prevLine;
            string wordText;

            if (string.IsNullOrEmpty(firstLine))
            {
                prevLine = null;
                wordText = string.Join(" ", textLines.Skip(1));
            }
            else if (WordLineMarkerRegex().IsMatch(firstLine))
            {
                prevLine = null;
                wordText = firstLine;
            }
            else
            {
                string cleaned = InlineTagRegex().Replace(firstLine, "").Trim();
                cleaned = cleaned.Replace(">>", "").Trim();
                cleaned = MultiSpaceRegex().Replace(cleaned, " ");
                prevLine = string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
                wordText = string.Join(" ", textLines.Skip(1));
            }

            if (!string.IsNullOrWhiteSpace(wordText))
                longCues.Add((start, end, prevLine, wordText));
        }

        var blocks = new List<CaptionBlock>(longCues.Count);

        foreach (var (start, end, prevLine, wordText) in longCues)
        {
            var words = ParseWords(wordText, start);
            if (words.Count == 0)
                continue;

            blocks.Add(new CaptionBlock
            {
                StartTime = words[0].Timestamp,
                EndTime = end,
                Words = words,
                PreviousSentence = prevLine
            });
        }

        for (int b = 0; b < blocks.Count - 1; b++)
            blocks[b].FullSentence = blocks[b + 1].PreviousSentence;

        if (blocks.Count > 0)
        {
            var last = blocks[^1];
            last.FullSentence = string.Join(" ", last.Words.Select(w => w.Text));
        }

        for (int b = 0; b < blocks.Count - 1; b++)
            blocks[b].EndTime = blocks[b + 1].StartTime;

        if (blocks.Count > 0)
        {
            var last = blocks[^1];
            if (last.Words.Count > 0)
                last.EndTime = last.Words[^1].Timestamp + 2f;
        }

        return blocks;
    }

    // -------------------------------------------------------------------------
    // Parser: plain VTT (Vimeo, generic — no word-level timing)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses a standard single-cue WebVTT file (Vimeo, generic sites) into CaptionBlocks.
    /// Unlike YouTube's rolling-window format, each VTT cue here is a standalone subtitle.
    /// Since there are no word timestamps, all words in a block appear simultaneously
    /// (every word gets Timestamp = StartTime). The previous block's text is shown on
    /// the top line to give the same two-line display as YouTube captions.
    /// </summary>
    private static List<CaptionBlock> ParseSimpleVtt(string filePath)
    {
        string[] lines = File.ReadAllLines(filePath, System.Text.Encoding.UTF8);
        var cues = new List<(float Start, float End, string Text)>();

        int i = 0;
        while (i < lines.Length && !lines[i].StartsWith("WEBVTT"))
            i++;
        i++;

        while (i < lines.Length)
        {
            string line = lines[i].Trim();

            if (string.IsNullOrEmpty(line) || line.StartsWith("NOTE") ||
                line.StartsWith("STYLE") || line.StartsWith("REGION"))
            { i++; continue; }

            if (!line.Contains("-->"))
            { i++; continue; }

            string[] arrow = line.Split(["-->"], 2, StringSplitOptions.None);
            if (arrow.Length < 2) { i++; continue; }

            float start = ParseVttTime(arrow[0].Trim().Split(' ')[0]);
            float end = ParseVttTime(arrow[1].Trim().Split(' ')[0]);
            i++;

            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrEmpty(lines[i].Trim()))
            {
                string cleaned = InlineTagRegex().Replace(lines[i].Trim(), "").Trim();
                if (!string.IsNullOrEmpty(cleaned))
                    textLines.Add(cleaned);
                i++;
            }

            if (textLines.Count == 0)
                continue;

            string text = MultiSpaceRegex()
                .Replace(string.Join(" ", textLines), " ").Trim();
            if (!string.IsNullOrWhiteSpace(text))
                cues.Add((start, end, text));
        }

        return BuildBlocksFromCues(cues);
    }

    // -------------------------------------------------------------------------
    // Parser: SRT (Bilibili, Dailymotion, generic)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses a standard SRT subtitle file into CaptionBlocks.
    /// SRT has no word-level timing, so all words in a block appear simultaneously
    /// (every word gets Timestamp = StartTime). The previous block's text is shown
    /// on the top line to give the same two-line display as YouTube captions.
    /// </summary>
    private static List<CaptionBlock> ParseSrt(string filePath)
    {
        string[] lines = File.ReadAllLines(filePath, System.Text.Encoding.UTF8);
        var cues = new List<(float Start, float End, string Text)>();

        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i].Trim();

            // Skip bare index lines
            if (SrtIndexRegex().IsMatch(line))
            { i++; continue; }

            // Timestamp line: 00:00:01,000 --> 00:00:04,000
            if (line.Contains("-->"))
            {
                string[] arrow = line.Split(["-->"], 2, StringSplitOptions.None);
                if (arrow.Length < 2) { i++; continue; }

                float start = ParseSrtTime(arrow[0].Trim());
                float end = ParseSrtTime(arrow[1].Trim());
                i++;

                var textLines = new List<string>();
                while (i < lines.Length && !string.IsNullOrEmpty(lines[i].Trim()))
                {
                    // Strip SRT formatting tags (<i>, <b>, <font ...>, etc.)
                    string cleaned = InlineTagRegex().Replace(lines[i].Trim(), "").Trim();
                    if (!string.IsNullOrEmpty(cleaned))
                        textLines.Add(cleaned);
                    i++;
                }

                if (textLines.Count == 0)
                    continue;

                string text = MultiSpaceRegex()
                    .Replace(string.Join(" ", textLines), " ").Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    cues.Add((start, end, text));

                continue;
            }

            i++;
        }

        return BuildBlocksFromCues(cues);
    }

    // -------------------------------------------------------------------------
    // Shared block builder for non-YouTube formats
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts a flat list of (start, end, text) cues into CaptionBlocks.
    /// All words in each block get Timestamp = StartTime (instant reveal).
    /// The previous cue's text is stored as PreviousSentence for two-line display.
    /// </summary>
    private static List<CaptionBlock> BuildBlocksFromCues(
        List<(float Start, float End, string Text)> cues)
    {
        var blocks = new List<CaptionBlock>(cues.Count);

        for (int i = 0; i < cues.Count; i++)
        {
            var (start, end, text) = cues[i];
            string previous = i > 0 ? cues[i - 1].Text : null;

            // All words stamped at StartTime — entire line appears at once
            var words = text
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => new CaptionWord { Text = w, Timestamp = start })
                .ToList();

            blocks.Add(new CaptionBlock
            {
                StartTime = start,
                EndTime = end,
                Words = words,
                PreviousSentence = previous,
                FullSentence = text
            });
        }

        return blocks;
    }

    // -------------------------------------------------------------------------
    // YouTube word-level parsing helpers
    // -------------------------------------------------------------------------

    private static List<CaptionWord> ParseWords(string rawText, float cueStart)
    {
        var words = new List<CaptionWord>();
        float timestamp = cueStart;

        foreach (Match match in WordTokenRegex().Matches(rawText))
        {
            string ts = match.Groups["ts"].Value;
            string word = match.Groups["word"].Value.Trim();

            if (!string.IsNullOrWhiteSpace(ts))
                timestamp = ParseVttTime(ts);

            if (!string.IsNullOrWhiteSpace(word))
                words.Add(new CaptionWord { Text = word, Timestamp = timestamp });
        }

        return words;
    }

    // -------------------------------------------------------------------------
    // Timestamp parsers
    // -------------------------------------------------------------------------

    private static float ParseVttTime(string timestamp)
    {
        string[] parts = timestamp.Replace(',', '.').Split(':');
        try
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return parts.Length == 3
                ? float.Parse(parts[0]) * 3600f
                  + float.Parse(parts[1]) * 60f
                  + float.Parse(parts[2], c)
                : float.Parse(parts[0]) * 60f
                  + float.Parse(parts[1], c);
        }
        catch { return 0f; }
    }

    private static float ParseSrtTime(string timestamp)
    {
        var m = SrtTimestampRegex().Match(timestamp);
        if (!m.Success) return 0f;
        try
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return float.Parse(m.Groups[1].Value, c) * 3600f
                 + float.Parse(m.Groups[2].Value, c) * 60f
                 + float.Parse(m.Groups[3].Value, c)
                 + float.Parse(m.Groups[4].Value, c) / 1000f;
        }
        catch { return 0f; }
    }

    // -------------------------------------------------------------------------
    // Process runner
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs a yt-dlp process and waits for it to exit.
    /// Logs all output at Debug level on non-zero exit.
    /// </summary>
    private static async Task RunYtDlpAsync(
        string ytdlpPath, string arguments, CancellationToken token)
        => await RunYtDlpCaptureStderrAsync(ytdlpPath, arguments, token);

    /// <summary>
    /// Runs yt-dlp and returns captured stderr. Also logs stdout+stderr together
    /// at Debug level on any non-zero exit so nothing is silently swallowed.
    /// </summary>
    private static async Task<string> RunYtDlpCaptureStderrAsync(
        string ytdlpPath, string arguments, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<string>();
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ytdlpPath,
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

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        process.Exited += (_, _) =>
        {
            process.WaitForExit();
            string errText = stderr.ToString().Trim();
            string outText = stdout.ToString().Trim();

            if (process.ExitCode != 0)
            {
                string combined = string.Join("\n",
                    new[] { errText, outText }.Where(s => !string.IsNullOrEmpty(s)));
                if (!string.IsNullOrEmpty(combined))
                    TerraVision.instance.Logger.Debug(
                        $"[Captions] yt-dlp exited with code {process.ExitCode}:\n{combined}");
            }

            tcs.TrySetResult(errText);
            process.Dispose();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (token.Register(() =>
        {
            try { process.Kill(); } catch { }
            tcs.TrySetCanceled();
        }))
        {
            return await tcs.Task;
        }
    }

    /// <summary>
    /// Runs yt-dlp --list-subs and logs the available subtitle tracks at Info level.
    /// This is a pure diagnostic pass — it writes nothing to disk.
    /// Lets us see in the log exactly what languages/formats Bilibili is offering,
    /// so we can tell whether the issue is "no subs", "wrong lang code", or auth.
    /// </summary>
    private static async Task LogAvailableSubsAsync(
        string ytdlpPath, string cookieFlag, string url, CancellationToken token)
    {
        try
        {
            var tcs = new TaskCompletionSource<string>();
            var output = new System.Text.StringBuilder();

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ytdlpPath,
                    Arguments = $"{cookieFlag} --list-subs --no-playlist -- \"{url}\"",
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
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.Exited += (_, _) =>
            {
                process.WaitForExit();
                tcs.TrySetResult(output.ToString().Trim());
                process.Dispose();
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            string result;
            using (token.Register(() =>
            {
                try { process.Kill(); } catch { }
                tcs.TrySetCanceled();
            }))
            {
                result = await tcs.Task;
            }

            TerraVision.instance.Logger.Info(
                $"[Captions/Bilibili] Available subtitles for {url}:\n{result}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Debug(
                $"[Captions/Bilibili] --list-subs diagnostic failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the path to the Cookies SQLite file for a given Chromium-based browser,
    /// or null if the browser isn't installed or the path can't be determined.
    /// Internal so SetupCookiesCommand can use it when saving cookies.
    /// </summary>
    internal static string ResolveCookiesFilePath(BrowserCookieSource browser)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        string profileDir = browser switch
        {
            BrowserCookieSource.Chrome => Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Network"),
            BrowserCookieSource.Edge => Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Network"),
            BrowserCookieSource.Brave => Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Network"),
            BrowserCookieSource.Opera => Path.Combine(roaming, "Opera Software", "Opera Stable", "Network"),
            BrowserCookieSource.OperaGX => Path.Combine(roaming, "Opera Software", "Opera GX Stable", "Network"),
            BrowserCookieSource.Vivaldi => Path.Combine(local, "Vivaldi", "User Data", "Default", "Network"),
            _ => null
        };

        return profileDir == null ? null : Path.Combine(profileDir, "Cookies");
    }
}