using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace TerraVision;

/// <summary>
/// Which browser yt-dlp should read login cookies from for caption fetching.
/// Only relevant for platforms that require login to access subtitles (e.g. Bilibili).
/// </summary>
public enum BrowserCookieSource
{
    /// <summary>
    /// Try Chrome, Firefox, then Edge in sequence.
    /// The first browser that successfully yields subtitles is used.
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
    /// Never attempt to read browser cookies.
    /// Bilibili captions will be unavailable without cookies.
    /// </summary>
    None
}

[Label("TerraVision Settings")]
public class TerraVisionConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    // -------------------------------------------------------------------------
    // Caption settings
    // -------------------------------------------------------------------------

    [Header("Captions")]

    [Label("Browser for Cookies")]
    [Tooltip(
        "Which browser yt-dlp should read login cookies from when fetching captions.\n" +
        "Required for platforms that need a login to access subtitles (e.g. Bilibili).\n" +
        "Select the browser you use to log into video sites.\n\n" +
        "'Auto' tries Chrome, Firefox, then Edge in sequence.\n" +
        "'Opera GX' is listed separately from Opera — pick the right one for your install.\n" +
        "'None' disables cookie-based auth entirely (Bilibili captions will be unavailable).\n\n" +
        "Note: Chromium-based browsers (Chrome, Edge, Opera, Brave) may fail to share\n" +
        "cookies while the browser is open due to database locking. Firefox does not\n" +
        "have this limitation and is the most reliable choice if captions are important."
    )]
    [DefaultValue(BrowserCookieSource.Auto)]
    public BrowserCookieSource BrowserForCookies { get; set; } = BrowserCookieSource.Auto;

    [Label("Cookies File Path (Advanced)")]
    [Tooltip(
        "Optional: path to a Netscape-format cookies.txt file exported from your browser.\n" +
        "If set, this takes priority over the 'Browser for Cookies' setting above.\n" +
        "Leave blank to use the browser setting instead.\n\n" +
        "Example: C:\\Users\\you\\Desktop\\cookies.txt\n\n" +
        "You can export a cookies.txt from Chrome or Firefox using the\n" +
        "'Get cookies.txt LOCALLY' browser extension."
    )]
    [DefaultValue("")]
    public string CookiesFilePath { get; set; } = "";
}