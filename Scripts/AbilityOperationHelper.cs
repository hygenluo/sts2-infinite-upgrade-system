using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 能力操作引擎。
/// 通过 ModelDb.Power<T>() → ToMutable() → ApplyInternal(creature, amount)
/// 直接在 Creature 上添加 Power，完全绕过 PowerCmd（其运行时签名需要 PlayerChoiceContext）。
/// </summary>
public static class AbilityOperationHelper
{
    private static readonly Dictionary<string, int> s_boosts = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Power 类信息
    private static readonly Dictionary<string, (string typeName, MethodInfo? applyMethod)> s_powerCache = new();
    private static MethodInfo? s_modelDbPowerGetter;

    public static bool TryPurchase(string key, int cost)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        s_boosts.TryGetValue(key, out int cur);
        s_boosts[key] = cur + 1;
        return true;
    }

    public static async Task ApplyImmediate(string key)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;
        s_boosts.TryGetValue(key, out int count);
        if (count <= 0) return;

        switch (key)
        {
            case "hp":
                if (player.Creature == null) return;
                await MegaCrit.Sts2.Core.Commands.CreatureCmd.GainMaxHp(player.Creature, count);
                await MegaCrit.Sts2.Core.Commands.CreatureCmd.Heal(player.Creature, count);
                break;
            case "energy":
                player.MaxEnergy += count;
                break;
            case "orbSlot":
                player.BaseOrbSlotCount += count;
                break;
        }
    }

    public static void ApplyAllOnCombatStart()
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null) return;

        foreach (var kv in s_boosts)
        {
            if (kv.Value <= 0) continue;
            var key = kv.Key;
            int count = kv.Value;

            try
            {
                if (!s_powerCache.TryGetValue(key, out var entry))
                {
                    // 查找 Power 类型对应的 ApplyInternal 方法
                    var typeName = key switch
                    {
                        "strength" => "MegaCrit.Sts2.Core.Models.Powers.StrengthPower",
                        "dexterity" => "MegaCrit.Sts2.Core.Models.Powers.DexterityPower",
                        "focus" => "MegaCrit.Sts2.Core.Models.Powers.FocusPower",
                        "plating" => "MegaCrit.Sts2.Core.Models.Powers.PlatingPower",
                        "thorns" => "MegaCrit.Sts2.Core.Models.Powers.ThornsPower",
                        "artifact" => "MegaCrit.Sts2.Core.Models.Powers.ArtifactPower",
                        _ => null
                    };
                    if (typeName == null) continue;

                    var powerType = FindType(typeName);
                    if (powerType == null) continue;

                    entry = (typeName, null);
                    s_powerCache[key] = entry;
                }

                // 通过 ModelDb.Power<T>().ToMutable().ApplyInternal(...)
                ApplyPower(player.Creature, key, count);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[InfiniteUpgrade] Apply {key}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void ApplyPower(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, string key, int count)
    {
        // 1. 获取 Power 模板: ModelDb.Power<T>()
        if (s_modelDbPowerGetter == null)
        {
            var modelDbType = FindType("MegaCrit.Sts2.Core.Models.ModelDb");
            if (modelDbType == null) { GD.PrintErr("[IU] ModelDb not found"); return; }
            s_modelDbPowerGetter = modelDbType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Power" && m.IsGenericMethod && m.GetParameters().Length == 0);
            if (s_modelDbPowerGetter == null) { GD.PrintErr("[IU] ModelDb.Power<T>() not found"); return; }
        }

        var typeName = key switch
        {
            "strength" => "MegaCrit.Sts2.Core.Models.Powers.StrengthPower",
            "dexterity" => "MegaCrit.Sts2.Core.Models.Powers.DexterityPower",
            "focus" => "MegaCrit.Sts2.Core.Models.Powers.FocusPower",
            "plating" => "MegaCrit.Sts2.Core.Models.Powers.PlatingPower",
            "thorns" => "MegaCrit.Sts2.Core.Models.Powers.ThornsPower",
            "artifact" => "MegaCrit.Sts2.Core.Models.Powers.ArtifactPower",
            _ => null
        };
        if (typeName == null) return;

        var powerType = FindType(typeName);
        if (powerType == null) { GD.PrintErr($"[IU] Type not found: {typeName}"); return; }

        var getter = s_modelDbPowerGetter.MakeGenericMethod(powerType);
        var template = getter.Invoke(null, null);
        if (template == null) { GD.PrintErr($"[IU] Power template null for {key}"); return; }

        // 2. ToMutable()
        var toMutable = template.GetType().GetMethod("ToMutable", Type.EmptyTypes);
        if (toMutable == null) { GD.PrintErr($"[IU] ToMutable not found"); return; }
        var mutable = toMutable.Invoke(template, null);
        if (mutable == null) { GD.PrintErr($"[IU] ToMutable returned null"); return; }

        // 3. ApplyInternal(Creature owner, decimal amount, bool silent=false)
        var applyInternal = mutable.GetType().GetMethod("ApplyInternal",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), typeof(decimal), typeof(bool) }, null);
        if (applyInternal == null) { GD.PrintErr($"[IU] ApplyInternal not found"); return; }

        applyInternal.Invoke(mutable, new object[] { creature, (decimal)count, false });
        GD.Print($"[InfiniteUpgrade] Applied {key} x{count}");
    }

    private static Type? FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(name);
            if (t != null) return t;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // 持久化
    // ═══════════════════════════════════════════════════════════════

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
