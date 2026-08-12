using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.RunRngs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 游戏生命周期 + 检查点持久化。
///
/// 检查点策略：
/// - 战斗开始 (CombatSetUp)  → 保存检查点
/// - 战斗胜利 (CombatWon)    → 保存检查点（含奖励点数）
/// - 新局 (RunStarted + 无文件) → 初始 5 点
/// - 读档 (RunStarted + 有文件) → 恢复到最近检查点
///
/// 修改只存在于内存中，仅在检查点时写盘。
/// 退出重进 → 回到最近检查点 → 未到检查点的修改自动回滚。
/// </summary>
public static class RunStateHook
{
    public static void Subscribe()
    {
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatWon += OnCombatWon;
        RunManager.Instance.RunStarted += OnRunStarted;
        GD.Print("[InfiniteUpgrade] Subscribed.");
    }

    /// <summary>战斗开始 → 应用能力 + 保存检查点</summary>
    private static void OnCombatSetUp(CombatState state)
    {
        // 方案 B：遍历所有玩家，按各自 store 数据施加（不按本地玩家分叉）
        AbilityOperationHelper.ApplyInitialBoosts(state);
        AbilityOperationHelper.ApplyStarsAtCombatStart(state);
        ApplySkillCombatStartPowers(state);
        // 以下 SaveCheckpoint 写本机 JSON 镜像（单人崩溃恢复/旧档迁移源），
        // 权威数据在 RitsuLib store（随 run 存档自动持久化）。
        UpgradePointManager.SaveCheckpoint();
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        CardUpgradeTracker.SaveCheckpoint(seed);
        AbilityOperationHelper.SaveCheckpoint(seed);
        SkillRegistry.SaveCheckpoint(seed);
        GD.Print($"[InfiniteUpgrade] Checkpoint SAVED (combat start) — pts={UpgradePointManager.CurrentPoints}");
    }

    /// <summary>技能战斗开始效果（应用战斗级 Power，随战斗重置、每场重新应用）。给所有玩家按各自技能施加。</summary>
    private static async void ApplySkillCombatStartPowers(CombatState state)
    {
        if (state == null) return;
        foreach (var player in state.Players)
        {
            if (player?.Creature == null) continue;
            // 储君「免费打出第一张牌」→ VoidFormPower(1)：能量+辉星免费 + 原生绿色荧光显示
            if (UpgradeDataStore.HasSkill(player, "free_first_card"))
                await AbilityOperationHelper.ApplyPower(player.Creature, "freeFirstCard", 1);
        }
    }

    /// <summary>当前房间是否为问号房（进入时已发问号房点数；其战斗不再额外发放）。</summary>
    private static bool s_enteredEventRoom;

    /// <summary>战斗胜利 → 发放点数 → 保存检查点（update05：Monster/Elite/Boss 改为随机区间）</summary>
    private static void OnCombatWon(CombatRoom room)
    {
        if (room == null) return;
        // 玩家源：优先 RunManager.State，回退 CombatManager 当前战斗状态（client 端 CombatWon 时
        // RunManager.State 可能未就绪 → 此前会导致 client 端不加战斗点数 → 点数两端不一致）。
        var runPlayers = RunManager.Instance?.State?.Players;
        var combatPlayers = CombatManager.Instance?.DebugOnlyGetState()?.Players;
        Log.Info($"IU CombatWon room={room?.RoomType} runPlayers={runPlayers?.Count} combatPlayers={combatPlayers?.Count}");
        var players = (IEnumerable<Player>?)(runPlayers ?? combatPlayers);

        bool isEvent = s_enteredEventRoom;
        s_enteredEventRoom = false;
        int total = 0;
        if (players == null) return;
        foreach (var player in players)
        {
            if (player == null) continue;
            int points;
            if (isEvent)
            {
                // 问号房只获取问号房点数：战斗胜利不再额外发放（update05 规则，进入时已发）
                points = 0;
            }
            else
            {
                points = room!.RoomType switch
                {
                    RoomType.Monster => Roll(player, 1, 3),
                    RoomType.Elite => Roll(player, 5, 7),
                    RoomType.Boss => Roll(player, 25, 30),
                    _ => 0
                };
            }
            if (points > 0)
            {
                UpgradePointManager.AddPoints(player, points);
                Log.Info($"IU CombatWon +{points} for {player.NetId}");
                total += points;
            }
        }

        UpgradePointManager.SaveCheckpoint();
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        CardUpgradeTracker.SaveCheckpoint(seed);
        AbilityOperationHelper.SaveCheckpoint(seed);
        SkillRegistry.SaveCheckpoint(seed);
        AbilityOperationHelper.ResetForNextCombat();  // 下场战斗重新应用初始能力
        GD.Print($"[InfiniteUpgrade] Checkpoint SAVED (combat won, +{total}) — pts={UpgradePointManager.CurrentPoints}");
    }

    private static void OnRunStarted(RunState runState)
    {
        var seed = runState.Rng.StringSeed;
        s_enteredEventRoom = false;
        GD.Print($"[InfiniteUpgrade] RunStarted — seed={seed}");

        int points = PointsPersistence.LoadPoints(seed);
        UpgradePointManager.SetPointsDirect(points);
        GD.Print($"[InfiniteUpgrade] Points = {points}");

        CardUpgradeTracker.Load(seed);
        AbilityOperationHelper.Load(seed);
        SkillRegistry.Load(seed);
        AbilityOperationHelper.ResetForNewRun();
        UpgradeDataStore.SyncOnRunStarted(runState); // store 权威优先；空则旧 JSON 迁移
        var player = LocalContext.GetMe(runState) ?? runState.Players.FirstOrDefault();
        if (player != null)
        {
            CardUpgradeTracker.ReapplyAll(player);
            CardOperationHelper.RefreshAllVisuals(player);
        }
    }

    /// <summary>
    /// 随机区间（含两端）。方案 B：用 RitsuLib 每玩家确定性 RNG 流（points stream，随 run save
    /// 保存、两端派生一致）。此前用 HashCode(seed, NetId, ActFloor, CurrentMapCoord)，但
    /// AfterRoomEntered 触发瞬间两端 CurrentMapCoord/ActFloor 可能未同步 → Roll 值两端不同
    /// （实测 host +2 / friend +1）→ 点数分叉。RNG 流不依赖坐标同步，两端消耗同一玩家流一致。
    /// </summary>
    internal static int Roll(Player player, int min, int maxInclusive)
    {
        if (player == null) return min;
        var rng = ModRunRngRegistry.Get(player, Entry.ModId, "points");
        return min + rng.NextInt(0, maxInclusive - min + 1);
    }

    /// <summary>房间进入回调（update05 问号房/商店/火堆点数）。</summary>
    public static void NotifyRoomEntered(bool isEventRoom) => s_enteredEventRoom = isEventRoom;
}

/// <summary>
/// update05：进入问号房(1-5)/商店(1-5)/火堆(2-6) 即发放随机点数。
/// 只处理这三种房间；Monster/Elite/Boss 由 CombatWon 发放。
/// AddPoints 内部立即写盘，退出重进不丢失。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterRoomEntered))]
public static class RoomEntryPointsPatch
{
    public static void Postfix(IRunState runState, AbstractRoom room)
    {
        if (room == null || runState == null) return;

        var state = RunManager.Instance?.State;
        var players = runState.Players;
        Log.Info($"IU RoomEntry room={room?.RoomType} runState_null={runState == null} state_null={state == null} players={players?.Count}");

        var (min, max) = room!.RoomType switch
        {
            RoomType.Event => (1, 5),
            RoomType.Shop => (1, 5),
            RoomType.RestSite => (2, 6),
            _ => (0, 0)
        };

        RunStateHook.NotifyRoomEntered(room.RoomType == RoomType.Event);
        if (max <= 0 || players == null) return;
        // 用 hook 的 runState.Players（而非 RunManager.State）：client 端 AfterRoomEntered 时
        // RunManager.State 可能未就绪 → 此前会导致 client 端不加点 → 点数两端不一致。
        foreach (var player in players)
        {
            if (player == null) continue;
            int points = RunStateHook.Roll(player, min, max);
            if (points > 0)
            {
                UpgradePointManager.AddPoints(player, points);
                Log.Info($"IU RoomEntry +{points} for {player.NetId}");
                GD.Print($"[InfiniteUpgrade] Room entry +{points} ({room.RoomType}) — pts={UpgradePointManager.CurrentPoints}");
            }
        }
    }
}
