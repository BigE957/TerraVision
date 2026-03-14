using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraVision.Content.Items;

public class TVRemote : ModItem
{
    public override void SetStaticDefaults()
    {
        Item.ResearchUnlockCount = 1;
    }

    public override void SetDefaults()
    {
        Item.width = 28;
        Item.height = 28;
        Item.maxStack = 1;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useAnimation = 15;
        Item.useTime = 15;
        Item.useTurn = true;
        Item.autoReuse = false;
        Item.value = Item.buyPrice(0, 10, 0, 0);
        Item.rare = ItemRarityID.Blue;
    }
}