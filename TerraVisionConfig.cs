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

    [Label("Opera GX")]
    OperaGX,

    Vivaldi,

    /// <summary>
    /// Never attempt to read browser cookies automatically.
    /// Only saved cookies files in the TerraVision cookies folder will be used.
    /// </summary>
    None
}

[Label("TerraVision Settings")]
public class TerraVisionConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    // -------------------------------------------------------------------------
    // Cookie settings
    // -------------------------------------------------------------------------

    [Header("Cookies")]

    [Label("Browser for Cookies")]
    [Tooltip(
        "Which browser TerraVision should read login cookies from automatically.\n" +
        "Used as a fallback when no saved cookies file exists for a site.\n\n" +
        "Cookies unlock features that require a login:\n" +
        "  • Bilibili: captions, higher resolutions (Premium)\n" +
        "  • YouTube: age-restricted videos, members-only content\n\n" +
        "'Auto' tries Chrome, Firefox, then Edge in sequence.\n" +
        "'Opera GX' is listed separately from Opera — pick the right one.\n" +
        "'None' disables automatic browser reading entirely.\n\n" +
        "Tip: For the most reliable experience, run  /tvsetup cookies  in chat\n" +
        "to save your cookies to the TerraVision cookies folder. Saved cookies\n" +
        "work even while your browser is open and apply to all supported sites.\n\n" +
        "Note: Chromium-based browsers (Chrome, Edge, Opera, Brave) may fail\n" +
        "while the browser is open due to database locking. Firefox does not\n" +
        "have this limitation."
    )]
    [DefaultValue(BrowserCookieSource.Auto)]
    public BrowserCookieSource BrowserForCookies { get; set; } = BrowserCookieSource.Auto;
}