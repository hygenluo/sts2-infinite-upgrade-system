using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 技能注册表（UI设计.md §六 6.2）。
///
/// 等级化存储：Dictionary&lt;string,int&gt;（不用 bool —— 未来技能升级叠层只改
/// MaxLevel 与效果逻辑，UI/持久化零改动）。当前技能 MaxLevel=1（满级 = 已购）。
///
/// 持久化：skills_&lt;seed&gt;.json，购买即写盘（与 AbilityOperationHelper 同模式），
/// 随检查点（CombatSetUp / CombatWon）一并保存，RunStarted 读档恢复。
/// </summary>
public static class SkillRegistry
{
    private static readonly Dictionary<string, int> s_levels = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>本场战斗打牌计数（v1.4.3：每玩家 store，两端一致；每 4 张触发敏捷/力量技能）。</summary>
    private static int s_combatPlayCount;

    /// <summary>当前战斗打牌计数（本地玩家缓存，UI 显示用：战斗间打开面板可见上一场计数）。</summary>
    public static int CombatPlayCount => s_combatPlayCount;

    /// <summary>指定玩家本场战斗打牌计数（store，两端一致）。</summary>
    public static int GetCombatPlayCount(Player player) => UpgradeDataStore.For(player).CombatPlayCount;

    /// <summary>打牌计数 +1（写该玩家 store，刷新本地缓存）。AfterCardPlayed 两端执行 → 计数一致。</summary>
    public static void AddCombatPlay(Player player)
    {
        if (player == null) return;
        UpgradeDataStore.Mutate(player, d => d.CombatPlayCount++);
        if (LocalContext.IsMe(player))
            s_combatPlayCount = UpgradeDataStore.For(player).CombatPlayCount;
    }

    /// <summary>战斗开始清零所有玩家打牌计数（BeforeCombatStart 两端执行，确定性）。</summary>
    public static void ResetCombatPlay()
    {
        s_combatPlayCount = 0;
        var state = RunManager.Instance?.State;
        if (state == null) return;
        foreach (var player in state.Players)
            if (player != null)
                UpgradeDataStore.Mutate(player, d => d.CombatPlayCount = 0);
    }

    /// <summary>是否拥有任何技能（诊断日志门槛：仅技能拥有者打印，避免打牌刷屏）。</summary>
    public static bool AnyOwned()
    {
        foreach (var kv in s_levels)
            if (kv.Value > 0) return true;
        return false;
    }

    /// <summary>s_levels 副本（供 UpgradeDataStore 旧档迁移，Step 1）。</summary>
    public static Dictionary<string, int> SnapshotLevels() => new(s_levels);

    /// <summary>用 store 数据替换本地缓存 s_levels（UI 显示用，方案 B 本地玩家缓存）。</summary>
    public static void ReplaceLevels(Dictionary<string, int> levels)
    {
        s_levels.Clear();
        foreach (var kv in levels)
            s_levels[kv.Key] = kv.Value;
    }

    /// <summary>当前等级（0 = 未拥有）。</summary>
    public static int GetLevel(string id) => s_levels.TryGetValue(id, out var v) ? v : 0;

    /// <summary>是否已拥有（等级 &gt; 0）。</summary>
    public static bool Has(string id) => GetLevel(id) > 0;

    /// <summary>
    /// 购买技能（方案 B）：扣点 + 技能等级写入指定玩家的 store。由同步 action 两端执行。
    /// 点数不足/已满级返回 false。
    /// </summary>
    public static bool TryPurchase(Player player, string id, int cost, int maxLevel = 1)
    {
        if (player == null) { Log.Info($"IU purchase {id} FAIL player=null"); return false; }
        if (UpgradeDataStore.GetPoints(player) < cost)
        { Log.Info($"IU purchase {id} cost={cost} pts={UpgradeDataStore.GetPoints(player)} FAIL(pts)"); return false; }
        if (UpgradeDataStore.GetLevel(player, id) >= maxLevel)
        { Log.Info($"IU purchase {id} FAIL(maxlevel)"); return false; }
        UpgradeDataStore.Mutate(player, d =>
        {
            d.Points -= cost;
            d.Skills.TryGetValue(id, out int lv);
            d.Skills[id] = lv + 1;
        });
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        SaveCheckpoint(seed);
        Log.Info($"IU purchase {id} cost={cost} pts_after={UpgradeDataStore.GetPoints(player)} OK");
        return true;
    }

    public static void SaveCheckpoint(string seed)
    {
        try
        {
            File.WriteAllText(SavePaths.GetFilePath("skills", seed), JsonSerializer.Serialize(s_levels, s_jsonOptions));
        }
        catch (System.Exception ex)
        {
            Log.Error($"Skill save: {ex.Message}");
        }
    }

    public static void Load(string seed)
    {
        s_levels.Clear();
        try
        {
            var path = SavePaths.GetFilePath("skills", seed);
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path), s_jsonOptions);
            if (data != null)
                foreach (var kv in data)
                    s_levels[kv.Key] = kv.Value;
        }
        catch (System.Exception ex)
        {
            Log.Warn($"Skill load: {ex.Message}");
        }
    }
}
