using LibVLCSharp.Shared;
using System;
using System.IO;
using Terraria;
using Terraria.ModLoader;
using TerraVision.Core;
using TerraVision.Core.VideoPlayer;

namespace TerraVision;

public class TerraVision : Mod
{
    public static TerraVision instance;
    public static LibVLC LibVLCInstance { get; private set; }
    private static bool _coreInitialized = false;

    public override void Load()
    {
        instance = this;

        if (!Main.dedServ)
            SetupLibVLC();
    }

    public override void PostSetupContent()
    {
        instance = this;
    }

    private static void SetupLibVLC()
    {
        if (_coreInitialized)
            return;

        string vlcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                      "My Games", "Terraria", "tModLoader", "VideoPlayerLibs");
        try
        {
            LibVLCSharp.Shared.Core.Initialize(vlcPath);
            _coreInitialized = true;

            string[] args =
            [
                "--no-quiet",
                "--network-caching=1000",
                "--file-caching=1000",
                "--avcodec-fast",
                "--aout=directsound,waveout,mmdevice",
            ];

            LibVLCInstance = new LibVLC(args);

            // Correct way to get VLC logs — event subscription, not constructor args
            LibVLCInstance.Log += (sender, e) =>
            {
                // Only capture warnings and errors to avoid spam
                if (e.Level >= LogLevel.Warning)
                    instance.Logger.Debug($"[VLC/{e.Level}] ({e.Module}) {e.Message}");
            };

            instance.Logger.Info("LibVLC initialized successfully!");
            instance.Logger.Info($"LibVLC version: {LibVLCInstance.Version}");
            LogAudioCapabilities();
        }
        catch (Exception ex)
        {
            instance.Logger.Error($"Failed to initialize LibVLC: {ex.Message}");
            instance.Logger.Error($"Stack trace: {ex.StackTrace}");
        }
    }

    private static void LogAudioCapabilities()
    {
        try
        {
            var audioOutputs = LibVLCInstance.AudioOutputs;
            instance.Logger.Info($"Available audio outputs: {audioOutputs.Length}");

            foreach (var output in audioOutputs)
            {
                instance.Logger.Info($"  - {output.Name}");
                instance.Logger.Info($"    Description: {output.Description}");
            }
        }
        catch (Exception ex)
        {
            instance.Logger.Warn($"Could not log audio capabilities: {ex.Message}");
        }
    }

    public override void Unload()
    {
        LibVLCInstance?.Dispose();
        LibVLCInstance = null;
        _coreInitialized = false;

        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TerrariaVideos", Name);
        if (System.IO.Directory.Exists(tempDir))
        {
            try
            {
                System.IO.Directory.Delete(tempDir, true);
            }
            catch { /* Ignore cleanup errors */ }
        }

        instance = null;
    }

    public override void HandlePacket(BinaryReader reader, int whoAmI)
    {
        ModContent.GetInstance<MultiplayerSyncSystem>()?.HandlePacket(reader, whoAmI);
    }

    /// <summary>
    /// TerraVision mod call API.
    ///
    /// All calls take a string command as the first argument and return either a
    /// result value or a bool indicating success. Unknown commands return false.
    ///
    /// Channel Registration:
    ///
    ///   "AddPresetChannel", int channelId, string query
    ///     Registers a preset channel (0–11) that plays videos matching the given
    ///     search query. Channel IDs 0–11 are preset; 12–99 are custom.
    ///     Returns: bool (true on success)
    ///     Example: Call("AddPresetChannel", 9, "terraria boss fights")
    ///
    ///   "AddPresetChannelEntry", int channelId, VideoChannelManager.ContentEntry entry
    ///     Registers a preset channel using a pre-built ContentEntry, giving full
    ///     control over ResultIndex, MaxResults, and ShowCaptions.
    ///     Returns: bool
    ///
    ///   "AddPresetChannelPlaylist", int channelId, string playlistUrl
    ///     Registers a preset channel that plays through a YouTube playlist.
    ///     Returns: bool
    ///
    /// Special Events:
    ///
    ///   "AddSpecialEvent", string videoUrl, Func&lt;bool&gt; condition
    ///     Registers a special event video that plays on all TVs when condition
    ///     returns true. Uses RepeatMode.OncePerSession and priority 0 by default.
    ///     Returns: bool
    ///
    ///   "AddSpecialEvent", string videoUrl, Func&lt;bool&gt; condition,
    ///                      VideoChannelManager.RepeatMode repeatMode, int priority
    ///     Full overload with explicit repeat mode and priority control.
    ///     Returns: bool
    ///
    /// Playback Control:
    ///
    ///   "PlayOnChannel", int channelId, string urlOrQuery
    ///     Immediately plays a URL or search query on the given channel.
    ///     Overrides any currently playing content on that channel.
    ///     Returns: bool
    ///
    ///   "StopChannel", int channelId
    ///     Stops playback on a channel regardless of how many TVs are watching.
    ///     Returns: bool
    ///
    ///   "TriggerSpecialEvent", string videoUrl
    ///     Manually triggers a special event broadcast on all TVs immediately,
    ///     bypassing condition and repeat checks.
    ///     Returns: bool
    ///
    ///   "StopSpecialEvent"
    ///     Stops the currently active special event and returns TVs to their
    ///     regular channels.
    ///     Returns: bool
    ///
    /// State Queries:
    ///
    ///   "IsReady"
    ///     Returns: bool — true if TerraVision is fully initialized and ready.
    ///
    ///   "IsChannelPlaying", int channelId
    ///     Returns: bool — true if the given channel has active playback.
    ///
    ///   "IsSpecialEventActive"
    ///     Returns: bool — true if a special broadcast is currently overriding TVs.
    ///
    ///   "GetChannelTitle", int channelId
    ///     Returns: string — the title of the currently playing video on that
    ///     channel, or null if nothing is playing or no title is available.
    ///
    ///   "GetChannelPosition", int channelId
    ///     Returns: float — playback position 0.0–1.0, or -1f if not playing.
    ///
    ///   "GetChannelDuration", int channelId
    ///     Returns: long — duration in milliseconds, or 0 if not available.
    ///
    ///   "GetChannelManager"
    ///     Returns: VideoChannelManager — direct reference for advanced use.
    ///     Intended for mods that need access beyond what the call API exposes.
    ///     Use with care; mutating internal state directly may cause issues.
    /// </summary>
    public override object Call(params object[] args)
    {
        if (args == null || args.Length == 0)
        {
            Logger.Warn("[ModCall] Called with no arguments");
            return false;
        }

        string command = args[0] as string;
        if (string.IsNullOrEmpty(command))
        {
            Logger.Warn("[ModCall] First argument must be a string command");
            return false;
        }

        try
        {
            var manager = ModContent.GetInstance<VideoChannelManager>();

            switch (command)
            {
                // Channel Registration

                case "AddPresetChannel":
                    {
                        int channelId = Convert.ToInt32(args[1]);
                        if (args[2] is not string query)
                        {
                            Logger.Warn("[ModCall] AddPresetChannel: query must be a string");
                            return false;
                        }
                        VideoChannelManager.AddPresetChannel(channelId, new VideoChannelManager.ChannelContent(query));
                        return true;
                    }

                case "AddPresetChannelEntry":
                    {
                        int channelId = Convert.ToInt32(args[1]);
                        if (args[2] is not VideoChannelManager.ContentEntry entry)
                        {
                            Logger.Warn("[ModCall] AddPresetChannelEntry: entry must be a ContentEntry");
                            return false;
                        }
                        VideoChannelManager.AddPresetChannel(channelId, new VideoChannelManager.ChannelContent(entry));
                        return true;
                    }

                case "AddPresetChannelPlaylist":
                    {
                        int channelId = Convert.ToInt32(args[1]);
                        if (args[2] is not string playlistUrl)
                        {
                            Logger.Warn("[ModCall] AddPresetChannelPlaylist: playlistUrl must be a string");
                            return false;
                        }
                        var content = new VideoChannelManager.ChannelContent(
                            new VideoChannelManager.ContentEntry(playlistUrl)
                        )
                        { IsPlaylist = true };
                        VideoChannelManager.AddPresetChannel(channelId, content);
                        return true;
                    }

                // Special Events

                case "AddSpecialEvent":
                    {
                        if (args[1] is not string videoUrl || args[2] is not Func<bool> condition)
                        {
                            Logger.Warn("[ModCall] AddSpecialEvent: videoUrl (string) and condition (Func<bool>) are required");
                            return false;
                        }
                        var repeatMode = args.Length > 3 ? (VideoChannelManager.RepeatMode)Convert.ToInt32(args[3]) : VideoChannelManager.RepeatMode.OncePerSession;
                        int priority = args.Length > 4 ? Convert.ToInt32(args[4]) : 0;
                        VideoChannelManager.AddSpecialEventVideo(videoUrl, condition, repeatMode, priority);
                        return true;
                    }

                // Playback Control

                case "PlayOnChannel":
                    {
                        if (manager == null) return false;
                        int channelId = Convert.ToInt32(args[1]);
                        if (args[2] is not string urlOrQuery)
                        {
                            Logger.Warn("[ModCall] PlayOnChannel: urlOrQuery must be a string");
                            return false;
                        }
                        var player = manager.GetOrCreateChannelPlayer(channelId);
                        if (player == null) return false;
                        Main.QueueMainThreadAction(() => player.Play(urlOrQuery, forcePlay: true));
                        return true;
                    }

                case "StopChannel":
                    {
                        if (manager == null) return false;
                        int channelId = Convert.ToInt32(args[1]);
                        var player = manager.GetChannelPlayer(channelId);
                        if (player == null) return false;
                        Main.QueueMainThreadAction(() => player.Stop());
                        return true;
                    }

                case "TriggerSpecialEvent":
                    {
                        if (manager == null) return false;
                        if (args[1] is not string videoUrl)
                        {
                            Logger.Warn("[ModCall] TriggerSpecialEvent: videoUrl must be a string");
                            return false;
                        }
                        // Register a one-shot AlwaysAvailable event and it will fire on the
                        // next CheckSpecialEvents tick — typically within one game frame
                        VideoChannelManager.AddSpecialEventVideo(
                            videoUrl,
                            () => true,
                            VideoChannelManager.RepeatMode.OncePerSession,
                            priority: 999
                        );
                        return true;
                    }

                case "StopSpecialEvent":
                    {
                        if (manager == null) return false;
                        Main.QueueMainThreadAction(() => manager.StopSpecialChannel());
                        return true;
                    }

                // State Queries

                case "IsReady":
                    return VideoUrlHelper.IsReady;

                case "IsChannelPlaying":
                    {
                        if (manager == null) return false;
                        int channelId = Convert.ToInt32(args[1]);
                        var player = manager.GetChannelPlayer(channelId);
                        return player != null && (player.IsPlaying || player.IsLoading || player.IsPreparing);
                    }

                case "IsSpecialEventActive":
                    return manager?.IsOverrideChannelActive() ?? false;

                case "GetChannelTitle":
                    {
                        if (manager == null) return null;
                        int channelId = Convert.ToInt32(args[1]);
                        var player = manager.GetChannelPlayer(channelId);
                        return player?.CurrentTitle;
                    }

                case "GetChannelPosition":
                    {
                        if (manager == null) return -1f;
                        int channelId = Convert.ToInt32(args[1]);
                        var player = manager.GetChannelPlayer(channelId);
                        return player != null && player.IsPlaying ? player.GetPosition() : -1f;
                    }

                case "GetChannelDuration":
                    {
                        if (manager == null) return 0L;
                        int channelId = Convert.ToInt32(args[1]);
                        var player = manager.GetChannelPlayer(channelId);
                        return player?.GetDuration() ?? 0L;
                    }

                case "GetChannelManager":
                    return manager;

                default:
                    Logger.Warn($"[ModCall] Unknown command: '{command}'");
                    return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ModCall] '{command}' threw an exception: {ex.Message}");
            return false;
        }
    }
}