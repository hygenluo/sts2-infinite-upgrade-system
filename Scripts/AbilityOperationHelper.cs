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
/// 通过 ModelDb 创建 Power 实例 + 非泛型 PowerCmd.Apply 在战斗开始时应用。
/// </summary>
public static class AbilityOperationHelper
{
    private static readonly Dictionary<string, int> s_boosts = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Dictionary<string, string> s_powerTypes = new()
    {
        ["strength"] = "MegaCrit.Sts2.Core.Models.Powers.StrengthPower",
        ["dexterity"] = "MegaCrit.Sts2.Core.Models.Powers.DexterityPower",
        ["focus"]     = "MegaCrit.Sts2.Core.Models.Powers.FocusPower",
        ["plating"]   = "MegaCrit.Sts2.Core.Models.Powers.PlatingPower",
        ["thorns"]    = "MegaCrit.Sts2.Core.Models.Powers.ThornsPower",
        ["artifact"]  = "MegaCrit.Sts2.Core.Models.Powers.ArtifactPower",
    };

    // 缓存反射结果
    private static Type? s_modelDbType;
    private static MethodInfo? s_powerGetter;
    private static MethodInfo? s_applyMethod;

    // ═══════════════════════════════════════════════════════════════
    // 公共
    // ═══════════════════════════════════════════════════════════════

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

    /// <summary>战斗开始时应用所有已购买的 Power 类能力。</summary>
    public static async void ApplyAllOnCombatStart()
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null) return;

        // 初始化反射缓存
        if (!InitReflection()) return;

        foreach (var kv in s_boosts)
        {
            if (kv.Value <= 0) continue;
            if (!s_powerTypes.TryGetValue(kv.Key, out var typeName)) continue;

            try
            {
                var powerType = FindType(typeName);
                if (powerType == null) continue;

                // ModelDb.Power<T>() — 获取模板
                var powerGetter = s_powerGetter!.MakeGenericMethod(powerType);
                var template = powerGetter.Invoke(null, null);
                if (template == null) continue;

                // .ToMutable() — 创建可变副本
                var toMutable = template.GetType().GetMethod("ToMutable", Type.EmptyTypes);
                if (toMutable == null) continue;
                var mutable = toMutable.Invoke(template, null);

                // PowerCmd.Apply(PowerModel power, Creature target, decimal amount, Creature? applier, CardModel? cardSource, bool silent=false)
                var result = s_applyMethod!.Invoke(null, new[]
                {
                    mutable,
                    player.Creature,
                    (decimal)kv.Value,
                    player.Creature,
                    null,    // cardSource
                    false    // silent
                });

                if (result is Task t) await t;
                GD.Print($"[InfiniteUpgrade] Applied {kv.Key} x{kv.Value}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[InfiniteUpgrade] Apply {kv.Key}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static bool InitReflection()
    {
        if (s_applyMethod != null) return true;

        try
        {
            // ModelDb
            s_modelDbType = FindType("MegaCrit.Sts2.Core.Models.ModelDb");
            if (s_modelDbType == null) { GD.PrintErr("[InfiniteUpgrade] ModelDb not found"); return false; }

            s_powerGetter = s_modelDbType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Power" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (s_powerGetter == null) { GD.PrintErr("[InfiniteUpgrade] ModelDb.Power<T>() not found"); return false; }

            // PowerCmd.Apply (非泛型版本)
            var powerCmdType = FindType("MegaCrit.Sts2.Core.Commands.PowerCmd");
            if (powerCmdType == null) { GD.PrintErr("[InfiniteUpgrade] PowerCmd not found"); return false; }

            foreach (var m in powerCmdType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == "Apply" && !m.IsGenericMethodDefinition)
                {
                    var p = m.GetParameters();
                    // 找 PowerModel, Creature, decimal, ... 签名的重载
                    if (p.Length >= 3 && p[0].ParameterType.Name == "PowerModel"
                        && p[1].ParameterType.Name == "Creature"
                        && p[2].ParameterType == typeof(decimal))
                    {
                        s_applyMethod = m;
                        GD.Print($"[InfiniteUpgrade] Found Apply: {m}");
                        break;
                    }
                }
            }

            if (s_applyMethod == null) { GD.PrintErr("[InfiniteUpgrade] PowerCmd.Apply(PowerModel,...) not found"); return false; }
            return true;
        }
        catch (Exception ex) { GD.PrintErr($"[InfiniteUpgrade] Init error: {ex}"); return false; }
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
