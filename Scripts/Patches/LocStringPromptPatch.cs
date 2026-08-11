using System;
using HarmonyLib;
using InfiniteUpgradeSystem.UiComponents;
using MegaCrit.Sts2.Core.Localization;

namespace InfiniteUpgradeSystem;

/// <summary>
/// Phase 4 专属选牌提示。
///
/// 背景：本模组不打包 pck，游戏不加载我们的本地化表 —— LocString 对
/// INFINITEUPGRADESYSTEM-* 的 key 会解析为 "Missing localization key" 占位文本。
/// 经 IL 反汇编确认：选牌屏的提示显示最终必经 LocString.GetFormattedText()。
/// 因此拦截该方法：key 带本模组前缀时，用自加载本地化（UpgradeLoc）解析后直接返回。
/// 游戏自身的 key 不受影响。
/// </summary>
[HarmonyPatch(typeof(LocString), nameof(LocString.GetFormattedText))]
public static class LocStringPromptPatch
{
    private const string KeyPrefix = "INFINITEUPGRADESYSTEM-";
    private const string CompositeTitle = "多重附魔";
    private static bool s_diagLogged;

    /// <summary>最高优先级：确保本 Postfix 最后执行，覆盖 RitsuLib 等其它补丁设置的占位结果。</summary>
    [HarmonyPriority(Priority.First)]
    public static void Postfix(LocString __instance, ref string __result)
    {
        try
        {
            // 合成附魔（CompositeEnchantment）标题/描述/附加文本。Id.Entry 经 ModelDb.GetId
            // 的 Slugify 生成，为 COMPOSITE_ENCHANTMENT（大写蛇形）。
            if (__instance.LocTable == "enchantments"
                && __instance.LocEntryKey.StartsWith("COMPOSITE_ENCHANTMENT.", StringComparison.OrdinalIgnoreCase))
            {
                if (!s_diagLogged)
                {
                    s_diagLogged = true;
                    GD.Print($"[IU-Loc] composite key='{__instance.LocEntryKey}' prevResult='{__result}'");
                }
                // extraCardText（卡面附加文本）→ 显示子附魔名称（如「腐化」）
                if (__instance.LocEntryKey.EndsWith(".extraCardText", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(CompositeEnchantment.LastSummary))
                {
                    __result = CompositeEnchantment.LastSummary;
                    return;
                }
                // title/description → 通用「多重附魔」；UpgradeLoc 不可用时硬编码回退
                __result = UpgradeLoc.Get("INFINITEUPGRADESYSTEM-ENCHANT_COMPOSITE", null)
                           ?? CompositeTitle;
                return;
            }

            var key = __instance.LocEntryKey;
            if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return;
            var resolved = UpgradeLoc.Get(key, __result);
            if (!string.IsNullOrEmpty(resolved))
                __result = resolved;
        }
        catch
        {
            // 非致命：解析失败保持游戏原结果（占位文本）
        }
    }
}
