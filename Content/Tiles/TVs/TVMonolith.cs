using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.Enums;
using Terraria.GameContent;
using Terraria.GameContent.ObjectInteractions;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;
using TerraVision.Common;
using TerraVision.Content.Items;
using TerraVision.Content.Skies;
using TerraVision.Core.VideoPlayer;
using TerraVision.UI.VideoPlayer;

namespace TerraVision.Content.Tiles.TVs
{
    public class TVMonolith : BaseTVTile
    {
        public override Point16 GetTVDimensions() => new(2, 3);

        public override string GetTVStyleName() => "TV Monolith";

        public override bool DrawsScreenNormally() => false;

        public static int TileWidth => 2;
        public static int TileHeight => 3;
        public static int AnimationFrameCount => 1;
        public static int AnimationDelay => 8;
        public static int CursorItemType => ModContent.ItemType<TVMonolithItem>();
        private static Asset<Texture2D> GlowMask;
        private static SoundStyle? RightClickSound = SoundID.Mech;
        public int EnabledFrameY => AnimationFrameHeight;

        public override void SetStaticDefaults()
        {
            if (!Main.dedServ)
                GlowMask = ModContent.Request<Texture2D>($"{Texture}_Glow");

            RegisterItemDrop(ModContent.ItemType<TVMonolithItem>());
            Main.tileFrameImportant[Type] = true;
            TileID.Sets.HasOutlines[Type] = true;
            TileObjectData.newTile.CopyFrom(TileObjectData.Style2xX);
            TileObjectData.newTile.Height = 3;
            TileObjectData.newTile.Origin = new Point16(0, 2);
            TileObjectData.newTile.CoordinateHeights = new[] { 16, 16, 18 };
            TileObjectData.newTile.LavaDeath = false;
            TileObjectData.newTile.UsesCustomCanPlace = true;
            TileObjectData.newTile.AnchorBottom = new AnchorData(AnchorType.SolidTile | AnchorType.SolidWithTop, 2, 0);

            AnimationFrameHeight = TileObjectData.newTile.CoordinateFullHeight;

            ModTileEntity te = ModContent.GetInstance<TVTileEntity>();
            TileObjectData.newTile.HookPostPlaceMyPlayer = new PlacementHook(te.Hook_AfterPlacement, -1, 0, true);

            TileObjectData.addTile(Type);

            TVTileEntity.TileData[Type] = (new(2, 3), Rectangle.Empty);

            AddMapEntry(new Color(16, 50, 64));

            DustType = DustID.Iron;
        }

        public override bool HasSmartInteract(int i, int j, SmartInteractScanSettings settings) => true;

        public override void MouseOver(int i, int j)
        {
            Player player = Main.LocalPlayer;
            player.noThrow = 2;
            player.cursorItemIconEnabled = CursorItemType != 0;
            player.cursorItemIconID = CursorItemType;
        }

        public override bool RightClick(int i, int j)
        {
            TVTileEntity tvEntity = TerraVisionUtils.FindTileEntity<TVTileEntity>(i, j, GetTVDimensions().X, GetTVDimensions().Y, 16);

            if (tvEntity == null)
            {
                Main.NewText("No TV entity found!", Color.Red);
                return false;
            }

            Player player = Main.LocalPlayer;

            if (player.HeldItem.type == ModContent.ItemType<TVRemote>())
            {
                ModContent.GetInstance<TVRemoteUISystem>().OpenUI(tvEntity);
                return true;
            }

            // Without remote, toggle on/off
            ToggleMonolith(i, j);
            SoundEngine.PlaySound(RightClickSound, new Vector2(i * 16, j * 16));
            bool wasOn = tvEntity.IsOn;
            tvEntity.IsOn = !tvEntity.IsOn;

            if (wasOn && !tvEntity.IsOn)
            {
                var manager = ModContent.GetInstance<VideoChannelManager>();
                manager?.StopChannelIfUnused(tvEntity.CurrentChannel);
            }

            return true;
        }

        public override void HitWire(int i, int j)
        {
            ToggleMonolith(i, j);
        }

        private void ToggleMonolith(int i, int j)
        {
            var tile = Main.tile[i, j];
            var width = 18 * TileWidth;
            var height = AnimationFrameHeight;
            var leftTopI = i - ((tile.TileFrameX % width) / 18);
            var leftTopJ = j - ((tile.TileFrameY % height) / 18);
            var enabled = tile.TileFrameY >= height;

            for (int o = 0; o < TileWidth; o++)
            {
                for (int p = 0; p < TileHeight; p++)
                {
                    var relI = leftTopI + o;
                    var relJ = leftTopJ + p;
                    var relTile = Main.tile[relI, relJ];

                    if (enabled) relTile.TileFrameY -= (short)height;
                    else relTile.TileFrameY += (short)height;

                    if (Wiring.running)
                    {
                        Wiring.SkipWire(relI, relJ);
                    }
                }
            }

            if (Main.netMode != NetmodeID.SinglePlayer)
            {
                NetMessage.SendTileSquare(-1, leftTopI, leftTopJ, TileWidth, TileHeight);
            }
        }

        public override void NearbyEffects(int i, int j, bool closer)
        {
            var enabled = Main.tile[i, j].TileFrameY >= EnabledFrameY;

            if (!enabled)
                return;

            if (Main.LocalPlayer is not null && Main.LocalPlayer.active && Main.LocalPlayer.TryGetModPlayer<TVMonolithPlayer>(out var player))
            {
                player.TVMonolith = 30;
                int left = i - Main.tile[i, j].TileFrameX % (2 * 16) / 16;
                int top = j - Main.tile[i, j].TileFrameY % (3* 16) / 16;
                TVSky.EntityPosition = new(left, top);
            }
        }

        public static Color GetGlowMaskDrawColor(int i, int j) =>  Color.White;

        public override void AnimateTile(ref int frame, ref int frameCounter)
        {
            frameCounter++;
            if (frameCounter >= AnimationDelay)
            {
                frameCounter = 0;
                if (++frame >= AnimationFrameCount)
                {
                    frame = 0;
                }
            }
        }

        public override bool PreDraw(int i, int j, SpriteBatch spriteBatch)
        {
            if (Main.tile[i, j].IsTileInvisible)
                return false;

            var tile = Main.tile[i, j];
            var texture = TextureAssets.Tile[Type].Value;

            var zero = Main.drawToScreen ? Vector2.Zero : new(Main.offScreenRange, Main.offScreenRange);
            var drawPos = new Vector2(i * 16, j * 16) - Main.screenPosition + zero;

            var animateFrameOffset = (tile.TileFrameY >= EnabledFrameY) ? Main.tileFrame[Type] * AnimationFrameHeight : 0;
            var isHeight18Pixels = (tile.TileFrameY % AnimationFrameHeight) >= (18 * (TileHeight - 1));
            var height = isHeight18Pixels ? 18 : 16;

            var rect = new Rectangle(tile.TileFrameX, tile.TileFrameY + animateFrameOffset, 16, height);

            var drawColor = Lighting.GetColor(i, j);
            var glowColor = GetGlowMaskDrawColor(i, j);

            Main.spriteBatch.Draw(texture, drawPos, rect, drawColor, 0f, default, 1f, SpriteEffects.None, 0f);
            if (GlowMask != null)
                Main.spriteBatch.Draw(GlowMask.Value, drawPos, rect, glowColor, 0f, default, 1f, SpriteEffects.None, 0f);

            // 02FEB2025: Ozzatron: code lifted from https://github.com/CalamityTeam/CalamityModPublic/pull/77
            // transplanted into base monolith as part of manual cherry pick merge
            //
            // Draws the Smart Cursor Highlight.
            Color highlightColor;
            var highlight = TextureAssets.HighlightMask[Type];
            if (highlight != null && highlight.IsLoaded && Main.InSmartCursorHighlightArea(i, j, out var actuallySelected))
            {
                int avgBrightness = (drawColor.R + drawColor.G + drawColor.B) / 3;
                if (avgBrightness > 10)
                {
                    highlightColor = Colors.GetSelectionGlowColor(actuallySelected, avgBrightness);
                    Main.spriteBatch.Draw(highlight.Value, new Vector2(i * 16 - (int)Main.screenPosition.X, j * 16 - (int)Main.screenPosition.Y) + zero, rect, highlightColor, 0f, default(Vector2), 1f, SpriteEffects.None, 0f);
                }
            }

            return false;
        }
    }

    public class TVMonolithItem : ModItem
    {
        public override void SetDefaults()
        {
            Item.DefaultToPlaceableTile(ModContent.TileType<TVMonolith>());
            Item.value = Item.sellPrice(silver: 1);
            Item.rare = ItemRarityID.LightRed;
            //Item.accessory = true; Needs to be hooked up to a player so... Can't really work as an equip. At least not yet.
            //Item.vanity = true; 
        }
    }

    public class TVMonolithPlayer : ModPlayer
    {
        public int TVMonolith = 0;

        public override void PostUpdateMiscEffects()
        {
            if (TVMonolith > 0)
                TVMonolith--;
        }
    }
}
