using System;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;
using TerraVision.Core.VideoPlayer.Captions;

namespace TerraVision.Core.Commands;

/// <summary>
/// Chat command: /tvsetup cookies
///
/// Copies the user's browser Cookies SQLite file to a persistent location in the
/// mod's save folder, then sets CookiesFilePath in TerraVisionConfig so future
/// Bilibili caption fetches use --cookies instead of --cookies-from-browser.
///
/// This is the recommended fix for the Chromium database-lock error that occurs
/// when yt-dlp tries to read cookies while the browser is open.
///
/// Usage:
///   /tvsetup cookies          — copies from the browser selected in mod settings
///   /tvsetup cookies chrome   — copies from Chrome specifically
///   /tvsetup cookies firefox  — copies from Firefox (not supported, see message)
///   /tvsetup cookies edge     — etc.
/// </summary>
public class SetupCookies : ModCommand
{
    public override string Command => "tvsetup";
    public override CommandType Type => CommandType.Chat;
    public override string Usage => "/tvsetup cookies [browser]";
    public override string Description => "Sets up Bilibili caption cookies. Run '/tvsetup cookies' to save your browser's " +
                                          "login cookies for use by TerraVision. The browser must be closed first.";

    /// <summary>
    /// Where we permanently store the saved cookies file.
    /// Using Main.SavePath keeps it alongside Terraria's other save data.
    /// </summary>
    private static string CookiesSavePath => Path.Combine(Main.SavePath, "TerraVision", "bilibili_cookies.db");

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("cookies", StringComparison.OrdinalIgnoreCase))
        {
            caller.Reply(
                "Usage: /tvsetup cookies [browser]\n" +
                "Saves your browser cookies so Bilibili captions work while your browser is open.\n" +
                "Browser is optional — defaults to the one set in TerraVision mod settings.",
                Color.LightBlue);
            return;
        }

        // Resolve which browser to use
        BrowserCookieSource browser;
        if (args.Length >= 2)
        {
            if (!Enum.TryParse(args[1], ignoreCase: true, out browser))
            {
                caller.Reply(
                    $"Unknown browser '{args[1]}'. Valid options: " +
                    "Chrome, Firefox, Edge, Brave, Opera, OperaGX, Vivaldi",
                    Color.Orange);
                return;
            }
        }
        else
        {
            var config = ModContent.GetInstance<TerraVisionConfig>();
            browser = config?.BrowserForCookies ?? BrowserCookieSource.Auto;

            if (browser == BrowserCookieSource.Auto)
            {
                // Auto mode — pick the first Chromium browser we can find a file for
                browser = FindFirstAvailableBrowser();
                if (browser == BrowserCookieSource.None)
                {
                    caller.Reply("Could not detect an installed browser with a cookies file. Specify a browser explicitly: /tvsetup cookies chrome", Color.Orange);
                    return;
                }

                caller.Reply($"Auto-detected browser: {browser}", Color.Gray);
            }
        }

        // Firefox uses NSS/SQLite — not compatible with yt-dlp's --cookies flag.
        // Firefox also doesn't have the database-lock problem, so this command
        // isn't needed for it anyway.
        if (browser == BrowserCookieSource.Firefox)
        {
            caller.Reply(
                "Firefox cookies don't need to be saved — Firefox doesn't lock its " +
                "cookie database, so TerraVision can read it directly while it's running. " +
                "If captions aren't working with Firefox, make sure you're logged into " +
                "Bilibili in Firefox and that 'Firefox' is selected in TerraVision mod settings.",
                Color.LightGreen);
            return;
        }

        if (browser == BrowserCookieSource.None)
        {
            caller.Reply(
                "Cookie auth is set to 'None' in mod settings. " +
                "Change 'Browser for Cookies' in TerraVision settings first.",
                Color.Orange);
            return;
        }

        // Try to copy the cookies file
        string sourcePath = CaptionFetcher.ResolveCookiesFilePath(browser);
        if (sourcePath == null)
        {
            caller.Reply(
                $"{browser} does not have a known cookies file location. " +
                "Try a different browser, or export cookies manually (see mod settings).",
                Color.Orange);
            return;
        }

        if (!File.Exists(sourcePath))
        {
            caller.Reply(
                $"Could not find {browser} cookies at:\n{sourcePath}\n" +
                $"Make sure {browser} is installed and you have logged into Bilibili in it.",
                Color.Orange);
            return;
        }

        caller.Reply($"Copying {browser} cookies — make sure the browser is closed...", Color.Gray);

        try
        {
            string saveDir = Path.GetDirectoryName(CookiesSavePath)!;
            Directory.CreateDirectory(saveDir);

            File.Copy(sourcePath, CookiesSavePath, overwrite: true);

            // Auto-set CookiesFilePath in config so future fetches use --cookies
            var config = ModContent.GetInstance<TerraVisionConfig>();
            if (config != null)
            {
                config.CookiesFilePath = CookiesSavePath;

                // Persist the config change to disk
                config.SaveChanges();
            }

            caller.Reply(
                $"✓ {browser} cookies saved successfully!\n" +
                "Bilibili captions should now work even while your browser is open.\n" +
                "Note: if you log out and back into Bilibili, run this command again " +
                "to refresh the saved cookies.",
                Color.LightGreen);

            TerraVision.instance.Logger.Info(
                $"[SetupCookies] Saved {browser} cookies from {sourcePath} to {CookiesSavePath}");
        }
        catch (IOException ex) when (ex.Message.Contains("used by another process") ||
                                     ex.HResult == unchecked((int)0x80070020))
        {
            // ERROR_SHARING_VIOLATION (0x20) — browser still has the file locked
            caller.Reply(
                $"Could not copy {browser} cookies — the browser still has the file locked.\n\n" +
                $"To fix this:\n" +
                $"  1. Close {browser} completely (check the system tray too)\n" +
                $"  2. Run  /tvsetup cookies  again\n\n" +
                "Alternatively, export your cookies manually:\n" +
                "  • Install the 'Get cookies.txt LOCALLY' extension in your browser\n" +
                "  • Export cookies for bilibili.com\n" +
                "  • Set the file path under 'Cookies File Path' in TerraVision mod settings",
                Color.Orange);
        }
        catch (UnauthorizedAccessException)
        {
            caller.Reply(
                $"Access denied reading {browser} cookies.\n" +
                "Try running Terraria as administrator, or export cookies manually " +
                "(see 'Cookies File Path' in TerraVision mod settings).",
                Color.Orange);
        }
        catch (Exception ex)
        {
            caller.Reply(
                $"Failed to copy cookies: {ex.Message}\n" +
                "You can export cookies manually using the 'Get cookies.txt LOCALLY' " +
                "browser extension and set the path in TerraVision mod settings.",
                Color.Orange);

            TerraVision.instance.Logger.Error($"[SetupCookies] Cookie copy failed: {ex}");
        }
    }

    /// <summary>
    /// In Auto mode, returns the first Chromium browser we can find an existing
    /// Cookies file for. Returns None if nothing is found.
    /// </summary>
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
            string path = CaptionFetcher.ResolveCookiesFilePath(b);
            if (path != null && File.Exists(path))
                return b;
        }

        return BrowserCookieSource.None;
    }
}