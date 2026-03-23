using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.DataStructures;
using Terraria.Enums;
using Terraria.Graphics.Effects;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerraVision.Content.Tiles.TVs;

public class BoxTV : BaseTVTile
{
    public override Point16 GetTVDimensions() => new(5, 6);

    public override string GetTVStyleName() => "Box TV";

    private Asset<Effect> _greyscaleShader;
    private static Asset<Texture2D> Overlay;

    public override Asset<Effect> GetShaderEffect() => _greyscaleShader;

    public override void Unload()
    {
        _greyscaleShader = null;
    }

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

        ModTileEntity te = ModContent.GetInstance<TVTileEntity>();
        TileObjectData.newTile.HookPostPlaceMyPlayer = new PlacementHook(te.Hook_AfterPlacement, -1, 0, true);

        TileObjectData.addTile(Type);

        AddMapEntry(new Color(100, 100, 100), CreateMapEntryName());

        DustType = DustID.Iron;

        TVTileEntity.TileData[Type] = (GetTVDimensions().ToPoint(), new Rectangle(12, 46, -26, -14));

        if (!Main.dedServ)
        {
            _greyscaleShader = ModContent.Request<Effect>("TerraVision/Assets/Effects/Greyscale");
            Overlay = ModContent.Request<Texture2D>("TerraVision/Content/Tiles/TVs/BoxTVOverlay");
        }
    }

    public override void PostDrawTVScreen(SpriteBatch spritebatch, TVTileEntity tvEntity)
    {
        spritebatch.Draw(Overlay.Value, tvEntity.Position.ToWorldCoordinates(0, 0) - Main.screenPosition, Lighting.GetColor(tvEntity.Position.ToPoint() + new Point(6, 9)));

    }
}

public class BoxTVItem : ModItem
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
        Item.createTile = ModContent.TileType<BoxTV>();
    }
}