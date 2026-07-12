using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 能力操作引擎。
/// 购买的能力在战斗开始时通过 PowerCmd.Apply 应用到玩家 Creature。
/// 使用 Reflection 调用以兼容实际运行时 PowerCmd 签名。
/// </summary>
public static class AbilityOperationHelper
{
    private static readonly Dictionary<string, int> s_boosts = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Power 类型全名（运行时反射用）
    private static readonly Dictionary<string, string> s_powerTypes = new()
    {
        ["strength"] = "MegaCrit.Sts2.Core.Models.Powers.StrengthPower",
        ["dexterity"] = "MegaCrit.Sts2.Core.Models.Powers.DexterityPower",
        ["focus"]     = "MegaCrit.Sts2.Core.Models.Powers.FocusPower",
        ["plating"]   = "MegaCrit.Sts2.Core.Models.Powers.PlatingPower",
        ["thorns"]    = "MegaCrit.Sts2.Core.Models.Powers.ThornsPower",
        ["artifact"]  = "MegaCrit.Sts2.Core.Models.Powers.ArtifactPower",
    };

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

    /// <summary>战斗开始时应用所有已购买的 Power 类能力。</summary>
    public static async void ApplyAllOnCombatStart()
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null) return;

        foreach (var kv in s_boosts)
        {
            if (kv.Value <= 0) continue;
            if (!s_powerTypes.TryGetValue(kv.Key, out var typeName)) continue;

            try
            {
                // 在所有已加载程序集中查找 Power 类型
                Type? powerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    powerType = asm.GetType(typeName);
                    if (powerType != null) break;
                }

                if (powerType == null)
                {
                    GD.PrintErr($"[InfiniteUpgrade] Power type not found: {typeName}");
                    continue;
                }

                // 调试：列出 PowerCmd.Apply 所有重载
                MethodInfo? applyMethod = null;
                foreach (var m in typeof(PowerCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "Apply" && m.IsGenericMethodDefinition)
                    {
                        var ps = string.Join(", ", m.GetParameters().Select(pp => $"{pp.ParameterType.Name} {pp.Name}"));
                        GD.Print($"[InfiniteUpgrade] PowerCmd.Apply<{m.GetGenericArguments()[0].Name}>({ps})");
                        var p = m.GetParameters();
                        // 选第一个参数是 Creature 且参数数量最少的（避免 IEnumerable 重载）
                        if (p.Length > 0 && p[0].ParameterType == typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)
                            && (applyMethod == null || p.Length < applyMethod.GetParameters().Length))
                        {
                            applyMethod = m;
                        }
                    }
                }

                if (applyMethod == null)
                {
                    GD.PrintErr($"[InfiniteUpgrade] PowerCmd.Apply method not found");
                    continue;
                }

                var genericMethod = applyMethod.MakeGenericMethod(powerType);
                var parms = genericMethod.GetParameters();
                var args = new object?[parms.Length];

                // 按参数类型填充: Creature, decimal, Creature?, CardModel?, bool
                for (int i = 0; i < parms.Length; i++)
                {
                    var pt = parms[i].ParameterType;
                    if (pt == typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature))
                        args[i] = player.Creature;
                    else if (pt == typeof(decimal))
                        args[i] = (decimal)kv.Value;
                    else if (pt == typeof(MegaCrit.Sts2.Core.Models.CardModel))
                        args[i] = null;
                    else if (pt == typeof(bool))
                        args[i] = false;
                    else
                        args[i] = null; // nullable Creature? or any other type
                }

                var result = genericMethod.Invoke(null, args);
                if (result is Task t)
                    await t;

                GD.Print($"[InfiniteUpgrade] Applied {kv.Key} x{kv.Value}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[InfiniteUpgrade] Apply {kv.Key} error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
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
