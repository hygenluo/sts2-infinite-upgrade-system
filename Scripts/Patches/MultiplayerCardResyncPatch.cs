using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem.Patches;

/// <summary>
/// 【多人关键】对端玩家牌组被「房间同步」重建之后，立刻把本模组的修改重放到那份重建出来的牌组上。
///
/// 背景（反编译 0.113.1 确认）：
/// - 每次进新地图点，<c>RunManager.EnterMapPointInternal</c> 会先
///   <c>CombatStateSynchronizer.StartSync()</c>（把本端玩家的 <c>SerializablePlayer</c> 发出去）
///   再 <c>await WaitForSync()</c>；
/// - <c>WaitForSync</c> 对**每个非本地玩家**调用 <c>Player.SyncWithSerializedPlayer</c>，
///   里面 <c>Deck.Clear()</c> + 用 <c>SerializableCard</c> 逐张重建牌组；
/// - 而 <c>SerializableCard</c> 只带 Id / CurrentUpgradeLevel / Enchantment(Id+Amount+Props) / Props，
///   **不带** 本模组制造的派生状态：额外 DynamicVar 加成（+攻击/格挡/抽牌）、加/删关键词、
///   自定义费用（SetCustomBaseCost）、BaseReplayCount、以及合成附魔的 Subs。
///
/// 结果：改过卡的玩家自己那份是「改过的」，对端那份每次进房间都被还原成原型 →
///   1) 战斗里对端模拟出的数值不同（伤害/格挡/费用/关键词），一打这张牌 checksum 就分歧；
///   2) 合成附魔在对端变成空壳（Subs 为 0），视觉/效果全丢。
///
/// 为什么重放是安全的：本模组所有卡牌修改都走 CardMod action，两端在同一个 action 里各记一份
/// 完全相同的 <see cref="CardUpgradeTracker"/> 记录（按玩家 NetId 分桶）。
/// 被同步重建出来的牌组本身没有任何本模组的修改（干净原型），所以这里重放一次正好补齐，
/// 不会重复叠加。钩在 <c>SyncWithSerializedPlayer</c> 上而不是 <c>WaitForSync</c> 上，
/// 是为了只处理「刚刚被重建的那一个玩家」，避免对没被重建的玩家重复施加。
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.SyncWithSerializedPlayer))]
public static class MultiplayerCardResyncPatch
{
    public static void Postfix(Player __instance)
    {
        if (__instance == null) return;
        try
        {
            if (CardUpgradeTracker.HasAnyRecords(__instance))
            {
                Log.Info($"InfiniteUpgrade: re-applying card mods to synced player {__instance.NetId} " +
                         "(multiplayer room sync rebuilt their deck from SerializableCard).");
                CardUpgradeTracker.ReapplyAll(__instance);
            }
        }
        catch (Exception ex)
        {
            // 非致命：重放失败只会导致该玩家本轮数值偏弱，不能让房间同步整体崩掉。
            Log.Error($"InfiniteUpgrade: re-apply after player sync failed for {__instance.NetId}: {ex}");
        }
    }
}
