using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace InfiniteUpgradeSystem.Patches;

/// <summary>
/// 能力「每当你击败一名敌人时，获取20金币」— Hook.AfterDeath Postfix。
/// 敌方生物死亡时（无论击杀来源：攻击/中毒/自爆/Doom）给本地玩家 +20 金币。
/// 已购能力 key=gold_on_kill（s_boosts 持久化，7 点）；未购买时零开销。
/// </summary>
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Hooks.Hook), nameof(MegaCrit.Sts2.Core.Hooks.Hook.AfterDeath))]
public static class GoldOnKillPatch
{
    static void Postfix(Creature creature, bool wasRemovalPrevented)
    {
        if (creature == null || wasRemovalPrevented) return;
        if (!creature.IsEnemy) return; // 只统计敌方生物（排除奥斯提/宠物等友方死亡）
        if (AbilityOperationHelper.GetBoost("gold_on_kill") <= 0) return;
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;
        _ = HandleAsync(player);
    }

    private static async Task HandleAsync(Player player)
    {
        try
        {
            await PlayerCmd.GainGold(20m, player);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Gold-on-kill error: {ex}");
        }
    }
}
