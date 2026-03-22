using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using Terraria.DataStructures;
using Terraria.Graphics.Effects;
using Terraria.ModLoader;
using TerraVision.Common;
using TerraVision.Content.Tiles.TVs;
using TerraVision.UI.VideoPlayer;

namespace TerraVision.Content.Skies;

public class TVSky : CustomSky
{
    public static Point16 EntityPosition = Point16.NegativeOne;
    private bool skyActive;
    private float opacity;

    public override void Activate(Vector2 position, params object[] args) => skyActive = true;

    public override void Deactivate(params object[] args) => skyActive = Main.LocalPlayer.TryGetModPlayer<TVMonolithPlayer>(out var player) && player.TVMonolith > 0;

    public override void Reset() => skyActive = false;

    public override bool IsActive() => skyActive || opacity > 0;

    public override void Update(GameTime gameTime)
    {
        if (!(Main.LocalPlayer.TryGetModPlayer<TVMonolithPlayer>(out var player) || player.TVMonolith <= 0) || Main.gameMenu)
            skyActive = false;

        if (skyActive && opacity < 1f)
            opacity += 0.02f;
        else if (!skyActive && opacity > 0f)
            opacity -= 0.02f;

        Opacity = opacity;
    }

    public override float GetCloudAlpha()
    {
        return (1f - opacity) * 0.97f + 0.03f;
    }

    public override void Draw(SpriteBatch spriteBatch, float minDepth, float maxDepth)
    {
        float whateverTheFuckThisVariableIsSupposedToBe = 3.40282347E+38f;
        if (maxDepth >= whateverTheFuckThisVariableIsSupposedToBe && minDepth < whateverTheFuckThisVariableIsSupposedToBe)
        {
            TVTileEntity tvEntity = TerraVisionUtils.FindTileEntity<TVTileEntity>(EntityPosition.X, EntityPosition.Y, 2, 3, 16);

            if (tvEntity == null)
            {
                Main.NewText("No entity...");
                return;
            }

            var player = tvEntity.GetVideoPlayer();
            bool needsStatic = player == null || (!player.IsPlaying && !player.IsLoading && !player.IsPreparing);
            if (needsStatic)
                tvEntity.DrawStatic(spriteBatch, new Rectangle(0, 0, Main.ScreenSize.X, Main.ScreenSize.Y), opacity);
            else
                player.Draw(spriteBatch, Vector2.Zero, Main.ScreenSize.ToVector2(), ExampleVideoUISystem.Background.Value);
        }
    }
}

public class TVMonolithScene : ModSceneEffect
{
    public override SceneEffectPriority Priority => SceneEffectPriority.Environment;

    public override void Load()
    {
        SkyManager.Instance["TerraVision:TV"] = new TVSky();
    }

    public override bool IsSceneEffectActive(Player player) => Main.LocalPlayer.TryGetModPlayer<TVMonolithPlayer>(out var monolithPlayer) && monolithPlayer.TVMonolith > 0;

    public override void SpecialVisuals(Player player, bool isActive)
    {
        if (isActive != SkyManager.Instance["TerraVision:TV"].IsActive())
        {
            if (isActive)
                SkyManager.Instance.Activate("TerraVision:TV");
            else
                SkyManager.Instance.Deactivate("TerraVision:TV");
        }
    }
}
