using System;
using Godot;
using HarmonyLib;
using InfiniteUpgradeSystem.UiComponents;
using MegaCrit.Sts2.Core.Localization;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 模组 key 本地化解析（Phase 4 专属选牌提示 + update05 合成附魔显示）。
///
/// 背景：本模组不打包 pck，游戏/RitsuLib 不加载我们的本地化表 —— 带
/// INFINITEUPGRADESYSTEM- 前缀或合成附魔（COMPOSITE_ENCHANTMENT.*）的 key 会解析为
/// 占位文本/原始 key。
///
/// 拦截两层：
/// - GetRawText：SmartFormat 内部（GetFormattedText → SmartFormat → GetRawText）与
///   直接调用都必经这里 —— 覆盖卡面附加文本（extraCardText）等未走 GetFormattedText 的路径。
/// - GetFormattedText：悬停标题/描述等直接格式化路径。
///
/// 最高优先级（Priority.First）确保最后执行，覆盖 RitsuLib 等其它补丁。
/// </summary>
[HarmonyPatch(typeof(LocString), nameof(LocString.GetRawText))]
public static class LocStringRawTextPatch
{
    private static bool s_diagLogged;

    [HarmonyPriority(Priority.First)]
    public static void Postfix(LocString __instance, ref string __result)
    {
        try
        {
            string before = __result;
            ResolveComposite(__instance.LocTable, __instance.LocEntryKey, ref __result);
            if (!s_diagLogged && __result != before
                && __instance.LocTable == "enchantments"
                && __instance.LocEntryKey.StartsWith("COMPOSITE_ENCHANTMENT.", StringComparison.OrdinalIgnoreCase))
            {
                s_diagLogged = true;
                GD.Print($"[IU-Loc] GetRawText key='{__instance.LocEntryKey}' '{before}' -> '{__result}'");
            }
        }
        catch { /* 非致命 */ }
    }

    /// <summary>解析合成附魔 key（title/description/extraCardText）。</summary>
    internal static void ResolveComposite(string locTable, string locEntryKey, ref string result)
    {
        if (locTable != "enchantments"
            || !locEntryKey.StartsWith("COMPOSITE_ENCHANTMENT.", StringComparison.OrdinalIgnoreCase))
            return;

        // extraCardText（卡面附加文本）→ 显示子附魔名称（如「腐化」「腐化、动量」）
        if (locEntryKey.EndsWith(".extraCardText", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(CompositeEnchantment.LastSummary))
        {
            result = CompositeEnchantment.LastSummary;
            return;
        }
        // title/description → 通用「多重附魔」；UpgradeLoc 不可用时硬编码回退
        var compositeText = UpgradeLoc.Get("INFINITEUPGRADESYSTEM-ENCHANT_COMPOSITE", "");
        result = string.IsNullOrEmpty(compositeText) ? "多重附魔" : compositeText;
    }
}

[HarmonyPatch(typeof(LocString), nameof(LocString.GetFormattedText))]
public static class LocStringPromptPatch
{
    private const string KeyPrefix = "INFINITEUPGRADESYSTEM-";

    /// <summary>最高优先级：确保本 Postfix 最后执行，覆盖 RitsuLib 等其它补丁设置的占位结果。</summary>
    [HarmonyPriority(Priority.First)]
    public static void Postfix(LocString __instance, ref string __result)
    {
        try
        {
            LocStringRawTextPatch.ResolveComposite(__instance.LocTable, __instance.LocEntryKey, ref __result);
            // 若已命中合成附魔 key（结果已改变），直接返回
            if (__instance.LocTable == "enchantments"
                && __instance.LocEntryKey.StartsWith("COMPOSITE_ENCHANTMENT.", StringComparison.OrdinalIgnoreCase))
                return;

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
