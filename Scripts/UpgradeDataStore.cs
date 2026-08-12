using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 每玩家升级数据（点数 / 技能 / 能力）。存 RitsuLib PlayerRunSavedData（按 NetId 分槽），
/// 随 run 存档持久化（host 权威）。多人下数据变更（购买/点数）必须走同步 action 或确定性 hook，
/// 保证两端对同一玩家读到同一份数据。
/// </summary>
public sealed class PlayerUpgradeData
{
    public int Points { get; set; }
    public Dictionary<string, int> Skills { get; set; } = new();
    public Dictionary<string, int> Boosts { get; set; } = new();
}

/// <summary>
/// 升级数据统一读写入口（方案 B 数据层）。
///
/// 原则：游戏逻辑 hook 一律按「主题玩家」从 store 读（UpgradeDataStore.For(player)），
/// 绝不读本地静态缓存（s_boosts / s_levels / CurrentPoints 仅作 UI 本地缓存）。
/// 写（Mutate）只在确定性 hook / 同步 action 内调用。
/// </summary>
public static class UpgradeDataStore
{
    public static PlayerRunSavedData<PlayerUpgradeData>? Slots { get; private set; }

    public static bool IsAvailable => Slots != null;

    /// <summary>注册每玩家数据槽位。在 Entry.Init 中于 RunStateHook.Subscribe 之前调用。</summary>
    public static void Register()
    {
        try
        {
            using (RitsuLibFramework.BeginModDataRegistration(Entry.ModId))
            {
                var store = RitsuLibFramework.GetRunSavedDataStore(Entry.ModId);
                Slots = store.RegisterPerPlayer<PlayerUpgradeData>(
                    "upgrade_data",
                    defaultFactory: () => new PlayerUpgradeData(),
                    options: new RunSavedDataOptions
                    {
                        WritePolicy = RunSavedDataWritePolicy.WhenNonDefault,
                    });
            }
            Log.Info("InfiniteUpgradeSystem: UpgradeDataStore registered (RitsuLib).");
        }
        catch (Exception ex)
        {
            Slots = null;
            Log.Error($"InfiniteUpgradeSystem: UpgradeDataStore registration failed (RitsuLib missing?): {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 读（游戏逻辑统一入口）
    // ═══════════════════════════════════════════════════════════════

    public static PlayerUpgradeData For(Player player)
    {
        if (Slots == null || player == null) return new PlayerUpgradeData();
        try { return Slots.Get(player); }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgradeSystem: store Get failed: {ex.Message}");
            return new PlayerUpgradeData();
        }
    }

    public static int GetPoints(Player player) => For(player).Points;

    public static int GetBoost(Player player, string key)
        => For(player).Boosts.TryGetValue(key, out var v) ? v : 0;

    public static int GetLevel(Player player, string skillId)
        => For(player).Skills.TryGetValue(skillId, out var v) ? v : 0;

    public static bool HasSkill(Player player, string id) => GetLevel(player, id) > 0;

    // ═══════════════════════════════════════════════════════════════
    // 写（仅确定性 hook / 同步 action 内调用）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>修改某玩家数据并标记槽位已变更。返回修改后的数据。</summary>
    public static PlayerUpgradeData? Mutate(Player player, Action<PlayerUpgradeData> mutate)
    {
        if (Slots == null || player == null) return null;
        try
        {
            var result = Slots.Modify(player, mutate);
            if (LocalContext.IsMe(player))
                RefreshLocalCache(player);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgradeSystem: store Modify failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>本地玩家数据变更后刷新静态缓存（UI 读取：CurrentPoints / s_boosts / s_levels）。</summary>
    private static void RefreshLocalCache(Player player)
    {
        var data = For(player);
        UpgradePointManager.SetPointsDirect(data.Points);
        AbilityOperationHelper.ReplaceBoosts(data.Boosts);
        SkillRegistry.ReplaceLevels(data.Skills);
    }

    // ═══════════════════════════════════════════════════════════════
    // RunStarted 同步（Step 5：store 权威优先）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// RunStarted 时同步**所有**玩家的数据（方案 B 修复）：
    /// - store 已有该玩家数据（RitsuLib 从 run save 恢复，host 权威）→ 用之（本地玩家刷新缓存）；
    /// - store 为空 → 填初始：本地玩家从旧 JSON（静态容器）迁移，**非本地玩家用默认 Points=7**
    ///   （保证两端对同一玩家的初始数据一致，否则购买 action 的校验在另一端失败）。
    /// </summary>
    public static void SyncOnRunStarted(RunState runState)
    {
        if (Slots == null || runState == null) return;
        foreach (var player in runState.Players)
        {
            if (player == null) continue;
            bool isLocal = LocalContext.IsMe(player);
            if (Slots.TryGet(runState, player.NetId, out _))
            {
                if (isLocal) RefreshLocalCache(player); // store 权威 → 本地缓存
            }
            else
            {
                var data = isLocal ? BuildFromLegacy() : new PlayerUpgradeData { Points = 7 };
                try { Slots.Set(runState, player.NetId, data); }
                catch (Exception ex)
                {
                    Log.Warn($"InfiniteUpgradeSystem: SyncOnRunStarted set failed for {player.NetId}: {ex.Message}");
                }
                if (isLocal) RefreshLocalCache(player);
            }
        }
        Log.Info("InfiniteUpgradeSystem: RunStarted — store synced for all players.");
    }

    /// <summary>本地玩家旧数据（静态容器，旧 JSON 迁移源）。</summary>
    private static PlayerUpgradeData BuildFromLegacy() => new()
    {
        Points = UpgradePointManager.CurrentPoints,
        Skills = SkillRegistry.SnapshotLevels(),
        Boosts = AbilityOperationHelper.SnapshotBoosts(),
    };
}
