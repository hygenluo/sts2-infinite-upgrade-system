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

public static class AbilityOperationHelper
{
    private static readonly Dictionary<string, int> s_boosts = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static bool s_appliedThisRun;

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
            case "energy": player.MaxEnergy += count; break;
            case "orbSlot": player.BaseOrbSlotCount += count; break;
        }
    }

    /// <summary>CombatSetUp时应用初始能力（只执行一次）。</summary>
    public static async void ApplyInitialBoosts()
    {
        if (s_appliedThisRun) return;
        s_appliedThisRun = true;

        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null) { GD.Print("[IU] No creature yet"); return; }

        var creature = player.Creature;
        GD.Print($"[IU] Creature: {creature.GetType().Name}, CanReceivePowers={creature.CanReceivePowers}, CombatState={creature.CombatState != null}");

        foreach (var kv in s_boosts)
        {
            if (kv.Value <= 0) continue;
            await ApplyOnePower(creature, kv.Key, kv.Value);
        }
    }

    private static async Task ApplyOnePower(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, string key, int count)
    {
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

        try
        {
            // 1. 查找类型
            Type? powerType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                powerType = asm.GetType(typeName);
                if (powerType != null) break;
            }
            if (powerType == null) { GD.PrintErr($"[IU] Type {typeName} not found"); return; }
            GD.Print($"[IU] Found type: {powerType.FullName}");

            // 2. ModelDb.Power<T>()
            var modelDb = Type.GetType("MegaCrit.Sts2.Core.Models.ModelDb, sts2");
            if (modelDb == null) { foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { modelDb = a.GetType("MegaCrit.Sts2.Core.Models.ModelDb"); if (modelDb != null) break; } }
            if (modelDb == null) { GD.PrintErr("[IU] ModelDb not found"); return; }

            var powerGetter = modelDb.GetMethod("Power", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (powerGetter == null) { GD.PrintErr("[IU] ModelDb.Power() not found"); return; }
            var genericGetter = powerGetter.MakeGenericMethod(powerType);
            var template = genericGetter.Invoke(null, null);
            if (template == null) { GD.PrintErr($"[IU] Power<T>() returned null for {key}"); return; }
            GD.Print($"[IU] Template: {template.GetType().Name}");

            // 3. MutableClone() — AbstractModel 上的 public 方法
            var cloneMethod = template.GetType().GetMethod("MutableClone",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (cloneMethod == null) { GD.PrintErr("[IU] MutableClone not found"); return; }
            var mutable = cloneMethod.Invoke(template, null);
            if (mutable == null) { GD.PrintErr("[IU] MutableClone null"); return; }
            GD.Print($"[IU] Mutable: {mutable.GetType().Name}");

            // 4. ApplyInternal(Creature, decimal, bool)
            var applyMethod = mutable.GetType().GetMethod("ApplyInternal",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (applyMethod == null) { GD.PrintErr("[IU] ApplyInternal not found"); return; }
            GD.Print($"[IU] ApplyInternal params: {string.Join(",", applyMethod.GetParameters().Select(p => p.ParameterType.Name))}");

            applyMethod.Invoke(mutable, new object[] { creature, (decimal)count, false });
            GD.Print($"[IU] ✓ Applied {key} x{count}");

            // 5. 验证
            var hasPowerMethod = creature.GetType().GetMethod("HasPower", Type.EmptyTypes);
            if (hasPowerMethod != null)
            {
                var genericHas = hasPowerMethod.MakeGenericMethod(powerType);
                var has = (bool)genericHas.Invoke(creature, null)!;
                GD.Print($"[IU] HasPower<{key}> = {has}");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[IU] {key}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public static void ResetForNewRun() => s_appliedThisRun = false;

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
        s_appliedThisRun = false;
        try
        {
            var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".";
            var path = Path.Combine(modDir, "runs", $"abilities_{seed}.json");
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path), s_jsonOptions);
            if (data != null) foreach (var kv in data) s_boosts[kv.Key] = kv.Value;
        }
        catch (Exception ex) { Log.Warn($"Ability load: {ex.Message}"); }
    }
}
