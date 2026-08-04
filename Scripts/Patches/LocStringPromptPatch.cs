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

    public static void Postfix(LocString __instance, ref string __result)
    {
        try
        {
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
