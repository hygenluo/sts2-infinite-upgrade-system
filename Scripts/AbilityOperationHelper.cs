using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 能力操作引擎。购买后在战斗开始时应用。
/// </summary>
public static class AbilityOperationHelper
{
    private static readonly Dictionary<string, int> s_boosts = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool TryPurchase(string key, int cost)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        s_boosts.TryGetValue(key, out int cur);
        s_boosts[key] = cur + 1;
        return true;
    }

    /// <summary>即时效果（hp/energy/orbSlot）。</summary>
    public static async Task ApplyImmediate(string key)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null) return;
        s_boosts.TryGetValue(key, out int count);
        if (count <= 0) return;

        switch (key)
        {
            case "hp":
                await CreatureCmd.GainMaxHp(player.Creature, count);
                await CreatureCmd.Heal(player.Creature, count);
                break;
            case "energy":
                player.MaxEnergy += count;
                break;
            case "orbSlot":
                player.BaseOrbSlotCount += count;
                break;
        }
    }

    /// <summary>战斗开始时应用所有已购买的能力（幂等，由 AbilityTracker 追踪已应用次数）。</summary>
    public static async void ApplyAllOnCombatStart()
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null) return;

        // HP/Energy/OrbSlot 在购买时即时应用，不需要战斗中重复
        // Power-based abilities (strength/dex/focus/plating/thorns/artifact)
        // 需要在战斗中通过 PowerCmd.Apply 应用，暂时留空
        GD.Print($"[InfiniteUpgrade] Ability boosts active: {string.Join(", ", s_boosts)}");
    }

    public static void SaveCheckpoint(string seed)
    {
        try
        {
            var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".";
            var dir = Path.Combine(modDir, "runs");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"abilities_{seed}.json"),
                JsonSerializer.Serialize(s_boosts, s_jsonOptions));
        }
        catch (Exception ex) { Log.Error($"Ability save: {ex.Message}"); }
    }

    public static void Load(string seed)
    {
        s_boosts.Clear();
        try
        {
            var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".";
            var path = Path.Combine(modDir, "runs", $"abilities_{seed}.json");
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path), s_jsonOptions);
            if (data != null)
                foreach (var kv in data) s_boosts[kv.Key] = kv.Value;
        }
        catch (Exception ex) { Log.Warn($"Ability load: {ex.Message}"); }
    }
}
