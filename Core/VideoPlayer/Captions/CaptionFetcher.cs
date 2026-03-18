using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Terraria.ModLoader;

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
    // Compile-time generated regexes (SYSLIB1045)
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex InlineTagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"(<(?<ts>\d+:\d{2}:\d{2}\.\d{3})>)?(?:<c>)?(?<word>[^<]+?)(?:</c>)?(?=<|$)")]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(@"<c>|<\d+:\d{2}:\d{2}\.\d{3}>")]
    private static partial Regex WordLineMarkerRegex();

    /// <summary>
    /// Fetches captions for a YouTube URL using yt-dlp.
    /// Tries manual captions first, then auto-generated.
    /// Returns an empty list if no captions are available.
    /// </summary>
    public static async Task<List<CaptionBlock>> FetchYouTubeAsync(string url, string ytdlpPath, CancellationToken token = default)
    {
        try
        {
            string langCode = GetLanguageCode();
            TerraVision.instance.Logger.Info(
                $"Fetching captions for {url} (language: {langCode})");

            string vttPath = await DownloadSubtitleAsync(url, ytdlpPath, langCode, token);

            if (vttPath == null)
            {
                TerraVision.instance.Logger.Info("No captions found for this video");
                return [];
            }

            TerraVision.instance.Logger.Info($"Parsing captions from {vttPath}");
            var blocks = ParseVtt(vttPath);
            TerraVision.instance.Logger.Info($"Loaded {blocks.Count} caption entries");
            return blocks;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Caption fetch failed: {ex.Message}");
            return [];
        }
    }

    private static string GetLanguageCode()
    {
        return Terraria.Localization.Language.ActiveCulture.Name switch
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
    }

    private static async Task<string> DownloadSubtitleAsync(string url, string ytdlpPath, string langCode, CancellationToken token)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "TerraVision_Captions");
        Directory.CreateDirectory(tempDir);

        string outputTemplate = Path.Combine(tempDir, "%(id)s.%(ext)s");

        string[] attempts = {
            $"--skip-download --write-sub --sub-format vtt " +
            $"--sub-langs \"{langCode}\" " +
            $"-o \"{outputTemplate}\" --no-playlist -- \"{url}\"",

            $"--skip-download --write-auto-sub --sub-format vtt " +
            $"--sub-langs \"{langCode}\" " +
            $"-o \"{outputTemplate}\" --no-playlist -- \"{url}\""
        };

        string[] labels = { "manual", "auto-generated" };

        for (int i = 0; i < attempts.Length; i++)
        {
            // Clear any leftover .vtt files before each attempt
            foreach (var old in Directory.GetFiles(tempDir, "*.vtt"))
                try { File.Delete(old); } catch { }

            await RunYtDlpAsync(ytdlpPath, attempts[i], token);

            string vttFile = FindVttFile(tempDir);
            if (vttFile != null)
            {
                TerraVision.instance.Logger.Info(
                    $"Found {labels[i]} captions: {Path.GetFileName(vttFile)}");
                return vttFile;
            }

            TerraVision.instance.Logger.Debug(
                $"No {labels[i]} captions found, trying next...");
        }

        return null;
    }

    private static string FindVttFile(string directory)
    {
        var files = Directory.GetFiles(directory, "*.vtt");
        return files.Length > 0 ? files[0] : null;
    }

    private static async Task RunYtDlpAsync(string ytdlpPath, string arguments, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>();
        var error = new System.Text.StringBuilder();

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

        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };
        process.Exited += (_, _) =>
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                // Log the error output but don't throw — the caller checks for output files
                // and handles missing results gracefully.
                TerraVision.instance.Logger.Debug(
                    $"yt-dlp exited with code {process.ExitCode}: {error.ToString().Trim()}");
            }
            tcs.TrySetResult(true);
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
            await tcs.Task;
        }
    }

    /// <summary>
    /// Parses a WebVTT file into CaptionBlocks.
    ///
    /// YouTube's auto-generated VTT uses a rolling two-line window:
    ///   - Short cues (duration &lt; 100ms): clean completed sentence — skipped,
    ///     since PreviousSentence is read directly from long cue line 0.
    ///   - Long cues: line 0 = previous sentence (or blank line after a pause),
    ///                remaining lines = current sentence with per-word timestamps.
    /// </summary>
    private static List<CaptionBlock> ParseVtt(string filePath)
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

            // CRITICAL: check the raw line before trimming — a single space signals
            // a post-pause cue with no previous sentence. Trimming first collapses
            // it to "" which looks like a cue separator, causing the whole cue to
            // be dropped and those sentences to never appear.
            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrEmpty(lines[i]))
            {
                textLines.Add(lines[i].Trim());
                i++;
            }

            if (textLines.Count == 0)
                continue;

            // Skip short transitional cues (< 100ms) — they're just markers
            if ((end - start) < 0.1f)
                continue;

            string firstLine = textLines[0]; // already trimmed, may be ""

            string prevLine;
            string wordText;

            if (string.IsNullOrEmpty(firstLine))
            {
                // Post-pause: blank first line means no previous sentence
                prevLine = null;
                wordText = string.Join(" ", textLines.Skip(1));
            }
            else if (WordLineMarkerRegex().IsMatch(firstLine))
            {
                // Alternate format: first line already contains word-by-word content
                prevLine = null;
                wordText = firstLine;
            }
            else
            {
                // Normal two-line cue: line 0 = previous sentence, rest = words
                string cleaned = InlineTagRegex().Replace(firstLine, "").Trim();

                // Strip speaker change markers (>>) — displayed as plain text for now
                cleaned = cleaned.Replace(">>", "").Trim();
                cleaned = MultiSpaceRegex().Replace(cleaned, " ");

                prevLine = string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
                wordText = string.Join(" ", textLines.Skip(1));
            }

            if (!string.IsNullOrWhiteSpace(wordText))
                longCues.Add((start, end, prevLine, wordText));
        }

        // Build CaptionBlocks from long cues
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

        // FullSentence of block N = PreviousSentence of block N+1,
        // which is the completed version of block N's words
        for (int b = 0; b < blocks.Count - 1; b++)
            blocks[b].FullSentence = blocks[b + 1].PreviousSentence;

        // Last block derives FullSentence from its own words
        if (blocks.Count > 0)
        {
            var last = blocks[^1];
            last.FullSentence = string.Join(" ", last.Words.Select(w => w.Text));
        }

        // EndTime = next block's StartTime for clean transitions
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
    /// Extracts words and their individual timestamps from a raw long cue line.
    /// Format: word&lt;timestamp&gt;&lt;c&gt;nextword&lt;/c&gt;...
    /// </summary>
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

    /// <summary>
    /// Parses a VTT timestamp string (HH:MM:SS.mmm or MM:SS.mmm) into seconds.
    /// </summary>
    private static float ParseVttTime(string timestamp)
    {
        string[] parts = timestamp.Replace(',', '.').Split(':');
        try
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (parts.Length == 3)
                return float.Parse(parts[0]) * 3600f + float.Parse(parts[1]) * 60f + float.Parse(parts[2], culture);
            return float.Parse(parts[0]) * 60f + float.Parse(parts[1], culture);
        }
        catch { return 0f; }
    }
}