using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace TerraVision;

/// <summary>
/// Which browser yt-dlp should read login cookies from as a fallback when no
/// saved cookies file exists in the TerraVision cookies folder.
/// </summary>
public enum BrowserCookieSource
{
    /// <summary>
    /// Try Chrome, Firefox, then Edge in sequence.
    /// The first browser that successfully yields data is used.
    /// </summary>
    Auto,

    Chrome,
    Firefox,
    Edge,
    Brave,
    Opera,
    OperaGX,
    Vivaldi,

    /// <summary>
    /// Never attempt to read browser cookies automatically.
    /// Only saved cookies files in the TerraVision cookies folder will be used.
    /// </summary>
    None
}

/// <summary>
/// Maximum video resolution to request from extractors.
/// Auto selects the best available quality.
/// </summary>
public enum PreferredVideoQuality
{
    Auto,
    Q480p,
    Q720p,
    Q1080p,
    Q1440p,
    Q2160p
}

/// <summary>
/// Language to use for captions. Auto follows the Terraria game language.
/// </summary>
public enum SubtitleLanguage
{
    Auto,
    English,
    Chinese,
    German,
    French,
    Spanish,
    Italian,
    Russian,
    PortugueseBrazil,
    Polish,
    Japanese,
    Korean
}

public class TerraVisionConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    // Cookie settings

    [Header("Cookies")]
    [DefaultValue(BrowserCookieSource.Auto)]
    public BrowserCookieSource BrowserForCookies { get; set; } = BrowserCookieSource.Auto;

    // Playback settings

    [Header("Playback")]
    [DefaultValue(PreferredVideoQuality.Auto)]
    public PreferredVideoQuality VideoQuality { get; set; } = PreferredVideoQuality.Auto;

    [DefaultValue(100)]
    [Range(0, 100)]
    public byte DefaultVolume { get; set; } = 100;

    [DefaultValue(true)]
    public bool EnableCaptions { get; set; } = true;

    [DefaultValue(SubtitleLanguage.Auto)]
    public SubtitleLanguage CaptionLanguage { get; set; } = SubtitleLanguage.Auto;

    [DefaultValue(true)]
    public bool ShowLoadingMessages { get; set; } = true;

    // Helpers

    /// <summary>
    /// Returns the configured maximum video height in pixels, or null for uncapped (Auto).
    /// Used by extractors to filter or request streams at or below this resolution.
    /// </summary>
    public int? MaxVideoHeight() => VideoQuality switch
    {
        PreferredVideoQuality.Q480p => 480,
        PreferredVideoQuality.Q720p => 720,
        PreferredVideoQuality.Q1080p => 1080,
        PreferredVideoQuality.Q1440p => 1440,
        PreferredVideoQuality.Q2160p => 2160,
        _ => null
    };

    /// <summary>
    /// Returns the yt-dlp language code string for the configured caption language.
    /// Returns null when set to Auto so callers can fall back to game language detection.
    /// </summary>
    public string CaptionLanguageCode() => CaptionLanguage switch
    {
        SubtitleLanguage.English => "en",
        SubtitleLanguage.Chinese => "zh-Hans,zh,zh-CN",
        SubtitleLanguage.German => "de",
        SubtitleLanguage.French => "fr",
        SubtitleLanguage.Spanish => "es",
        SubtitleLanguage.Italian => "it",
        SubtitleLanguage.Russian => "ru",
        SubtitleLanguage.PortugueseBrazil => "pt-BR,pt",
        SubtitleLanguage.Polish => "pl",
        SubtitleLanguage.Japanese => "ja",
        SubtitleLanguage.Korean => "ko",
        _ => null  // Auto — let CaptionFetcher detect from game language
    };
}