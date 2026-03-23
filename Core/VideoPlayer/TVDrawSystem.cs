using Daybreak.Common.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using System;
using System.Collections.Generic;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using TerraVision.Content.Tiles.TVs;

namespace TerraVision.Core.VideoPlayer;

public class TVDrawSystem : ModSystem
{
    private static readonly List<TVTileEntity> _tvsToRender = [];

    private record struct TVCachedData(TVTileEntity Entity, bool NeedsStatic, Effect Shader);
    private record struct TVDrawEntry(Vector2 Position, Vector2 Size, Rectangle StaticArea, TVTileEntity Entity, bool NeedsStatic, Effect Shader);

    private static readonly List<TVCachedData> _tvCache = [];
    private static readonly List<TVDrawEntry> _tvData = [];

    private static readonly Dictionary<Effect, List<TVDrawEntry>> _shaderGroups = [];

    public static List<TVTileEntity> GetActiveTVs() => _tvsToRender;

    public override void OnModLoad()
    {
        On_Main.DrawNPCs += DrawTVScreens;
    }

    public override void UpdateUI(GameTime gameTime)
    {
        _tvsToRender.Clear();
        _tvCache.Clear();

        Rectangle screenBounds = new((int)Main.screenPosition.X - 200, (int)Main.screenPosition.Y - 200, Main.screenWidth + 400, Main.screenHeight + 400);

        foreach (var kvp in TileEntity.ByID)
        {
            if (kvp.Value is not TVTileEntity tvEntity || !tvEntity.IsOn)
                continue;

            int tileType = Main.tile[tvEntity.Position.X, tvEntity.Position.Y].TileType;

            if (TileLoader.GetTile(tileType) is BaseTVTile baseTVTile && !baseTVTile.DrawsScreenNormally())
                continue;

            Rectangle tvBounds = new(tvEntity.Position.X * 16, tvEntity.Position.Y * 16, tvEntity.Size.X * 16, tvEntity.Size.Y * 16);

            if (!screenBounds.Intersects(tvBounds))
                continue;

            _tvsToRender.Add(tvEntity);

            var player = tvEntity.GetVideoPlayer();
            bool needsStatic = player == null || (!player.IsPlaying && !player.IsLoading && !player.IsPreparing);

            Effect shader = GetShaderForTV(tvEntity);

            _tvCache.Add(new TVCachedData(tvEntity, needsStatic, shader));
        }
    }

    private void DrawTVScreens(On_Main.orig_DrawNPCs orig, Main self, bool behindTiles)
    {
        if (behindTiles || _tvCache.Count == 0)
        {
            orig(self, behindTiles);

            if(!behindTiles)
            {
                Main.spriteBatch.End(out var scope);

                TennaSystem.DrawTennas();

                Main.spriteBatch.Begin(scope);
            }

            return;
        }

        SpriteBatch sb = Main.spriteBatch;

        try
        {
            // Resolve screen-position-dependent areas now, at actual draw time
            _tvData.Clear();
            for (int i = 0; i < _tvCache.Count; i++)
            {
                TVCachedData cached = _tvCache[i];
                var (position, size, staticArea) = cached.Entity.CalculateScreenAreas();
                _tvData.Add(new TVDrawEntry(position, size, staticArea, cached.Entity, cached.NeedsStatic, cached.Shader));
            }

            sb.End(out var scope);

            TennaSystem.DrawTennas();

            bool noShaderBatchOpen = false;

            for (int i = 0; i < _tvData.Count; i++)
            {
                TVDrawEntry entry = _tvData[i];
                if (entry.NeedsStatic || entry.Shader != null)
                    continue;

                if (!noShaderBatchOpen)
                {
                    sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, Main.DefaultSamplerState, DepthStencilState.None, Main.Rasterizer);
                    noShaderBatchOpen = true;
                }

                try
                {
                    entry.Entity.DrawVideoOrLoading(sb, entry.Position, entry.Size);
                }
                catch (Exception ex)
                {
                    TerraVision.instance.Logger.Error($"Error drawing video for TV at {entry.Entity.Position}: {ex.Message}");
                }
            }

            if (noShaderBatchOpen)
                sb.End();

            _shaderGroups.Clear();

            for (int i = 0; i < _tvData.Count; i++)
            {
                TVDrawEntry entry = _tvData[i];
                if (entry.NeedsStatic || entry.Shader == null)
                    continue;

                if (!_shaderGroups.TryGetValue(entry.Shader, out var group))
                    _shaderGroups[entry.Shader] = group = [];

                group.Add(entry);
            }

            foreach (var (shader, group) in _shaderGroups)
            {
                try
                {
                    sb.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, Main.DefaultSamplerState, DepthStencilState.None, Main.Rasterizer, shader);

                    foreach (var entry in group)
                    {
                        try
                        {
                            ConfigureShaderForTV(shader, entry.Entity);
                            shader.CurrentTechnique.Passes[0].Apply();
                            entry.Entity.DrawVideoOrLoading(sb, entry.Position, entry.Size);
                        }
                        catch (Exception ex)
                        {
                            TerraVision.instance.Logger.Error($"Error drawing shaded video for TV at {entry.Entity.Position}: {ex.Message}");
                        }
                    }

                    sb.End();
                }
                catch (Exception ex)
                {
                    TerraVision.instance.Logger.Error($"Error in shader group batch: {ex.Message}");
                    try { sb.End(); } catch { }
                }
            }

            bool hasStatic = false;
            for (int i = 0; i < _tvData.Count; i++)
            {
                TVDrawEntry entry = _tvData[i];

                int tileType = Main.tile[entry.Entity.Position.X, entry.Entity.Position.Y].TileType;
                if (TileLoader.GetTile(tileType) is BaseTVTile baseTVTile && !baseTVTile.UsesStatic())
                    continue;

                if (entry.NeedsStatic) { hasStatic = true; break; }
            }

            if (hasStatic)
            {
                sb.Begin(scope);

                for (int i = 0; i < _tvData.Count; i++)
                {
                    TVDrawEntry entry = _tvData[i];
                    if (!entry.NeedsStatic)
                        continue;

                    var player = entry.Entity.GetVideoPlayer();

                    int tileType = Main.tile[entry.Entity.Position.X, entry.Entity.Position.Y].TileType;
                    if (TileLoader.GetTile(tileType) is BaseTVTile baseTVTile && !baseTVTile.UsesStatic())
                        continue;

                    try
                    {
                        entry.Entity.DrawStatic(sb, entry.StaticArea);
                    }
                    catch (Exception ex)
                    {
                        TerraVision.instance.Logger.Error($"Error drawing static for TV at {entry.Entity.Position}: {ex.Message}");
                    }
                }

                sb.End();
            }

            sb.Begin(scope);

            foreach(var tvEntity in _tvsToRender)
            {
                int tileType = Main.tile[tvEntity.Position.X, tvEntity.Position.Y].TileType;

                var player = tvEntity.GetVideoPlayer();
                bool needsStatic = player == null || (!player.IsPlaying && !player.IsLoading && !player.IsPreparing);

                if (TileLoader.GetTile(tileType) is BaseTVTile baseTVTile && (!needsStatic || baseTVTile.UsesStatic()) && ((player != null && !player.IsLoading && !player.IsPreparing) || baseTVTile.UsesLoadingSpinner()))
                    baseTVTile.PostDrawTVScreen(sb, tvEntity);
            }

            orig(self, behindTiles);
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Critical error in DrawTVScreens: {ex.Message}");
            orig(self, behindTiles);
        }
    }

    /// <summary>
    /// Returns the shader Effect for the given TV's tile type, or null if none.
    /// </summary>
    private static Effect GetShaderForTV(TVTileEntity tvEntity)
    {
        try
        {
            int tileType = Main.tile[tvEntity.Position.X, tvEntity.Position.Y].TileType;

            if (TileLoader.GetTile(tileType) is BaseTVTile baseTVTile)
                return baseTVTile.GetShaderEffect()?.Value;
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Error getting shader for TV at {tvEntity.Position}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Forwards per-TV shader parameters to the tile's ConfigureShader implementation.
    /// </summary>
    private static void ConfigureShaderForTV(Effect shader, TVTileEntity tvEntity)
    {
        try
        {
            int tileType = Main.tile[tvEntity.Position.X, tvEntity.Position.Y].TileType;

            if (TileLoader.GetTile(tileType) is BaseTVTile baseTVTile)
                baseTVTile.ConfigureShader(shader, tvEntity);
        }
        catch (Exception ex)
        {
            TerraVision.instance.Logger.Error($"Error configuring shader for TV at {tvEntity.Position}: {ex.Message}");
        }
    }
}