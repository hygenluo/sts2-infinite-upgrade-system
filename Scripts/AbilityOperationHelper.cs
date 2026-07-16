using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
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
        // 每次购买立即写盘，避免中途退出丢失
        var seed = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        SaveCheckpoint(seed);
        return true;
    }

    public static int GetBoost(string key) => s_boosts.TryGetValue(key, out int v) ? v : 0;

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
            case "energy": player.MaxEnergy += count; break;
            case "orbSlot": player.BaseOrbSlotCount += count; break;
        }
    }

    public static async void ApplyInitialBoosts()
    {
        if (s_appliedThisRun) return;
        s_appliedThisRun = true;
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null) return;
        var creature = player.Creature;

        foreach (var kv in s_boosts)
        {
            if (kv.Value <= 0) continue;
            await ApplyOnePower(creature, kv.Key, kv.Value);
        }
    }

    /// <summary>CombatSetUp时应用辉星（一次性，非每回合）。</summary>
    public static async void ApplyStarsAtCombatStart()
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;
        if (s_boosts.TryGetValue("stars", out int stars) && stars > 0)
            await PlayerCmd.GainStars(stars, player);
    }

    // 格挡跨回合不消失 — 通过 BarricadePower 实现（与壁垒卡牌相同效果），
    // 由 ApplyInitialBoosts → ApplyOnePower("blockKeep", ...) 在战斗开始时应用。

    public static void ResetForNewRun() => s_appliedThisRun = false;
    public static void ResetForNextCombat() => s_appliedThisRun = false;

    private static async Task ApplyOnePower(Creature creature, string key, int count)
    {
        var typeName = key switch
        {
            "strength" => "MegaCrit.Sts2.Core.Models.Powers.StrengthPower",
            "dexterity" => "MegaCrit.Sts2.Core.Models.Powers.DexterityPower",
            "focus" => "MegaCrit.Sts2.Core.Models.Powers.FocusPower",
            "plating" => "MegaCrit.Sts2.Core.Models.Powers.PlatingPower",
            "thorns" => "MegaCrit.Sts2.Core.Models.Powers.ThornsPower",
            "artifact" => "MegaCrit.Sts2.Core.Models.Powers.ArtifactPower",
            "blockKeep" => "MegaCrit.Sts2.Core.Models.Powers.BarricadePower",
            _ => null
        };
        if (typeName == null) return;
        try
        {
            Type? powerType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            { powerType = asm.GetType(typeName); if (powerType != null) break; }
            if (powerType == null) return;

            var modelDb = Type.GetType("MegaCrit.Sts2.Core.Models.ModelDb, sts2");
            if (modelDb == null) foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { modelDb = a.GetType("MegaCrit.Sts2.Core.Models.ModelDb"); if (modelDb != null) break; }
            if (modelDb == null) return;

            var powerGetter = modelDb.GetMethod("Power", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (powerGetter == null) return;
            var template = powerGetter.MakeGenericMethod(powerType).Invoke(null, null);
            if (template == null) return;

            var cloneMethod = template.GetType().GetMethod("MutableClone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (cloneMethod == null) return;
            var mutable = cloneMethod.Invoke(template, null);
            if (mutable == null) return;

            var applyMethod = mutable.GetType().GetMethod("ApplyInternal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (applyMethod == null) return;
            applyMethod.Invoke(mutable, new object[] { creature, (decimal)count, false });
        }
        catch (Exception ex) { GD.PrintErr($"[IU] ApplyPower {key}: {ex.Message}"); }
    }

    public static void SaveCheckpoint(string seed)
    {
        try
        {
            File.WriteAllText(SavePaths.GetFilePath("abilities", seed), JsonSerializer.Serialize(s_boosts, s_jsonOptions));
        }
        catch (Exception ex) { Log.Error($"Ability save: {ex.Message}"); }
    }

    public static void Load(string seed)
    {
        s_boosts.Clear();
        s_appliedThisRun = false;
        try
        {
            var path = SavePaths.GetFilePath("abilities", seed);
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path), s_jsonOptions);
            if (data != null) foreach (var kv in data) s_boosts[kv.Key] = kv.Value;
        }
        catch (Exception ex) { Log.Warn($"Ability load: {ex.Message}"); }
    }
}
