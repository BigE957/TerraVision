using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using System;
using System.Collections.Generic;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraVision.Core.VideoPlayer;
using TerraVision.UI.VideoPlayer;

namespace TerraVision.Content.Tiles.TVs;

/// <summary>
/// TV tile entity that displays video from shared channels.
/// All TVs on the same channel show the same video (perfectly synced).
/// </summary>
public class TVTileEntity : ModTileEntity
{
    public static readonly Dictionary<int, (Point TileSize, Rectangle ScreenOffsets)> TileData = [];

    public bool IsOn { get; set; } = false;
    public int Volume { get; set; } = 100;
    public Point16 Size { get; set; }

    private bool _hasStartedChannel = false;

    private bool loadedIn = false;

    public int CurrentChannel
    {
        get => _currentChannel;
        set
        {
            if (_currentChannel != value)
            {
                int oldChannel = _currentChannel;
                _currentChannel = value;
                _hasStartedChannel = false;
                OnChannelChanged(oldChannel, value);
            }
        }
    }
    private int _currentChannel = VideoChannelManager.DEFAULT_CHANNEL;

    public Point16 TilePosition { get; set; }

    public Point16 MediaPlayerPosition { get; set; } = Point16.Zero;

    public VideoPlayerCore GetVideoPlayer()
    {
        if(MediaPlayerPosition != Point16.Zero && TileEntity.TryGet<MediaPlayerEntity>(MediaPlayerPosition.X, MediaPlayerPosition.Y, out var entity))
            return entity.player;

        var manager = ModContent.GetInstance<VideoChannelManager>();
        return manager?.GetChannelPlayer(CurrentChannel);
    }

    public string GetDebugInfo() => $"TV Entity ID: {ID}, Position: {Position}, TilePosition: {TilePosition}, Channel: {CurrentChannel}, IsOn: {IsOn}, Volume: {Volume}";

    /// <summary>
    /// Handle channel change.
    /// </summary>
    private void OnChannelChanged(int oldChannel, int newChannel)
    {
        if (!IsOn) return;

        var manager = ModContent.GetInstance<VideoChannelManager>();
        manager.StopChannelIfUnused(oldChannel);

        if (VideoChannelManager.IsPresetChannel(newChannel))
        {
            manager.StartChannel(newChannel);
            _hasStartedChannel = true;
        }
    }

    /// <summary>
    /// Stop playback (stops channel if no other TVs watching).
    /// </summary>
    public void Stop()
    {
        var manager = ModContent.GetInstance<VideoChannelManager>();
        manager?.StopChannelIfUnused(CurrentChannel);
    }

    public override int Hook_AfterPlacement(int i, int j, int type, int style, int direction, int alternate)
    {
        TilePosition = new Point16(i, j);
        Size = ((BaseTVTile)TileLoader.GetTile(type)).GetTVDimensions();
        Volume = ModContent.GetInstance<TerraVisionConfig>()?.DefaultVolume ?? 100;

        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int tile = Main.tile[i, j].TileType;
            NetMessage.SendTileSquare(Main.myPlayer, i, j, TileData[tile].TileSize.X, TileData[tile].TileSize.Y);
            NetMessage.SendData(MessageID.TileEntityPlacement, -1, -1, null, i, j, Type);
            return -1;
        }

        return Place(i, j);
    }

    public override void Update()
    {
        if (!IsTileValidForEntity(Position.X, Position.Y))
        {
            Kill(Position.X, Position.Y);
            return;
        }

        if (!loadedIn)
        {
            if (!TileEntity.TryGet<MediaPlayerEntity>(MediaPlayerPosition.X, MediaPlayerPosition.Y, out var entity))
                MediaPlayerPosition = Point16.Zero;
            else if (!entity.ConnectedTVs.Contains(Position))
                entity.ConnectedTVs.Add(Position);

            loadedIn = true;
        }

        var manager = ModContent.GetInstance<VideoChannelManager>();
        if (manager == null) return;

        if(MediaPlayerPosition != Point16.Zero)
        {
            Tile tile = Main.tile[MediaPlayerPosition];
            if (!tile.HasTile || ModContent.GetModTile(tile.TileType) is not BaseMediaPlayerTile)
                MediaPlayerPosition = Point16.Zero;
        }

        if (!IsOn)
        {
            if (_hasStartedChannel)
            {
                manager.StopChannelIfUnused(CurrentChannel);
                _hasStartedChannel = false;
            }
            return;
        }

        if (MediaPlayerPosition == Point16.Zero && !manager.IsOverrideChannelActive() && VideoChannelManager.IsPresetChannel(CurrentChannel))
        {
            var player = manager.GetChannelPlayer(CurrentChannel);

            if (player == null || (!player.IsPlaying && !player.IsLoading && !player.IsPreparing))
            {
                manager.StartChannel(CurrentChannel);
                _hasStartedChannel = true;
            }
            else if (!_hasStartedChannel)
                _hasStartedChannel = true;
        }
    }

    public override void SaveData(TagCompound tag)
    {
        tag["channel"] = CurrentChannel;
        tag["posX"] = TilePosition.X;
        tag["posY"] = TilePosition.Y;
        tag["isOn"] = IsOn;
        tag["volume"] = Volume;
        tag["playerX"] = MediaPlayerPosition.X;
        tag["playerY"] = MediaPlayerPosition.Y;
    }

    public override void LoadData(TagCompound tag)
    {
        CurrentChannel = tag.GetInt("channel");
        TilePosition = new Point16(tag.GetShort("posX"), tag.GetShort("posY"));
        IsOn = tag.GetBool("isOn");
        Volume = tag.GetInt("volume");
        MediaPlayerPosition = new Point16(tag.GetShort("playerX"), tag.GetShort("playerY"));

        loadedIn = false;
    }

    public override bool IsTileValidForEntity(int x, int y)
    {
        Tile tile = Main.tile[x, y];
        return tile.HasTile && TileData.ContainsKey(tile.TileType);
    }

    /// <summary>
    /// Render the video on this TV.
    /// </summary>
    public void DrawVideoOrLoading(SpriteBatch spriteBatch, Vector2 position, Vector2 size)
    {
        if (!IsOn) return;

        var player = GetVideoPlayer();
        if (player == null) return;

        int tileType = Main.tile[Position.X, Position.Y].TileType;
        if (TileLoader.GetTile(tileType) is BaseTVTile baseTVTile && !baseTVTile.UsesLoadingSpinner() && (player.IsLoading || player.IsPreparing))
            return;

        player.Draw(spriteBatch, position, size, ExampleVideoUISystem.Background.Value);
    }

    private static Asset<Texture2D> _staticTexture;
    private Vector2 _staticOffset;

    public void DrawStatic(SpriteBatch spriteBatch, Rectangle screenArea, float opacity = 1f)
    {
        try
        {
            _staticTexture ??= ModContent.Request<Texture2D>("TerraVision/Assets/ExtraTextures/StaticNoise");

            if ((int)(Main.GlobalTimeWrappedHourly * 60) % 3 == 0)
                _staticOffset = new(Main.rand.Next(0, Math.Max(1, _staticTexture.Width() - screenArea.Width)), Main.rand.Next(0, Math.Max(1, _staticTexture.Height() - screenArea.Height)));

            Rectangle sourceRect = new((int)_staticOffset.X, (int)_staticOffset.Y, Math.Min(_staticTexture.Width(), screenArea.Width), Math.Min(_staticTexture.Height(), screenArea.Height));

            spriteBatch.Draw(_staticTexture.Value, screenArea, sourceRect, Color.White * opacity);
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Error drawing static: {ex.Message}");
        }
    }

    private Vector2? _cachedVideoPosition;
    private Vector2? _cachedVideoSize;
    private Rectangle? _cachedStaticArea;
    private float _lastCachedZoom = -1f;
    private Vector2 _lastCachedScreenPos;

    public (Vector2 Position, Vector2 Size, Rectangle StaticArea) CalculateScreenAreas()
    {
        bool cacheValid = _lastCachedZoom == Main.GameViewMatrix.Zoom.X &&
                          _lastCachedScreenPos == Main.screenPosition;

        if (cacheValid && _cachedVideoPosition.HasValue &&
            _cachedVideoSize.HasValue && _cachedStaticArea.HasValue)
        {
            return (_cachedVideoPosition.Value, _cachedVideoSize.Value, _cachedStaticArea.Value);
        }

        int tileType = Main.tile[Position.X, Position.Y].TileType;
        if (!TileData.TryGetValue(tileType, out var tileInfo))
            return (Vector2.Zero, Vector2.Zero, Rectangle.Empty);

        Rectangle worldArea = new(
            Position.X * 16, Position.Y * 16,
            tileInfo.TileSize.X * 16, tileInfo.TileSize.Y * 16
        );

        worldArea.X += tileInfo.ScreenOffsets.X;
        worldArea.Y += tileInfo.ScreenOffsets.Y;
        worldArea.Width -= tileInfo.ScreenOffsets.X;
        worldArea.Height -= tileInfo.ScreenOffsets.Y;
        worldArea.Width += tileInfo.ScreenOffsets.Width;
        worldArea.Height += tileInfo.ScreenOffsets.Height;

        Vector2 worldPos = new(worldArea.X, worldArea.Y);
        Vector2 screenPos = worldPos - Main.screenPosition;
        float zoom = Main.GameViewMatrix.Zoom.X;
        Vector2 screenCenter = new(Main.screenWidth / 2f, Main.screenHeight / 2f);

        Vector2 finalPos = (screenPos - screenCenter) * zoom + screenCenter;
        Vector2 finalSize = new Vector2(worldArea.Width, worldArea.Height) * zoom;

        Rectangle staticArea = new(
            (int)screenPos.X, (int)screenPos.Y, worldArea.Width, worldArea.Height
        );

        _cachedVideoPosition = finalPos;
        _cachedVideoSize = finalSize;
        _cachedStaticArea = staticArea;
        _lastCachedZoom = zoom;
        _lastCachedScreenPos = Main.screenPosition;

        return (finalPos, finalSize, staticArea);
    }

    public override void OnKill()
    {
        var manager = ModContent.GetInstance<VideoChannelManager>();
        manager?.StopChannelIfUnused(CurrentChannel);
    }
}