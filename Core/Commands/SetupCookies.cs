using System;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraVision.Core.Commands;

/// <summary>
/// Chat command: /tvsetup cookies [site] [browser]
///
/// Copies the user's browser Cookies SQLite file into TerraVision's cookies folder
/// (%SavePath%/TerraVision/cookies/) as a Netscape-format .txt file named after
/// the target site (e.g. bilibili.com.txt, youtube.com.txt).
///
/// Once saved, TerraVision uses the file automatically for all requests to that
/// site — even while the browser is open. This is the recommended fix for the
/// Chromium database-lock error, and also the way to unlock YouTube age-restricted
/// and members-only content.
///
/// Usage:
///   /tvsetup cookies                    — saves cookies for all supported sites
///   /tvsetup cookies bilibili.com       — saves cookies for Bilibili only
///   /tvsetup cookies youtube.com        — saves cookies for YouTube only
///   /tvsetup cookies bilibili.com edge  — saves Bilibili cookies from Edge
/// </summary>
public class SetupCookiesCommand : ModCommand
{
    public override string Command => "tvsetup";
    public override CommandType Type => CommandType.Chat;
    public override string Usage => "/tvsetup cookies [site] [browser]";
    public override string Description =>
        "Saves your browser login cookies so TerraVision can access restricted content. " +
        "Close your browser first, then run this command.";

    // Sites we know are worth saving cookies for, in order of usefulness.
    // The filename is the domain used for lookup by CookieManager.
    private static readonly string[] DefaultSites =
    [
        "bilibili.com",
        "youtube.com",
        "twitch.tv"
    ];

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("cookies", StringComparison.OrdinalIgnoreCase))
        {
            // Check if any cookies are already saved and report their status
            string folderPath = CookieManager.CookiesFolderPath;
            bool anyFound = false;

            if (Directory.Exists(folderPath))
            {
                foreach (string site in DefaultSites)
                {
                    string path = CookieManager.GetSavedCookiesPath(site);
                    if (File.Exists(path))
                    {
                        if (!anyFound)
                        {
                            caller.Reply("TerraVision cookies folder status:", Color.LightBlue);
                            anyFound = true;
                        }
                        var info = new FileInfo(path);
                        caller.Reply(
                            $"  ✓ {site}  ({info.Length / 1024}KB, saved {info.LastWriteTime:yyyy-MM-dd HH:mm})",
                            Color.LightGreen);
                    }
                    else
                    {
                        if (!anyFound)
                        {
                            caller.Reply("TerraVision cookies folder status:", Color.LightBlue);
                            anyFound = true;
                        }
                        caller.Reply($"  ✗ {site}  (not saved)", Color.Gray);
                    }
                }
            }

            if (anyFound)
            {
                caller.Reply(
                    "\nTo refresh cookies: /tvsetup cookies [site] [browser]\n" +
                    $"Cookies folder: {folderPath}",
                    Color.LightBlue);
            }
            else
            {
                caller.Reply(
                    "Usage: /tvsetup cookies [site] [browser]\n" +
                    "Saves your browser login cookies for use by TerraVision.\n" +
                    "Site examples: bilibili.com, youtube.com (omit to save all)\n" +
                    "Browser examples: chrome, firefox, edge, operagx (omit to use mod settings)\n\n" +
                    $"Cookies are saved to: {folderPath}",
                    Color.LightBlue);
            }
            return;
        }

        // Parse optional site and browser args
        string targetSite = null;
        BrowserCookieSource? explicitBrowser = null;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];

            // If it contains a dot it's a domain, otherwise try parsing as a browser
            if (arg.Contains('.'))
            {
                targetSite = arg.ToLowerInvariant();
            }
            else if (Enum.TryParse(arg, ignoreCase: true, out BrowserCookieSource parsed))
            {
                explicitBrowser = parsed;
            }
            else
            {
                caller.Reply(
                    $"Unknown argument '{arg}'. Expected a domain (e.g. bilibili.com) " +
                    "or a browser name (Chrome, Firefox, Edge, Brave, Opera, OperaGX, Vivaldi).",
                    Color.Orange);
                return;
            }
        }

        // Resolve browser
        BrowserCookieSource browser;
        if (explicitBrowser.HasValue)
        {
            browser = explicitBrowser.Value;
        }
        else
        {
            var config = ModContent.GetInstance<TerraVisionConfig>();
            browser = config?.BrowserForCookies ?? BrowserCookieSource.Auto;

            if (browser == BrowserCookieSource.Auto)
            {
                browser = FindFirstAvailableBrowser();
                if (browser == BrowserCookieSource.None)
                {
                    caller.Reply(
                        "Could not detect an installed browser with a cookies database.\n" +
                        "Specify one explicitly: /tvsetup cookies bilibili.com chrome\n" +
                        "Or export cookies manually and place them in:\n" +
                        $"  {CookieManager.CookiesFolderPath}\\bilibili.com.txt",
                        Color.Orange);
                    return;
                }
                caller.Reply($"Auto-detected browser: {browser}", Color.Gray);
            }
        }

        // Firefox: doesn't need this command — no locking issue — but still
        // explain what's happening rather than silently doing nothing.
        if (browser == BrowserCookieSource.Firefox)
        {
            caller.Reply(
                "Firefox doesn't lock its cookie database, so TerraVision can read it directly " +
                "while it's running. You don't need to run this command for Firefox.\n" +
                "If captions or content access still aren't working, make sure:\n" +
                "  • 'Firefox' is selected under Browser for Cookies in TerraVision settings\n" +
                "  • You are logged in to the relevant site in Firefox",
                Color.LightGreen);
            return;
        }

        if (browser == BrowserCookieSource.None)
        {
            caller.Reply(
                "Cookie auth is set to 'None' in TerraVision settings.\n" +
                "Change 'Browser for Cookies' in the mod settings menu, then run this command again.",
                Color.Orange);
            return;
        }

        string[] sitesToSave = targetSite != null ? [targetSite] : DefaultSites;

        string sourcePath = CookieManager.ResolveBrowserCookiesDbPath(browser);
        if (sourcePath == null)
        {
            caller.Reply(
                $"{browser} does not have a known cookies file location.\n" +
                "Try a different browser, or export cookies manually and place them in:\n" +
                $"  {CookieManager.CookiesFolderPath}\\<site>.txt",
                Color.Orange);
            return;
        }

        if (!File.Exists(sourcePath))
        {
            caller.Reply(
                $"Could not find {browser} cookies database at:\n{sourcePath}\n" +
                $"Make sure {browser} is installed and you have browsed while logged in.",
                Color.Orange);
            return;
        }

        caller.Reply(
            $"Copying {browser} cookies — make sure the browser is fully closed...",
            Color.Gray);

        try
        {
            Directory.CreateDirectory(CookieManager.CookiesFolderPath);

            // Copy the raw SQLite file to a temp location first, then we'll save it
            // per-site. We can't export to Netscape format directly without yt-dlp
            // running, so we save the SQLite file itself — yt-dlp accepts it via --cookies.
            string tempCopy = Path.Combine(
                Path.GetTempPath(),
                $"TerraVision_CookiesSetup_{Guid.NewGuid():N}.db");

            File.Copy(sourcePath, tempCopy, overwrite: true);

            try
            {
                int saved = 0;
                foreach (string site in sitesToSave)
                {
                    string destPath = CookieManager.GetSavedCookiesPath(site);
                    File.Copy(tempCopy, destPath, overwrite: true);
                    saved++;
                    TerraVision.instance.Logger.Info(
                        $"[SetupCookies] Saved {browser} cookies for {site} → {destPath}");
                }

                string siteList = string.Join(", ", sitesToSave);
                caller.Reply(
                    $"✓ {browser} cookies saved for: {siteList}\n" +
                    $"Location: {CookieManager.CookiesFolderPath}\n\n" +
                    "TerraVision will now use these cookies automatically. " +
                    "If you log out and back in to a site, run this command again to refresh.",
                    Color.LightGreen);
            }
            finally
            {
                try { File.Delete(tempCopy); } catch { }
            }
        }
        catch (IOException ex) when (
            ex.Message.Contains("used by another process") ||
            ex.HResult == unchecked((int)0x80070020))
        {
            caller.Reply(
                $"Could not copy {browser} cookies — the browser still has the file locked.\n\n" +
                $"  1. Close {browser} completely (check the system tray too)\n" +
                $"  2. Run  /tvsetup cookies  again\n\n" +
                "Or export cookies manually (there are browser extensions that can do this)\n" +
                $"and save the file to: {CookieManager.CookiesFolderPath}\\<site>.txt\n" +
                "  e.g. bilibili.com.txt, youtube.com.txt\n" +
                "Ensure you export your cookies in the NETSCAPE format!",
                Color.Orange);
        }
        catch (UnauthorizedAccessException)
        {
            caller.Reply(
                $"Access denied reading {browser} cookies.\n" +
                "Try running Terraria as administrator, or export cookies manually\n" +
                $"to: {CookieManager.CookiesFolderPath}\\<site>.txt",
                Color.Orange);
        }
        catch (Exception ex)
        {
            caller.Reply(
                $"Failed to copy cookies: {ex.Message}\n" +
                "You can export cookies manually using the 'Get cookies.txt LOCALLY' extension\n" +
                $"and save them to: {CookieManager.CookiesFolderPath}\\<site>.txt",
                Color.Orange);
            TerraVision.instance.Logger.Error($"[SetupCookies] Cookie copy failed: {ex}");
        }
    }

    private static BrowserCookieSource FindFirstAvailableBrowser()
    {
        BrowserCookieSource[] candidates =
        [
            BrowserCookieSource.Chrome,
            BrowserCookieSource.Edge,
            BrowserCookieSource.Brave,
            BrowserCookieSource.Opera,
            BrowserCookieSource.OperaGX,
            BrowserCookieSource.Vivaldi
        ];

        foreach (var b in candidates)
        {
            string path = CookieManager.ResolveBrowserCookiesDbPath(b);
            if (path != null && File.Exists(path))
                return b;
        }

        return BrowserCookieSource.None;
    }
}