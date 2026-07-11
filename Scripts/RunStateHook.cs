using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 使用 Harmony 补丁挂钩游戏生命周期事件：
/// - 战斗开始(SetUpCombat)时订阅 CombatWon 事件来发放点数
/// - 新对局开始时重置点数
///
/// 为什么不用 EndCombatInternal：该方法是 async Task，Harmony Postfix
/// 会在 Task 返回时（而非 await 链完成后）执行，导致点数发放时机不对。
/// 改用 SetUpCombat（同步方法）懒订阅 CombatWon（C# 事件）的方案。
/// </summary>
public static class RunStateHook
{
    private static bool _combatWonSubscribed;

    /// <summary>
    /// 首次进入战斗时订阅 CombatWon 事件。
    /// SetUpCombat 是同步方法，此时 CombatManager.Instance 已存在。
    /// </summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    [HarmonyPostfix]
    public static void LazySubscribeToCombatWon()
    {
        if (_combatWonSubscribed) return;
        if (CombatManager.Instance == null) return;

        CombatManager.Instance.CombatWon += OnCombatWon;
        _combatWonSubscribed = true;
        Log.Info("InfiniteUpgrade: subscribed to CombatWon.");
    }

    /// <summary>
    /// 战斗胜利时发放点数：普通怪 +1，精英 +5，BOSS +20
    /// </summary>
    private static void OnCombatWon(CombatRoom room)
    {
        if (room == null) return;

        int points = room.RoomType switch
        {
            RoomType.Monster => 1,
            RoomType.Elite => 5,
            RoomType.Boss => 20,
            _ => 0
        };

        if (points > 0)
        {
            UpgradePointManager.AddPoints(points);
            Log.Info($"InfiniteUpgrade: +{points} points from {room.RoomType} combat. Total: {UpgradePointManager.CurrentPoints}");
        }
    }

    /// <summary>
    /// 新对局开始时重置点数为 5
    /// </summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
    [HarmonyPostfix]
    public static void AfterRunLaunch()
    {
        UpgradePointManager.Initialize();
        Log.Info("InfiniteUpgrade: points reset to 5 for new run.");
    }
}
