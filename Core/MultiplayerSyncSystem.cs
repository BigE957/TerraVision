using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraVision.Core.VideoPlayer;

namespace TerraVision.Core;

/// <summary>
/// Handles all multiplayer synchronisation for TerraVision channels.
///
/// Architecture overview:
///
///   Singleplayer / host:
///     VideoChannelManager fires Play/Pause/Resume/Stop as normal.
///     TVSyncSystem intercepts these via events and broadcasts to all clients.
///
///   Non-host client:
///     Local VideoChannelManager is bypassed for Play — instead a PlayRequest
///     packet is sent to the server, which executes it and broadcasts the result.
///     Pause/Resume/Stop requests are similarly relayed rather than applied locally.
///
///   Late joiner:
///     On player join, the server sends a SyncState packet for every active channel
///     containing the current URL and position, so the client can seek to the right spot.
///
///   Ready synchronisation:
///     When the server sends LoadVideo, each client loads the URL and reports back
///     VideoReady when PlaybackReady fires. The server waits up to ReadyTimeoutSeconds
///     for all clients, then sends BeginPlay regardless. Clients who weren't ready
///     will start playing from position 0, which is acceptable.
///
/// Packet routing:
///   Packets are dispatched from TerraVision.HandlePacket (the Mod class).
///   Call TVSyncSystem.HandlePacket from there to route to the correct handler.
///
/// Packet type IDs (single byte, unique within this mod):
///   0  — PlayRequest    client -> server
///   1  — LoadVideo      server -> all clients
///   2  — VideoReady     client -> server
///   3  — BeginPlay      server -> all clients
///   4  — SyncState      server -> joining client
///   5  — PauseResume    server -> all clients
///   6  — StopChannel    server -> all clients
/// </summary>
public class MultiplayerSyncSystem : ModSystem
{
    public const byte PKT_PLAY_REQUEST = 0;
    public const byte PKT_LOAD_VIDEO = 1;
    public const byte PKT_VIDEO_READY = 2;
    public const byte PKT_BEGIN_PLAY = 3;
    public const byte PKT_SYNC_STATE = 4;
    public const byte PKT_PAUSE_RESUME = 5;
    public const byte PKT_STOP_CHANNEL = 6;

    private const double ReadyTimeoutSeconds = 15.0;

    private class PendingLoad
    {
        public string Url { get; init; }
        public Guid RequestId { get; init; }
        public HashSet<int> PendingPlayers { get; init; } = [];
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    }

    // channelId -> pending load state (server only)
    private readonly Dictionary<int, PendingLoad> _pendingLoads = [];

    // channelId -> (url, startedAt) for late-joiner sync (server only)
    private readonly Dictionary<int, (string Url, DateTime StartedAt)> _activeChannels = [];

    #region Loading + Unloading

    public override void OnWorldLoad()
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            return;

        _pendingLoads.Clear();
        _activeChannels.Clear();

        HookChannelManagerEvents();
    }

    public override void OnWorldUnload()
    {
        _pendingLoads.Clear();
        _activeChannels.Clear();

        UnhookChannelManagerEvents();
    }

    private void HookChannelManagerEvents()
    {
        var manager = ModContent.GetInstance<VideoChannelManager>();
        if (manager == null) return;

        manager.OnChannelPlay += OnChannelPlayFired;
        manager.OnChannelPause += OnChannelPauseFired;
        manager.OnChannelResume += OnChannelResumeFired;
        manager.OnChannelStop += OnChannelStopFired;
    }

    private void UnhookChannelManagerEvents()
    {
        var manager = ModContent.GetInstance<VideoChannelManager>();
        if (manager == null) return;

        manager.OnChannelPlay -= OnChannelPlayFired;
        manager.OnChannelPause -= OnChannelPauseFired;
        manager.OnChannelResume -= OnChannelResumeFired;
        manager.OnChannelStop -= OnChannelStopFired;
    }

    #endregion

    #region VideoChannelManager Event handlers

    private void OnChannelPlayFired(int channelId, string url)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            return;
        BroadcastLoadVideo(channelId, url);
    }

    private void OnChannelPauseFired(int channelId)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            return;
        BroadcastPauseResume(channelId, paused: true);
    }

    private void OnChannelResumeFired(int channelId)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            return;
        BroadcastPauseResume(channelId, paused: false);
    }

    private void OnChannelStopFired(int channelId)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            return;
        BroadcastStopChannel(channelId);
    }

    #endregion

    #region Timeout check

    private int _tickCounter = 0;
    private const int TimeoutCheckInterval = 60;

    public override void PostUpdateEverything()
    {
        if (Main.netMode != NetmodeID.Server)
            return;

        _tickCounter++;
        if (_tickCounter % TimeoutCheckInterval != 0)
            return;

        var timedOut = _pendingLoads
            .Where(kvp => (DateTime.UtcNow - kvp.Value.StartedAt).TotalSeconds >= ReadyTimeoutSeconds)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (int channelId in timedOut)
        {
            var pending = _pendingLoads[channelId];
            TerraVision.instance.Logger.Info($"[Sync] Channel {channelId}: ready timeout — starting anyway ({pending.PendingPlayers.Count} player(s) not ready: {string.Join(", ", pending.PendingPlayers)})");

            SendBeginPlay(channelId, pending.RequestId);
            _pendingLoads.Remove(channelId);
        }
    }

    #endregion

    #region Packet sending

    private void BroadcastLoadVideo(int channelId, string url)
    {
        var requestId = Guid.NewGuid();

        _activeChannels[channelId] = (url, DateTime.UtcNow);

        var pending = new PendingLoad
        {
            Url = url,
            RequestId = requestId
        };

        for (int i = 0; i < Main.maxPlayers; i++)
        {
            if (Main.player[i].active && i != Main.myPlayer)
                pending.PendingPlayers.Add(i);
        }

        _pendingLoads[channelId] = pending;

        if (pending.PendingPlayers.Count == 0)
        {
            _pendingLoads.Remove(channelId);
            return;
        }

        TerraVision.instance.Logger.Info($"[Sync] Broadcasting LoadVideo for channel {channelId}: {url} (requestId {requestId})");

        var packet = TerraVision.instance.GetPacket();
        packet.Write(PKT_LOAD_VIDEO);
        packet.Write(channelId);
        packet.Write(url);
        packet.Write(requestId.ToString());
        packet.Send(); // server -> all clients
    }

    private static void SendBeginPlay(int channelId, Guid requestId)
    {
        TerraVision.instance.Logger.Info($"[Sync] Sending BeginPlay for channel {channelId}");

        var packet = TerraVision.instance.GetPacket();
        packet.Write(PKT_BEGIN_PLAY);
        packet.Write(channelId);
        packet.Write(requestId.ToString());
        packet.Send(); // server -> all clients
    }

    private static void BroadcastPauseResume(int channelId, bool paused)
    {
        var packet = TerraVision.instance.GetPacket();
        packet.Write(PKT_PAUSE_RESUME);
        packet.Write(channelId);
        packet.Write(paused);
        packet.Send(); // server -> all clients
    }

    private void BroadcastStopChannel(int channelId)
    {
        _activeChannels.Remove(channelId);
        _pendingLoads.Remove(channelId);

        var packet = TerraVision.instance.GetPacket();
        packet.Write(PKT_STOP_CHANNEL);
        packet.Write(channelId);
        packet.Send(); // server -> all clients
    }

    private static void SendSyncState(int channelId, string url, long positionMs, int toPlayer)
    {
        var packet = TerraVision.instance.GetPacket();
        packet.Write(PKT_SYNC_STATE);
        packet.Write(channelId);
        packet.Write(url);
        packet.Write(positionMs);
        packet.Send(toClient: toPlayer); // server -> specific client
    }

    public void OnPlayerJoined(int playerId)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            return;

        var manager = ModContent.GetInstance<VideoChannelManager>();
        if (manager == null)
            return;

        foreach (var kvp in _activeChannels)
        {
            int channelId = kvp.Key;
            string url = kvp.Value.Url;

            var player = manager.GetChannelPlayer(channelId);
            if (player == null || (!player.IsPlaying && !player.IsPaused))
                continue;

            long positionMs = (long)(player.GetPosition() * player.GetDuration());

            TerraVision.instance.Logger.Info($"[Sync] Sending SyncState to player {playerId}: channel {channelId} @ {positionMs}ms");

            SendSyncState(channelId, url, positionMs, toPlayer: playerId);
        }
    }

    #endregion

    #region Packet receiving

    public bool HandlePacket(BinaryReader reader, int sender)
    {
        byte packetType = reader.ReadByte();

        switch (packetType)
        {
            case PKT_PLAY_REQUEST:
                return HandlePlayRequest(reader, sender);
            case PKT_LOAD_VIDEO:
                return HandleLoadVideo(reader);
            case PKT_VIDEO_READY:
                return HandleVideoReady(reader, sender);
            case PKT_BEGIN_PLAY:
                return HandleBeginPlay(reader);
            case PKT_SYNC_STATE:
                return HandleSyncState(reader);
            case PKT_PAUSE_RESUME:
                return HandlePauseResume(reader);
            case PKT_STOP_CHANNEL:
                return HandleStopChannel(reader);

            default:
                TerraVision.instance.Logger.Warn($"[Sync] Unknown packet type: {packetType}");
                return false;
        }
    }

    // client -> server: non-host client wants to play a URL on a channel
    private static bool HandlePlayRequest(BinaryReader reader, int sender)
    {
        int channelId = reader.ReadInt32();
        string url = reader.ReadString();

        if (Main.netMode != NetmodeID.Server)
            return true;

        TerraVision.instance.Logger.Info($"[Sync] PlayRequest from player {sender}: channel {channelId}, url={url}");

        var manager = ModContent.GetInstance<VideoChannelManager>();
        if (manager == null)
            return true;

        var player = manager.GetOrCreateChannelPlayer(channelId);
        if (player == null)
            return true;

        // Executing Play locally triggers OnChannelPlay -> BroadcastLoadVideo to all clients
        Main.QueueMainThreadAction(() => player.Play(url, forcePlay: true));
        return true;
    }

    // server -> all clients: begin loading, pause, wait for BeginPlay
    private static bool HandleLoadVideo(BinaryReader reader)
    {
        int channelId = reader.ReadInt32();
        string url = reader.ReadString();
        var requestId = Guid.Parse(reader.ReadString());

        if (Main.netMode != NetmodeID.MultiplayerClient)
            return true;

        TerraVision.instance.Logger.Info($"[Sync] LoadVideo received: channel {channelId}, url={url}");

        var manager = ModContent.GetInstance<VideoChannelManager>();
        if (manager == null)
            return true;

        var player = manager.GetOrCreateChannelPlayer(channelId);
        if (player == null)
            return true;

        void readyHandler(object s, EventArgs e)
        {
            player.PlaybackReady -= readyHandler;
            player.Pause();

            // client -> server: report ready
            var packet = TerraVision.instance.GetPacket();
            packet.Write(PKT_VIDEO_READY);
            packet.Write(channelId);
            packet.Write(requestId.ToString());
            packet.Send(); // client always sends to server with no parameters

            TerraVision.instance.Logger.Info($"[Sync] VideoReady sent for channel {channelId} (requestId {requestId})");
        }

        player.PlaybackReady += readyHandler;
        Main.QueueMainThreadAction(() => player.Play(url, forcePlay: true));
        return true;
    }

    // client -> server: check if all players ready, send BeginPlay if so
    private bool HandleVideoReady(BinaryReader reader, int sender)
    {
        int channelId = reader.ReadInt32();
        var requestId = Guid.Parse(reader.ReadString());

        if (Main.netMode != NetmodeID.Server)
            return true;

        if (!_pendingLoads.TryGetValue(channelId, out var pending) || pending.RequestId != requestId)
        {
            TerraVision.instance.Logger.Debug($"[Sync] VideoReady from player {sender} for stale requestId {requestId} — ignored");
            return true;
        }

        pending.PendingPlayers.Remove(sender);
        TerraVision.instance.Logger.Info($"[Sync] Player {sender} ready for channel {channelId} ({pending.PendingPlayers.Count} remaining)");

        if (pending.PendingPlayers.Count == 0)
        {
            _pendingLoads.Remove(channelId);
            SendBeginPlay(channelId, requestId);
        }

        return true;
    }

    // server -> all clients: resume from paused state
    private static bool HandleBeginPlay(BinaryReader reader)
    {
        int channelId = reader.ReadInt32();
        _ = reader.ReadString(); // requestId — consumed but not needed client-side

        if (Main.netMode != NetmodeID.MultiplayerClient)
            return true;

        TerraVision.instance.Logger.Info($"[Sync] BeginPlay received for channel {channelId}");

        var manager = ModContent.GetInstance<VideoChannelManager>();
        var player = manager?.GetChannelPlayer(channelId);
        if (player == null)
            return true;

        Main.QueueMainThreadAction(() => player.Resume());
        return true;
    }

    // server -> joining client: load URL and seek to position
    private static bool HandleSyncState(BinaryReader reader)
    {
        int channelId = reader.ReadInt32();
        string url = reader.ReadString();
        long positionMs = reader.ReadInt64();

        if (Main.netMode != NetmodeID.MultiplayerClient)
            return true;

        TerraVision.instance.Logger.Info($"[Sync] SyncState received: channel {channelId} @ {positionMs}ms");

        var manager = ModContent.GetInstance<VideoChannelManager>();
        if (manager == null)
            return true;

        var player = manager.GetOrCreateChannelPlayer(channelId);
        if (player == null)
            return true;

        void readyHandler(object s, EventArgs e)
        {
            player.PlaybackReady -= readyHandler;

            long duration = player.GetDuration();
            if (duration > 0 && positionMs > 0)
            {
                float position = Math.Clamp((float)positionMs / duration, 0f, 1f);
                player.Seek(position);
                TerraVision.instance.Logger.Info($"[Sync] Sought channel {channelId} to {positionMs}ms ({position:P0})");
            }
        }

        player.PlaybackReady += readyHandler;
        Main.QueueMainThreadAction(() => player.Play(url, forcePlay: true));
        return true;
    }

    // server -> all clients: pause or resume
    private static bool HandlePauseResume(BinaryReader reader)
    {
        int channelId = reader.ReadInt32();
        bool paused = reader.ReadBoolean();

        if (Main.netMode != NetmodeID.MultiplayerClient)
            return true;

        var manager = ModContent.GetInstance<VideoChannelManager>();
        var player = manager?.GetChannelPlayer(channelId);
        if (player == null) return true;

        Main.QueueMainThreadAction(() =>
        {
            if (paused) player.Pause();
            else player.Resume();
        });

        return true;
    }

    // server -> all clients: stop a channel
    private static bool HandleStopChannel(BinaryReader reader)
    {
        int channelId = reader.ReadInt32();

        if (Main.netMode != NetmodeID.MultiplayerClient)
            return true;

        var manager = ModContent.GetInstance<VideoChannelManager>();
        var player = manager?.GetChannelPlayer(channelId);

        Main.QueueMainThreadAction(() => player?.Stop());
        return true;
    }

    #endregion

    /// <summary>
    /// Sends a play request to the server from a non-host client.
    /// Call this instead of player.Play() on non-host clients for channel playback.
    /// </summary>
    public static void RequestPlay(int channelId, string url)
    {
        if (Main.netMode != NetmodeID.MultiplayerClient)
            return;

        var packet = TerraVision.instance.GetPacket();
        packet.Write(PKT_PLAY_REQUEST);
        packet.Write(channelId);
        packet.Write(url);
        packet.Send();
    }
}