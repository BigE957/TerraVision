using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraVision.Content.Items.TVs;
using static Terraria.ModLoader.ModContent;

namespace TerraVision.Core;

public class AutoloadingSystem : ModSystem
{
    internal static MethodInfo resizeMethod = typeof(ModContent).GetMethod("ResizeArrays", BindingFlags.Static | BindingFlags.NonPublic);
    internal static Hook loadStoneHook;
    internal delegate void orig_ResizeArrays(bool optional);

    public override void Load()
    {
        loadStoneHook = new Hook(resizeMethod, ResizeArraysWithRocks);
    }

    public override void Unload()
    {
        loadStoneHook = null;
    }

    internal static void ResizeArraysWithRocks(orig_ResizeArrays orig, bool unloading)
    {
        Mod tv = ModLoader.GetMod("TerraVision");
        FieldInfo modLoading = typeof(Mod).GetField("loading", BindingFlags.Instance | BindingFlags.NonPublic);
        if (modLoading != null)
        {
            modLoading.SetValue(tv, true);

            for (int i = 0; i < CassetteID.PathsForCassettes.Length; i++)
            {
                Cassette d = new(i);
                tv.AddContent(d);
            }

            modLoading.SetValue(tv, false);
        }
        orig(unloading);
    }
}
