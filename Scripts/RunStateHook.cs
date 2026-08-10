using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

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
        AbilityOperationHelper.ApplyInitialBoosts();
        AbilityOperationHelper.ApplyStarsAtCombatStart();
        ApplySkillCombatStartPowers();
        UpgradePointManager.SaveCheckpoint();
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        CardUpgradeTracker.SaveCheckpoint(seed);
        AbilityOperationHelper.SaveCheckpoint(seed);
        SkillRegistry.SaveCheckpoint(seed);
        GD.Print($"[InfiniteUpgrade] Checkpoint SAVED (combat start) — pts={UpgradePointManager.CurrentPoints}");
    }

    /// <summary>技能战斗开始效果（应用战斗级 Power，随战斗重置、每场重新应用）。</summary>
    private static async void ApplySkillCombatStartPowers()
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null) return;
        // 储君「免费打出第一张牌」→ VoidFormPower(1)：能量+辉星免费 + 原生绿色荧光显示
        if (SkillRegistry.Has("free_first_card"))
            await AbilityOperationHelper.ApplyOnePower(player.Creature, "freeFirstCard", 1);
    }

    /// <summary>当前房间是否为问号房（进入时已发问号房点数；其战斗不再额外发放）。</summary>
    private static bool s_enteredEventRoom;

    /// <summary>战斗胜利 → 发放点数 → 保存检查点（update05：Monster/Elite/Boss 改为随机区间）</summary>
    private static void OnCombatWon(CombatRoom room)
    {
        if (room == null) return;

        int points;
        if (s_enteredEventRoom)
        {
            // 问号房只获取问号房点数：战斗胜利不再额外发放（update05 规则，进入时已发）
            points = 0;
            s_enteredEventRoom = false;
        }
        else
        {
            points = room.RoomType switch
            {
                RoomType.Monster => Roll(1, 3),
                RoomType.Elite => Roll(5, 7),
                RoomType.Boss => Roll(25, 30),
                _ => 0
            };
        }

        if (points > 0)
            UpgradePointManager.AddPoints(points);

        UpgradePointManager.SaveCheckpoint();
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        CardUpgradeTracker.SaveCheckpoint(seed);
        AbilityOperationHelper.SaveCheckpoint(seed);
        SkillRegistry.SaveCheckpoint(seed);
        AbilityOperationHelper.ResetForNextCombat();  // 下场战斗重新应用初始能力
        GD.Print($"[InfiniteUpgrade] Checkpoint SAVED (combat won, +{points}) — pts={UpgradePointManager.CurrentPoints}");
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
        var player = LocalContext.GetMe(runState) ?? runState.Players.FirstOrDefault();
        if (player != null)
        {
            CardUpgradeTracker.ReapplyAll(player);
            CardOperationHelper.RefreshAllVisuals(player);
        }
    }

    /// <summary>
    /// 随机区间（含两端）。用全局 Chaotic RNG：点数纯 mod 内部数据，不必跟随 run 种子，
    /// 且避免扰动 run RNG 各流（Niche 等）的确定性。
    /// </summary>
    internal static int Roll(int min, int maxInclusive) =>
        Rng.Chaotic.NextInt(min, maxInclusive + 1);

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

        int points = room.RoomType switch
        {
            RoomType.Event => RunStateHook.Roll(1, 5),
            RoomType.Shop => RunStateHook.Roll(1, 5),
            RoomType.RestSite => RunStateHook.Roll(2, 6),
            _ => 0
        };

        RunStateHook.NotifyRoomEntered(room.RoomType == RoomType.Event);
        if (points > 0)
        {
            UpgradePointManager.AddPoints(points);
            GD.Print($"[InfiniteUpgrade] Room entry +{points} ({room.RoomType}) — pts={UpgradePointManager.CurrentPoints}");
        }
    }
}
