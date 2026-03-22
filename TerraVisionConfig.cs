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

public class TerraVisionConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    [Header("Cookies")]
    [DefaultValue(BrowserCookieSource.Auto)]
    public BrowserCookieSource BrowserForCookies { get; set; } = BrowserCookieSource.Auto;

    [Header("Playback")]
    [DefaultValue(PreferredVideoQuality.Auto)]
    public PreferredVideoQuality VideoQuality { get; set; } = PreferredVideoQuality.Auto;

    [DefaultValue(true)]
    public bool EnableCaptions { get; set; } = true;

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
}