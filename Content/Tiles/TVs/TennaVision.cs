using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.DataStructures;
using Terraria.Enums;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;
using TerraVision.Common;

namespace TerraVision.Content.Tiles.TVs;

public class TennaVision : BaseTVTile
{
    private static Asset<Texture2D> Overlay;

    public override Point16 GetTVDimensions() => new(12, 18);

    public override string GetTVStyleName() => "TennaVision";

    public override bool UsesStatic() => false;

    public override bool UsesLoadingSpinner() => false;

    public override void SetStaticDefaults()
    {
        Point16 dimensions = GetTVDimensions();

        Main.tileFrameImportant[Type] = true;
        Main.tileNoAttach[Type] = true;
        Main.tileLavaDeath[Type] = true;

        TileObjectData.newTile.CopyFrom(TileObjectData.Style3x2);
        TileObjectData.newTile.Width = dimensions.X;
        TileObjectData.newTile.Height = dimensions.Y;
        TileObjectData.newTile.CoordinateHeights = new int[dimensions.Y];
        for (int i = 0; i < dimensions.Y; i++)
        {
            TileObjectData.newTile.CoordinateHeights[i] = 16;
        }
        TileObjectData.newTile.CoordinateWidth = 16;
        TileObjectData.newTile.CoordinatePadding = 0;
        TileObjectData.newTile.Origin = new Point16(dimensions.X / 2, dimensions.Y - 1);
        TileObjectData.newTile.AnchorBottom = new AnchorData(AnchorType.SolidTile | AnchorType.SolidWithTop, TileObjectData.newTile.Width, 0);
        TileObjectData.newTile.UsesCustomCanPlace = true;
        TileObjectData.newTile.LavaDeath = true;

        //AnimationFrameHeight = dimensions.Y * 16;

        ModTileEntity te = ModContent.GetInstance<TVTileEntity>();
        TileObjectData.newTile.HookPostPlaceMyPlayer = new PlacementHook(te.Hook_AfterPlacement, -1, 0, true);

        TileObjectData.addTile(Type);

        AddMapEntry(new Color(255, 0, 0), CreateMapEntryName());

        DustType = DustID.RedMoss;

        TVTileEntity.TileData[Type] = (GetTVDimensions().ToPoint(), new Rectangle(50, 88, -60, -154));

        Overlay = ModContent.Request<Texture2D>("TerraVision/Content/Tiles/TVs/TennaVisionHollow");
    }

    public override void NearbyEffects(int i, int j, bool closer)
    {
        if (closer)
        {
            Tile tile = Main.tile[i, j];
            Point16 dimensions = GetTVDimensions();

            int left = i - tile.TileFrameX % (dimensions.X * 16) / 16;
            int top = j - tile.TileFrameY % (dimensions.Y * 16) / 16;

            if (i == left && top == j)
            {
                TennaSystem.Tennas.TryAdd(new(i, j), 3);
            }
        }
    }

    public override void KillMultiTile(int i, int j, int frameX, int frameY)
    {
        TennaSystem.Tennas.Remove(new(i, j));
    }

    public override bool PreDraw(int i, int j, SpriteBatch spriteBatch) => false;

    public override void PostDrawTVScreen(SpriteBatch spritebatch, TVTileEntity tvEntity)
    {
        spritebatch.Draw(Overlay.Value, tvEntity.Position.ToWorldCoordinates(0, 0) - Main.screenPosition, Lighting.GetColor(tvEntity.Position.ToPoint() + new Point(6, 9)));
    }
}

public class TennaSystem : ModSystem
{
    internal struct SignalParticle(Vector2 position, Vector2 velocity, float scale)
    {
        internal Vector2 Position = position;
        internal Vector2 Velocity = velocity;
        internal float Scale = scale;
        internal int Time = 0;
    }
    internal static List<SignalParticle> Particles = [];

    internal static Dictionary<Point, int> Tennas = [];

    private static Asset<Texture2D> SignalTexture;

    public override void OnModLoad()
    {
        SignalTexture = ModContent.Request<Texture2D>("TerraVision/Content/Tiles/TVs/TennaVisionBolt");
    }

    public override void UpdateUI(GameTime gameTime)
    {
        for (int i = 0; i <  Particles.Count; i++)
        {
            SignalParticle particle = Particles[i];

            particle.Position += particle.Velocity;

            particle.Velocity *= 0.98f;

            particle.Time++;

            Particles[i] = particle;
        }

        Particles.RemoveAll(p => p.Time >= 60);

        List<Point> tennasToKill = [];

        foreach(Point p in Tennas.Keys)
        {
            Tennas[p]--;
            if (Tennas[p] < 0)
                tennasToKill.Add(p);
        }

        foreach (Point p in tennasToKill)
            Tennas.Remove(p);
    }

    public static void DrawTennas()
    {
        Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullCounterClockwise, null, Main.GameViewMatrix.TransformationMatrix);

        foreach(var tenna in Tennas)
        {
            Point16 dimensions = ModContent.GetInstance<TennaVision>().GetTVDimensions();

            Texture2D texture = TextureAssets.Tile[ModContent.TileType<TennaVision>()].Value;
            Point framePos = GetTennaFrame(tenna.Key, dimensions);
            Rectangle frame = texture.Frame(6, 24, framePos.X, framePos.Y);

            Vector2 position = tenna.Key.ToWorldCoordinates(0, 0);

            Main.spriteBatch.Draw(texture, position - Main.screenPosition, frame, Lighting.GetColor(tenna.Key + new Point(dimensions.X / 2, dimensions.Y / 2)));
        }

        foreach (SignalParticle particle in Particles)
            Main.spriteBatch.Draw(SignalTexture.Value, particle.Position - Main.screenPosition, null, Color.Yellow * (1 - (particle.Time / 60f)), particle.Velocity.ToRotation() + MathHelper.PiOver2, SignalTexture.Size() * 0.5f, particle.Scale, ((particle.Time % 8) >= 4 ? SpriteEffects.FlipHorizontally : SpriteEffects.None), 0);

        Main.spriteBatch.End();
    }

    private static Point GetTennaFrame(Point p, Point16 dimensions)
    {
        TVTileEntity tvEntity = TerraVisionUtils.FindTileEntity<TVTileEntity>(p.X, p.Y, dimensions.X, dimensions.Y, 16);

        if (tvEntity == null)
            return new(0, 0);

        int FrameX;
        int FrameY;

        if (tvEntity.IsOn)
        {
            FrameX = 4;
            FrameY = 0;
            var player = tvEntity.GetVideoPlayer();
            bool isStatic = player == null || (!player.IsPlaying && !player.IsLoading && !player.IsPreparing);
            if (isStatic)
            {
                int counter = (int)(Main.GlobalTimeWrappedHourly * 12);
                FrameY = counter % 4;
            }
            else if (player.IsLoading || player.IsPreparing)
            {
                FrameX++;

                int wrappedTime = ((int)(Main.GlobalTimeWrappedHourly * 60)) % 120;

                if (wrappedTime == 0)
                {
                    Vector2 topLeft = p.ToWorldCoordinates(0, 0);
                    for (int s = -1; s <= 1; s++)
                    {
                        float angleOff = MathHelper.PiOver4 * s;
                        TennaSystem.Particles.Add(new(topLeft + new Vector2(72, 32), Vector2.UnitY.RotatedBy(-MathHelper.PiOver4 + angleOff + Main.rand.NextFloat(-MathHelper.PiOver4 / 4f, MathHelper.PiOver4 / 4f)) * -1, 0.5f));
                        TennaSystem.Particles.Add(new(topLeft + new Vector2(118, 28), Vector2.UnitY.RotatedBy(MathHelper.PiOver4 + angleOff + Main.rand.NextFloat(-MathHelper.PiOver4 / 4f, MathHelper.PiOver4 / 4f)) * -1, 0.5f));
                    }
                }
            }
        }
        else
        {
            int counter = (int)(Main.GlobalTimeWrappedHourly * 16);
            int danceFrame = counter % 92;

            FrameX = danceFrame / 24;
            FrameY = danceFrame % (FrameX == 0 ? 24 : 23);
        }

        return new(FrameX, FrameY);
    }
}

public class TennaVisionItem : ModItem
{
    public override void SetStaticDefaults()
    {
        Item.ResearchUnlockCount = 1;
    }

    public override void SetDefaults()
    {
        Item.width = 20;
        Item.height = 32;
        Item.maxStack = 99;
        Item.useTurn = true;
        Item.autoReuse = true;
        Item.useAnimation = 15;
        Item.useTime = 10;
        Item.useStyle = ItemUseStyleID.Swing;
        Item.consumable = true;
        Item.value = Item.buyPrice(1, 0, 0, 0);
        Item.rare = ItemRarityID.Green;
        Item.createTile = ModContent.TileType<TennaVision>();
    }
}
