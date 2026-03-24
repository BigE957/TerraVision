using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using ReLogic.Content;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using TerraVision.Common;
using TerraVision.Content.Tiles.TVs;

namespace TerraVision.Content.Items.TVs;

public class VHSID : ModSystem
{
    public static readonly List<(string path, string name)> TapePathes = [];

    internal static MethodInfo resizeMethod = typeof(ModContent).GetMethod("ResizeArrays", BindingFlags.Static | BindingFlags.NonPublic);
    internal static Hook loadTapesHook;
    internal delegate void orig_ResizeArrays(bool optional);

    public static (string path, Color color)[] TapeData =>
    [
        ("TerraVision/Assets/Videos/PortedHim.mp4", Color.Red),
        ("TerraVision/Assets/Videos/Hubris.mp4", Color.LimeGreen)
    ];

    public override void Load()
    {
        loadTapesHook = new Hook(resizeMethod, ResizeArraysWithRocks);
    }

    public override void Unload()
    {
        loadTapesHook = null;
    }

    internal static void ResizeArraysWithRocks(orig_ResizeArrays orig, bool unloading)
    {
        Mod tv = ModLoader.GetMod("TerraVision");
        FieldInfo modLoading = typeof(Mod).GetField("loading", BindingFlags.Instance | BindingFlags.NonPublic);
        if (modLoading != null)
        {
            modLoading.SetValue(tv, true);

            for (int i = 0; i < VHSID.TapeData.Length; i++)
            {
                VHSTape d = new(i, TapeData[i].color);
                tv.AddContent(d);
            }

            modLoading.SetValue(tv, false);
        }
        orig(unloading);
    }
}

[Autoload(false)]
public class VHSTape : ModItem, ILocalizedModType
{
    public override string LocalizationCategory => "Items.Tapes";

    public override string Texture => "TerraVision/Content/Items/TVs/VHSTape";

    public int VHSType;
    private Color TapeColor;

    private static Asset<Texture2D> Overlay;

    protected override bool CloneNewInstances => true;
    public override string Name => VHSID.TapePathes[VHSType].name + "Tape";

    public VHSTape(int i, Color color)
    {
        string name = VHSID.TapeData[i].path;
        while (name.Contains('/'))
            name = name.Remove(0, name.IndexOf('/') + 1);

        name = name.Remove(name.IndexOf('.'));

        VHSType = VHSID.TapePathes.Count;
        TapeColor = color;
        VHSID.TapePathes.Add((VHSID.TapeData[i].path, name));
    }

    public override void SetStaticDefaults()
    {
        Item.ResearchUnlockCount = 1;
        Overlay = ModContent.Request<Texture2D>(Texture + "Overlay");
    }

    public override void SetDefaults()
    {
        Item.width = 32;
        Item.height = 20;
        Item.useTurn = true;
        Item.autoReuse = true;
        Item.useAnimation = 15;
        Item.useTime = 10;
        Item.useStyle = ItemUseStyleID.Swing;
        Item.consumable = true;
        Item.value = Item.buyPrice(1, 0, 0, 0);
        Item.rare = ItemRarityID.Green;
    }

    public override bool? UseItem(Player player)
    {
        if(player.whoAmI == Main.myPlayer)
        {
            Point mouseTile = Main.MouseWorld.ToTileCoordinates();
            MediaPlayerEntity playerEntity = TerraVisionUtils.FindTileEntity<MediaPlayerEntity>(mouseTile.X, mouseTile.Y, 2, 1);

            if (playerEntity == null)
                return false;

            playerEntity.player.ClearVideoQueue();

            playerEntity.CurrentContentPath = VHSID.TapePathes[VHSType].path;
            playerEntity.StoredItem = Type;
            return true;
        }
        return false;
    }

    public override void PostDrawInInventory(SpriteBatch spriteBatch, Vector2 position, Rectangle frame, Color drawColor, Color itemColor, Vector2 origin, float scale)
    {
        spriteBatch.Draw(Overlay.Value, position, frame, TapeColor.MultiplyRGB(drawColor), 0f, origin, scale, 0, 0);
    }

    public override void PostDrawInWorld(SpriteBatch spriteBatch, Color lightColor, Color alphaColor, float rotation, float scale, int whoAmI)
    {
        var position = Item.Center - Main.screenPosition - (Vector2.UnitY * 7);
        var origin = Overlay.Size() / 2f;
        spriteBatch.Draw(Overlay.Value, position, null, TapeColor.MultiplyRGB(lightColor), rotation, origin, scale, SpriteEffects.None, 0);
    }
}
