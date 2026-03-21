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

public class TerraVisionConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    // -------------------------------------------------------------------------
    // Cookie settings
    // -------------------------------------------------------------------------

    [Header("Cookies")]
    [DefaultValue(BrowserCookieSource.Auto)]
    public BrowserCookieSource BrowserForCookies { get; set; } = BrowserCookieSource.Auto;
}