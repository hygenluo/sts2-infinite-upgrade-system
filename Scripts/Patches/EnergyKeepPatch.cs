using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace InfiniteUpgradeSystem.Patches;

/// <summary>
/// 能量跨回合不消失 — 通过 Harmony Postfix 补丁 Hook.ShouldPlayerResetEnergy。
/// 当玩家购买了 "energyKeep" 能力（消耗 25 点）后，未使用的能量在回合结束时保留。
/// 参考原版遗物 IceCream（冰淇淋）的 ShouldPlayerResetEnergy 实现。
/// </summary>
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Hooks.Hook), "ShouldPlayerResetEnergy")]
public static class EnergyKeepPatch
{
    static void Postfix(CombatState combatState, Player player, ref bool __result)
    {
        // 已有其他钩子监听器（如 IceCream 遗物）否决了能量重置，不重复干预
        if (!__result) return;

        // 按传入玩家从 store 读（方案 B，不按本地玩家分叉）
        if (player == null) return;

        // 第 1 回合正常重置（与 IceCream 行为一致）
        if (combatState.RoundNumber <= 1) return;

        // 检查玩家是否购买了 energyKeep 能力
        if (UpgradeDataStore.GetBoost(player, "energyKeep") > 0)
        {
            __result = false; // 保留能量 → AddMaxEnergyToCurrent()
        }
    }
}
