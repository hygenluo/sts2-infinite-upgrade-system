using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 多附魔标记（update05）：合成附魔有多个子附魔时，卡牌左侧只显示一个附魔标记。
/// 在 NCard.UpdateEnchantmentVisuals 后追加每个子附魔的图标标记，堆叠在第一个下方。
/// 用 ConditionalWeakTable 按卡牌实例跟踪，避免重复添加/泄漏。
/// </summary>
[HarmonyPatch(typeof(NCard), "UpdateEnchantmentVisuals")]
public static class NCardEnchantMarkersPatch
{
    private static readonly ConditionalWeakTable<NCard, List<TextureRect>> s_extraIcons = new();

    public static void Postfix(NCard __instance)
    {
        try
        {
            if (__instance?.Model?.Enchantment is not CompositeEnchantment composite)
                return; // 非合成附魔：不干预

            // 清理上次添加的标记（该可视化方法每次更新都会触发）
            var list = s_extraIcons.GetOrCreateValue(__instance);
            foreach (var old in list)
            {
                if (GodotObject.IsInstanceValid(old))
                    old.QueueFree();
            }
            list.Clear();

            if (composite.Subs.Count <= 1) return;

            var tab = __instance.EnchantmentTab;
            if (!GodotObject.IsInstanceValid(tab)) return;

            var iconTemplate = __instance.GetNode<TextureRect>("%Enchantment/Icon");
            var iconSize = iconTemplate?.Size.Y > 0 ? iconTemplate.Size.Y : 18f;
            float spacing = iconSize + 2f;
            var iconPos = iconTemplate?.Position ?? Vector2.Zero;

            for (int i = 1; i < composite.Subs.Count; i++)
            {
                var icon = new TextureRect
                {
                    Texture = composite.Subs[i].Icon,
                    Position = iconPos + new Vector2(0, spacing * i),
                    Size = iconTemplate?.Size ?? new Vector2(iconSize, iconSize),
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                tab.AddChild(icon);
                list.Add(icon);
            }
        }
        catch { /* 非致命：标记失败不影响游戏 */ }
    }
}
