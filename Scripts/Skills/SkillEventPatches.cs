using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 最近一次可用的 PlayerChoiceContext（v2.0 保留：AbilityOperationHelper.ApplyPower 兜底 context）。
/// </summary>
public static class SkillContextCache
{
    public static PlayerChoiceContext? Last { get; set; }
}

/// <summary>
/// 你可以在休息处选择任意数量的选项（通用，参照遗物 微型帐篷 MiniatureTent）。
/// 休息处逻辑调用 Hook.ShouldDisableRemainingRestSiteOptions 决定是否禁用剩余选项；
/// 按**被处理玩家**从 store 判断（两端一致）—— 此前读本地缓存导致 host/client 对同一玩家
/// 选项状态判定不同 → RestSiteSynchronizer 分歧 → 休息处黑屏。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldDisableRemainingRestSiteOptions))]
public static class SkillRestSitePatch
{
    public static void Postfix(Player player, ref bool __result)
    {
        if (player != null && UpgradeDataStore.HasSkill(player, "rest_all_options"))
            __result = false;
    }
}
