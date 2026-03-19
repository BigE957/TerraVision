using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Terraria.Localization;
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

    /// <summary>
    /// When true, the block's text contains intentional newlines (e.g. ASCII art
    /// captions, multi-line plain VTT cues) that must not be reflowed.
    /// The renderer will scale the content to fit rather than word-wrapping it.
    /// </summary>
    public bool IsPreformatted { get; init; }
}

public static partial class CaptionFetcher
{
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
            TerraVision.instance.Logger.Info($"[Captions/YouTube] Fetching for {url} (lang: {langCode})");

            string vttPath = await DownloadYouTubeSubtitleAsync(url, ytdlpPath, langCode, token);

            if (vttPath == null)
            {
                TerraVision.instance.Logger.Info("[Captions/YouTube] No captions found");
                return [];
            }

            TerraVision.instance.Logger.Info($"[Captions/YouTube] Parsing {Path.GetFileName(vttPath)}");

            // YouTube's auto-generated VTT uses word-level <c> timing tags.
            // Manually uploaded captions (and special videos like ASCII art) do not —
            // they're plain single-cue VTTs. Route based on actual file content rather
            // than assuming all YouTube VTTs use the rolling-window format.
            bool hasWordTiming = VttHasWordTiming(vttPath);
            var blocks = hasWordTiming ? ParseYouTubeVtt(vttPath) : ParseSimpleVtt(vttPath);
            TerraVision.instance.Logger.Info($"[Captions/YouTube] Loaded {blocks.Count} blocks ({(hasWordTiming ? "word-timed" : "plain VTT")})");
            return blocks;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"[Captions/YouTube] Fetch failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Fetches captions for a Bilibili URL.
    /// Bilibili subtitles require a logged-in session, so yt-dlp is invoked
    /// with --cookies-from-browser (or --cookies path) per the user's config.
    /// Falls back through browsers in Auto mode, then gives up gracefully.
    /// Output is SRT (yt-dlp converts Bilibili's JSON format automatically).
    /// </summary>
    private static async Task<List<CaptionBlock>> FetchBilibiliAsync(
        string url, string ytdlpPath, CancellationToken token = default)
    {
        try
        {
            // Bilibili content is predominantly Chinese; always include zh-CN as a fallback
            // even for English-language Terraria installs so users get something useful.
            // AI-generated subtitles use the codes ai-zh / ai-en — these are distinct from
            // zh-CN/zh and must be listed explicitly or the filter misses them entirely.
            string primaryLang = GetLanguageCode();
            string zhVariants = "zh-CN,zh,ai-zh";
            string langCode = primaryLang.Contains("zh") ? $"{primaryLang},{zhVariants}" : $"{primaryLang},ai-en,{zhVariants}";

            TerraVision.instance.Logger.Info($"[Captions/Bilibili] Fetching for {url} (lang: {langCode})");

            string subPath = await DownloadBilibiliSubtitleAsync(url, ytdlpPath, langCode, token);

            if (subPath == null)
                return [];

            string ext = Path.GetExtension(subPath).ToLowerInvariant();
            var blocks = ext == ".vtt" ? ParseSimpleVtt(subPath) : ParseSrt(subPath);
            TerraVision.instance.Logger.Info($"[Captions/Bilibili] Loaded {blocks.Count} blocks from {ext.ToUpperInvariant()}");
            return blocks;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"[Captions/Bilibili] Fetch failed: {ex.Message}");
            return [];
        }
    }

    private static async Task<string> DownloadBilibiliSubtitleAsync(string url, string ytdlpPath, string langCode, CancellationToken token)
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

        // --- Step 1: saved cookies folder (takes priority, works while browser is open) ---
        string folderArg = CookieManager.GetFolderCookiesArg(url);
        if (folderArg != null)
        {
            TerraVision.instance.Logger.Debug("[Captions/Bilibili] Using saved cookies file");
            ClearSubtitleFiles(tempDir);
            string folderStderr = null;
            try { folderStderr = await RunYtDlpCaptureStderrAsync(ytdlpPath, $"{folderArg} {baseArgs}", token); }
            catch (OperationCanceledException) { throw; }
            catch { }

            string found = FindSubtitleFile(tempDir);
            if (found != null) return found;

            // Distinguish "auth failed" from "video genuinely has no subtitles".
            // If stderr contains login-related text, our cookies may be stale — fall
            // through to browser detection. If stderr is clean, the video simply has
            // no subtitles and there is nothing browser cookies could add.
            bool authError = folderStderr != null && (
                folderStderr.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                folderStderr.Contains("not logged", StringComparison.OrdinalIgnoreCase) ||
                folderStderr.Contains("cookies", StringComparison.OrdinalIgnoreCase) ||
                folderStderr.Contains("authentication", StringComparison.OrdinalIgnoreCase));

            if (!authError)
            {
                TerraVision.instance.Logger.Debug(
                    "[Captions/Bilibili] No subtitles found (video has none, not an auth issue)");
                return null;
            }

            TerraVision.instance.Logger.Debug("[Captions/Bilibili] Saved cookies may be stale (auth error detected), trying browser...");
        }

        // --- Step 2: browser cookie auto-detection ---
        string[] browserCascade = CookieManager.GetBrowserCascade();

        if (browserCascade.Length == 0)
        {
            // Auth disabled — one unauthenticated attempt in case the video is public
            TerraVision.instance.Logger.Debug("[Captions/Bilibili] Cookie auth disabled — attempting unauthenticated");
            ClearSubtitleFiles(tempDir);
            try { await RunYtDlpAsync(ytdlpPath, baseArgs, token); }
            catch (OperationCanceledException) { throw; }
            catch { }
            return
                FindSubtitleFile(tempDir);
        }

        foreach (string browserArg in browserCascade)
        {
            token.ThrowIfCancellationRequested();

            TerraVision.instance.Logger.Debug($"[Captions/Bilibili] Trying --cookies-from-browser {browserArg}");

            ClearSubtitleFiles(tempDir);
            string stderr = null;
            try
            {
                stderr = await RunYtDlpCaptureStderrAsync(ytdlpPath, $"--cookies-from-browser {browserArg} {baseArgs}", token);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            string found = FindSubtitleFile(tempDir);
            if (found != null)
            {
                TerraVision.instance.Logger.Info($"[Captions/Bilibili] Got subtitles via browser cookies ({browserArg})");
                return found;
            }

            if (stderr != null && stderr.Contains("Could not copy") &&
                stderr.Contains("cookie database"))
            {
                CookieManager.NotifyDatabaseLocked(browserArg);
                return null;
            }

            TerraVision.instance.Logger.Debug($"[Captions/Bilibili] No subtitles from {browserArg}, trying next...");
        }

        TerraVision.instance.Logger.Info("[Captions/Bilibili] No captions found — Bilibili subtitles require being logged in via a supported browser (see TerraVision mod settings)");

        return null;
    }

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
            TerraVision.instance.Logger.Info($"[Captions/Vimeo] Fetching for {url} (lang: {langCode})");

            string tempDir = GetTempDir();
            string cookieArg = CookieManager.GetCookiesArg(url) ?? "";
            string args =
                $"{cookieArg} --skip-download --write-sub --sub-format vtt/srt/best " +
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
            TerraVision.instance.Logger.Info($"[Captions/Vimeo] Loaded {blocks.Count} blocks");
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
            TerraVision.instance.Logger.Info($"[Captions/Generic] Attempting fetch for {url} (lang: {langCode})");

            string tempDir = GetTempDir();
            string cookieArg = CookieManager.GetCookiesArg(url) ?? "";
            string[] attempts =
            [
                // Manual subs first
                $"{cookieArg} --skip-download --write-sub --sub-format vtt/srt/best " +
                $"--sub-langs \"{langCode}\" " +
                $"-o \"{Path.Combine(tempDir, "%(id)s.%(ext)s")}\" " +
                $"--no-playlist -- \"{url}\"",

                // Auto-generated fallback
                $"{cookieArg} --skip-download --write-auto-sub --sub-format vtt/srt/best " +
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
                    TerraVision.instance.Logger.Info($"[Captions/Generic] Loaded {blocks.Count} blocks");
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

    private static async Task<string> DownloadYouTubeSubtitleAsync(
        string url, string ytdlpPath, string langCode, CancellationToken token)
    {
        string tempDir = GetTempDir();
        string outputTemplate = Path.Combine(tempDir, "%(id)s.%(ext)s");

        // Inject cookies if available — unlocks age-restricted and members-only videos
        string cookieArg = CookieManager.GetCookiesArg(url) ?? "";
        if (!string.IsNullOrEmpty(cookieArg))
            TerraVision.instance.Logger.Debug("[Captions/YouTube] Using cookies for auth");

        string[] attempts =
        {
            // 1. Manual captions in the user's language
            $"{cookieArg} --skip-download --write-sub --sub-format vtt " +
            $"--sub-langs \"{langCode}\" " +
            $"-o \"{outputTemplate}\" --no-playlist -- \"{url}\"",

            // 2. Auto-generated captions in the user's language
            $"{cookieArg} --skip-download --write-auto-sub --sub-format vtt " +
            $"--sub-langs \"{langCode}\" " +
            $"-o \"{outputTemplate}\" --no-playlist -- \"{url}\"",

            // 3. Any manual captions in any language (e.g. Japanese-only videos)
            $"{cookieArg} --skip-download --write-sub --sub-format vtt " +
            $"--sub-langs \"all\" " +
            $"-o \"{outputTemplate}\" --no-playlist -- \"{url}\"",

            // 4. Any auto-generated captions in any language
            $"{cookieArg} --skip-download --write-auto-sub --sub-format vtt " +
            $"--sub-langs \"all\" " +
            $"-o \"{outputTemplate}\" --no-playlist -- \"{url}\""
        };

        string[] labels = ["manual", "auto-generated", "manual (any language)", "auto-generated (any language)"];

        for (int i = 0; i < attempts.Length; i++)
        {
            ClearSubtitleFiles(tempDir);
            try { await RunYtDlpAsync(ytdlpPath, attempts[i], token); }
            catch (OperationCanceledException) { throw; }
            catch { }

            // Pass langCode so that when --sub-langs all downloads multiple files,
            // we still prefer the user's language over an arbitrary first match.
            string vttFile = FindVttFile(tempDir, langCode);
            if (vttFile != null)
            {
                TerraVision.instance.Logger.Info($"[Captions/YouTube] Found {labels[i]} captions: {Path.GetFileName(vttFile)}");
                return vttFile;
            }

            TerraVision.instance.Logger.Debug($"[Captions/YouTube] No {labels[i]} captions found, trying next...");
        }

        return null;
    }

    private static string GetLanguageCode() => Language.ActiveCulture.Name switch
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

    /// <summary>
    /// Returns true if a line should be treated as a VTT cue separator —
    /// i.e. the end of a cue's text block. Handles:
    ///   - Truly empty lines
    ///   - Lines containing only ASCII whitespace
    ///   - Lines containing only Unicode whitespace like U+3000 (IDEOGRAPHIC SPACE),
    ///     which is used as a visual separator in some Japanese/Asian caption files
    ///     (e.g. the Bad Apple ASCII art captions) and is NOT caught by string.IsNullOrEmpty.
    /// </summary>
    private static bool IsVttCueSeparator(string line) => string.IsNullOrEmpty(line) || line.All(c => char.IsWhiteSpace(c));

    /// <summary>
    /// Returns true if the VTT file uses YouTube's word-level timing format
    /// (i.e. contains &lt;c&gt; tags or inline timestamp tags like &lt;00:00:00.000&gt;).
    /// Used to decide whether to route to ParseYouTubeVtt or ParseSimpleVtt.
    /// Reads only the first 200 lines to avoid loading the whole file.
    /// </summary>
    private static bool VttHasWordTiming(string filePath)
    {
        try
        {
            int linesRead = 0;
            foreach (string line in File.ReadLines(filePath, System.Text.Encoding.UTF8))
            {
                if (line.Contains("<c>") || line.Contains("</c>") ||
                    System.Text.RegularExpressions.Regex.IsMatch(line, @"<\d+:\d{2}:\d{2}\.\d{3}>"))
                    return true;
                if (++linesRead >= 200) break;
            }
        }
        catch { }
        return false;
    }

    private static string FindVttFile(string directory) => FindVttFile(directory, preferredLang: null);

    /// <summary>
    /// Finds the best VTT file in the directory.
    /// When multiple files exist (--sub-langs all), prefers:
    ///   1. A file whose name contains the preferred language code
    ///   2. A file that doesn't look auto-generated (no ".auto." or "-auto." in name)
    ///   3. The first file found
    /// </summary>
    private static string FindVttFile(string directory, string preferredLang)
    {
        var files = Directory.GetFiles(directory, "*.vtt");
        if (files.Length == 0) return null;
        if (files.Length == 1) return files[0];

        // Prefer the user's language if specified
        if (!string.IsNullOrEmpty(preferredLang))
        {
            foreach (string lang in preferredLang.Split(','))
            {
                string trimmed = lang.Trim();
                var match = files.FirstOrDefault(f =>
                    Path.GetFileName(f).Contains(trimmed, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
        }

        // Prefer manual (non-auto) tracks
        var manual = files.FirstOrDefault(f =>
        {
            string name = Path.GetFileName(f);
            return !name.Contains(".auto.", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("-auto.", StringComparison.OrdinalIgnoreCase);
        });
        if (manual != null) return manual;

        return files[0];
    }

    private static string FindSrtFile(string directory)
    {
        var files = Directory.GetFiles(directory, "*.srt");
        return files.Length > 0 ? files[0] : null;
    }

    /// <summary>Returns the first subtitle file found, VTT preferred over SRT.</summary>
    private static string FindSubtitleFile(string directory) => FindVttFile(directory) ?? FindSrtFile(directory);

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
            while (i < lines.Length && !IsVttCueSeparator(lines[i]))
            {
                // Strip HTML inline tags but preserve all other characters including
                // Unicode, Braille, and full-width characters used in ASCII art captions.
                string cleaned = InlineTagRegex().Replace(lines[i], "").TrimEnd();
                textLines.Add(cleaned);
                i++;
            }

            // Trim leading/trailing blank lines but keep internal structure
            while (textLines.Count > 0 && string.IsNullOrWhiteSpace(textLines[0]))
                textLines.RemoveAt(0);
            while (textLines.Count > 0 && string.IsNullOrWhiteSpace(textLines[^1]))
                textLines.RemoveAt(textLines.Count - 1);

            if (textLines.Count == 0)
                continue;

            // Join with newline — preserves ASCII art frame layout and multi-line subtitles.
            // The renderer's WrapText already handles line breaks correctly.
            string text = string.Join("\n", textLines);
            if (!string.IsNullOrWhiteSpace(text))
                cues.Add((start, end, text));
        }

        return BuildBlocksFromCues(cues);
    }

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

    /// <summary>
    /// Converts a flat list of (start, end, text) cues into CaptionBlocks.
    /// All words in each block get Timestamp = StartTime (instant reveal).
    /// The previous cue's text is stored as PreviousSentence for two-line display.
    /// </summary>
    private static List<CaptionBlock> BuildBlocksFromCues(List<(float Start, float End, string Text)> cues)
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
                FullSentence = text,
                IsPreformatted = text.Contains('\n')
            });
        }

        return blocks;
    }

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

    private static float ParseVttTime(string timestamp)
    {
        string[] parts = timestamp.Replace(',', '.').Split(':');
        try
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if(parts.Length == 3)
                return float.Parse(parts[0]) * 3600f + float.Parse(parts[1]) * 60f + float.Parse(parts[2], c);
            return  float.Parse(parts[0]) * 60f + float.Parse(parts[1], c);
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
            return float.Parse(m.Groups[1].Value, c) * 3600f + float.Parse(m.Groups[2].Value, c) * 60f + float.Parse(m.Groups[3].Value, c) + float.Parse(m.Groups[4].Value, c) / 1000f;
        }
        catch { return 0f; }
    }

    /// <summary>
    /// Runs a yt-dlp process and waits for it to exit.
    /// Logs all output at Debug level on non-zero exit.
    /// </summary>
    private static async Task RunYtDlpAsync(string ytdlpPath, string arguments, CancellationToken token) => await RunYtDlpCaptureStderrAsync(ytdlpPath, arguments, token);

    /// <summary>
    /// Runs yt-dlp and returns captured stderr. Also logs stdout+stderr together
    /// at Debug level on any non-zero exit so nothing is silently swallowed.
    /// </summary>
    private static async Task<string> RunYtDlpCaptureStderrAsync(string ytdlpPath, string arguments, CancellationToken token)
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
}