using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem.Patches;

/// <summary>
/// 击杀得金（gold_on_kill，+20/次）：敌人死亡时给所有购买该能力的玩家发金。
///
/// 0.113 起 <c>AbstractModel.AfterDeath</c> 虚方法签名插入 <c>CardModel source</c>
/// （5 参），旧的 4 参 override 失效且无法写双签名 —— 改为 patch 全局
/// <c>Hook.AfterDeath</c>：<see cref="TargetMethod"/> 反射探测真实签名，
/// 113（6 参：IRunState, ICombatState, Creature, CardModel, bool, float）与
/// 111 及以前（5 参：IRunState, CombatState?, Creature, bool, float）取存在的那个。
/// Postfix 把发金任务链入 <c>__result</c> 的任务尾部，时序与原版 per-model
/// 分发一致（两端 checksum 同步点在任务完成之后）。
/// </summary>
[HarmonyPatch]
public static class GoldOnKillPatch
{
    static MethodBase? TargetMethod()
    {
        var t = typeof(Hook);
        return t.GetMethod("AfterDeath", new[]
               {
                   typeof(MegaCrit.Sts2.Core.Runs.IRunState), typeof(ICombatState),
                   typeof(Creature), typeof(CardModel), typeof(bool), typeof(float)
               })
            ?? t.GetMethod("AfterDeath", new[]
               {
                   typeof(MegaCrit.Sts2.Core.Runs.IRunState), typeof(CombatState),
                   typeof(Creature), typeof(bool), typeof(float)
               });
    }

    static void Postfix(ref Task __result, Creature creature, bool wasRemovalPrevented)
    {
        if (creature == null || wasRemovalPrevented || !creature.IsEnemy) return;
        __result = Chain(__result);
    }

    private static async Task Chain(Task inner)
    {
        if (inner != null) await inner;
        var state = RunManager.Instance?.State;
        if (state == null) return;
        foreach (var player in state.Players)
        {
            if (player == null) continue;
            if (UpgradeDataStore.GetBoost(player, "gold_on_kill") <= 0) continue;
            await PlayerCmd.GainGold(20m, player);
        }
    }
}
