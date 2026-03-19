using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraVision.Core;

/// <summary>
/// Central cookie management for TerraVision.
///
/// Resolution order for any URL:
///   1. Cookies folder  — %SavePath%/TerraVision/cookies/{domain}.txt
///                        e.g. youtube.com.txt, bilibili.com.txt
///   2. Browser auto-detection — --cookies-from-browser per TerraVisionConfig
///
/// The cookies folder is the recommended approach for Chromium-based browsers
/// (which lock their database while running). Run /tvsetup cookies to populate it.
/// Browser auto-detection is the zero-config fallback for Firefox and any browser
/// that isn't currently open.
/// </summary>
public static class CookieManager
{
    /// <summary>
    /// The folder where per-site cookies files live.
    /// Files should be named {domain}.txt (e.g. youtube.com.txt, bilibili.com.txt).
    /// </summary>
    public static string CookiesFolderPath => Path.Combine(Main.SavePath, "TerraVision", "cookies");

    /// <summary>
    /// Returns the path where cookies for a given domain should be stored.
    /// e.g. GetSavedCookiesPath("bilibili.com") → .../cookies/bilibili.com.txt
    /// </summary>
    public static string GetSavedCookiesPath(string domain) => Path.Combine(CookiesFolderPath, $"{domain}.txt");

    /// <summary>
    /// Returns the best --cookies or --cookies-from-browser argument string for
    /// the given URL, or null if no cookies are configured/available.
    ///
    /// Callers prepend this to their yt-dlp argument string:
    ///   string cookieArg = CookieManager.GetCookiesArg(url);
    ///   string fullArgs = cookieArg != null ? $"{cookieArg} {baseArgs}" : baseArgs;
    /// </summary>
    public static string GetCookiesArg(string url)
    {
        // 1. Check the cookies folder first
        string folderArg = GetFolderCookiesArg(url);
        if (folderArg != null)
            return folderArg;

        // 2. Fall back to browser auto-detection
        return GetBrowserCookiesArg(url);
    }

    /// <summary>
    /// Returns only the folder-based --cookies arg for the given URL, or null.
    /// Useful when callers want to know specifically whether a saved file exists.
    /// </summary>
    public static string GetFolderCookiesArg(string url)
    {
        string path = GetFolderCookiesPath(url);
        if (path == null) return null;
        TerraVision.instance.Logger.Debug(
            $"[Cookies] Using saved cookies: {Path.GetFileName(path)}");
        return $"--cookies \"{path}\"";
    }

    /// <summary>
    /// Returns the raw path to the saved cookies file for the given URL, or null.
    /// Use this when passing to an API that takes a path directly (e.g. YtdlSharp
    /// OptionSet.Cookies) rather than a command-line flag string.
    /// </summary>
    public static string GetFolderCookiesPath(string url)
    {
        string domain = ExtractDomain(url);
        if (domain == null) return null;

        string[] candidates =
        [
            GetSavedCookiesPath(domain),
            GetSavedCookiesPath(ApexDomain(domain))
        ];

        foreach (string path in candidates.Distinct())
            if (File.Exists(path)) return path;

        return null;
    }

    /// <summary>
    /// Returns a --cookies-from-browser arg based on the user's config,
    /// or null if cookie auth is disabled (BrowserCookieSource.None).
    /// Does NOT check whether the browser is actually installed or logged in.
    /// </summary>
    public static string GetBrowserCookiesArg(string url)
    {
        var config = ModContent.GetInstance<TerraVisionConfig>();
        BrowserCookieSource browser = config?.BrowserForCookies ?? BrowserCookieSource.Auto;

        if (browser == BrowserCookieSource.None)
            return null;

        if (browser == BrowserCookieSource.Auto)
        {
            // Auto: return the first installed browser we can find
            BrowserCookieSource[] cascade =
            [
                BrowserCookieSource.Chrome,
                BrowserCookieSource.Firefox,
                BrowserCookieSource.Edge
            ];

            foreach (var b in cascade)
            {
                string cookiePath = ResolveBrowserCookiesDbPath(b);
                if (cookiePath != null && File.Exists(cookiePath))
                    return $"--cookies-from-browser {ResolveBrowserArg(b)}";
            }

            return null;
        }

        return $"--cookies-from-browser {ResolveBrowserArg(browser)}";
    }

    /// <summary>
    /// Attempts each browser in the configured cascade, runs the yt-dlp invocation
    /// via the provided delegate, and returns the first stderr output that doesn't
    /// contain a locking error. Fires an in-game message if the database is locked.
    ///
    /// This is used by CaptionFetcher.DownloadBilibiliSubtitleAsync where we need
    /// to try multiple browsers in sequence and inspect stderr.
    /// </summary>
    public static string[] GetBrowserCascade()
    {
        var config = ModContent.GetInstance<TerraVisionConfig>();
        BrowserCookieSource browser = config?.BrowserForCookies ?? BrowserCookieSource.Auto;

        if (browser == BrowserCookieSource.None)
            return [];

        if (browser == BrowserCookieSource.Auto)
            return
            [
                ResolveBrowserArg(BrowserCookieSource.Chrome),
                ResolveBrowserArg(BrowserCookieSource.Firefox),
                ResolveBrowserArg(BrowserCookieSource.Edge)
            ];

        return [ResolveBrowserArg(browser)];
    }

    /// <summary>
    /// Notifies the user in-game that the browser database is locked and tells
    /// them to run /tvsetup cookies. Should be called when a "Could not copy
    /// cookie database" error is detected in yt-dlp stderr.
    /// </summary>
    public static void NotifyDatabaseLocked(string browserArg)
    {
        // Extract a human-readable browser name from the arg (strip path for OperaGX)
        string displayName = browserArg.Contains(':') ? browserArg[..browserArg.IndexOf(':')] : browserArg;

        TerraVision.instance.Logger.Warn(
            $"[Cookies] {displayName} cookie database is locked (browser is open).");

        Main.QueueMainThreadAction(() => Main.NewText($"Cookies locked ({displayName} is open). Run  /tvsetup cookies  to save them, or close the browser and try again.", Color.Orange));
    }

    /// <summary>
    /// Returns the path to the Cookies SQLite file for a given browser,
    /// or null if the browser isn't known or the path doesn't exist.
    /// </summary>
    public static string ResolveBrowserCookiesDbPath(BrowserCookieSource browser)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        string profileDir = browser switch
        {
            BrowserCookieSource.Chrome => Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Network"),
            BrowserCookieSource.Edge => Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Network"),
            BrowserCookieSource.Brave => Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Network"),
            BrowserCookieSource.Opera => Path.Combine(roaming, "Opera Software", "Opera Stable", "Network"),
            BrowserCookieSource.OperaGX => Path.Combine(roaming, "Opera Software", "Opera GX Stable", "Default", "Network"),
            BrowserCookieSource.Vivaldi => Path.Combine(local, "Vivaldi", "User Data", "Default", "Network"),
            _ => null
        };

        return profileDir == null ? null : Path.Combine(profileDir, "Cookies");
    }

    /// <summary>
    /// Returns the yt-dlp --cookies-from-browser argument value for a browser.
    /// Opera GX needs the explicit "opera:path" syntax; everything else uses the
    /// lowercase browser name.
    /// </summary>
    public static string ResolveBrowserArg(BrowserCookieSource browser)
    {
        if (browser == BrowserCookieSource.OperaGX)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string gxPath = Path.Combine(appData, "Opera Software", "Opera GX Stable");
            return $"opera:\"{gxPath}\"";
        }

        return browser.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Extracts the registrable domain from a URL (e.g. "bilibili.com" from
    /// "https://www.bilibili.com/video/BV1..."). Returns null on failure.
    /// </summary>
    public static string ExtractDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            // Strip leading "www." for normalisation
            string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..]
                : uri.Host;
            return host.ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the apex (two-label) domain: "video.bilibili.com" → "bilibili.com".
    /// Used as a fallback filename if the full host file doesn't exist.
    /// </summary>
    private static string ApexDomain(string domain)
    {
        string[] parts = domain.Split('.');
        return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : domain;
    }
}