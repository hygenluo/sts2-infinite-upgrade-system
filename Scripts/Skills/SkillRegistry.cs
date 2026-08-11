using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
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

    /// <summary>本场战斗打牌计数（每 4 张触发一次敏捷/力量技能，BeforeCombatStart 清零）。</summary>
    private static int s_combatPlayCount;

    /// <summary>当前战斗打牌计数（UI 显示用：战斗间打开面板可见上一场计数）。</summary>
    public static int CombatPlayCount => s_combatPlayCount;

    /// <summary>是否拥有任何技能（诊断日志门槛：仅技能拥有者打印，避免打牌刷屏）。</summary>
    public static bool AnyOwned()
    {
        foreach (var kv in s_levels)
            if (kv.Value > 0) return true;
        return false;
    }

    /// <summary>s_levels 副本（供 UpgradeDataStore 旧档迁移，Step 1）。</summary>
    public static Dictionary<string, int> SnapshotLevels() => new(s_levels);

    public static void AddCombatPlay() => s_combatPlayCount++;
    public static void ResetCombatPlay() => s_combatPlayCount = 0;

    /// <summary>当前等级（0 = 未拥有）。</summary>
    public static int GetLevel(string id) => s_levels.TryGetValue(id, out var v) ? v : 0;

    /// <summary>是否已拥有（等级 &gt; 0）。</summary>
    public static bool Has(string id) => GetLevel(id) > 0;

    /// <summary>
    /// 购买：扣点 → 升级（不超过 maxLevel）→ 立即写盘。点数不足/已满级返回 false。
    /// </summary>
    public static bool TryPurchase(string id, int cost, int maxLevel = 1)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        if (GetLevel(id) >= maxLevel)
        {
            UpgradePointManager.AddPoints(cost); // 已满级：退款
            return false;
        }
        s_levels[id] = GetLevel(id) + 1;
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        SaveCheckpoint(seed);
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
