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
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

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

    /// <summary>
    /// 应用 Power（public 供技能系统复用，Phase S2）。
    /// key 映射：能力系统 + 技能效果（vigor/weak）。
    ///
    /// 叠加处理（Phase S2 修复）：Creature.ApplyPowerInternal 对已存在的同类 Power
    /// 直接抛异常（IL 反汇编确认，重复应用检查）——第二次触发同类技能会失败。
    /// 因此：已有同类 Power 时改用 SetAmount 叠加数量（触发 PowerModified 更新显示与数值），
    /// 否则走「模板克隆 + ApplyInternal」应用路径。
    /// </summary>
    public static async Task ApplyOnePower(Creature creature, string key, int count)
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
            "vigor" => "MegaCrit.Sts2.Core.Models.Powers.VigorPower",
            "weak" => "MegaCrit.Sts2.Core.Models.Powers.WeakPower",
            "poison" => "MegaCrit.Sts2.Core.Models.Powers.PoisonPower",
            "parry" => "MegaCrit.Sts2.Core.Models.Powers.ParryPower",
            "freeFirstCard" => "MegaCrit.Sts2.Core.Models.Powers.VoidFormPower",
            _ => null
        };
        if (typeName == null) return;
        try
        {
            Type? powerType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            { powerType = asm.GetType(typeName); if (powerType != null) break; }
            if (powerType == null) return;

            // 1) 已有同类 Power → 叠加数量（避免 ApplyPowerInternal 的重复类型异常）
            var existing = GetExistingPower(creature, powerType);
            if (existing != null)
            {
                existing.SetAmount(existing._amount + count, false);
                return;
            }

            // 2) 原路径：模板克隆 + ApplyInternal
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
        catch (Exception ex)
        {
            // 解包 TargetInvocationException，打印真实内层异常与堆栈（技能战斗中应用失败排查）
            var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                ? tie.InnerException
                : ex;
            GD.PrintErr($"[IU] ApplyPower {key}: {inner}");
        }
    }

    /// <summary>
    /// 技能「每铸造一次君王之剑永久+1格挡」：铸造时给玩家 +1 招架。
    /// 君王之剑格挡 = 招架层数（CalculatedBlockVar，参照招架能力牌：OnPlay 仅在 GetOwnerParryAmount>0 时 GainBlock）。
    /// 招架数作为持久 boost 存储（s_boosts["parry"]），每场战斗开始由 ApplyInitialBoosts 重新应用 → 跨战斗永久。
    /// </summary>
    public static async Task AddSovereignBladeForgeParry(Player player)
    {
        s_boosts.TryGetValue("parry", out int cur);
        s_boosts["parry"] = cur + 1;
        var seed = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        SaveCheckpoint(seed);
        if (player?.Creature != null)
            await ApplyOnePower(player.Creature, "parry", 1);
    }

    /// <summary>查找生物身上已应用的指定类型 Power（publicized 泛型方法反射调用）。</summary>
    private static PowerModel? GetExistingPower(Creature creature, Type powerType)
    {
        try
        {
            var hasPower = creature.GetType()
                .GetMethods()
                .FirstOrDefault(m => m.Name == "HasPower" && m.GetParameters().Length == 0 && m.IsGenericMethodDefinition)?
                .MakeGenericMethod(powerType)
                .Invoke(creature, null);
            if (hasPower is not true) return null;

            var power = creature.GetType()
                .GetMethods()
                .FirstOrDefault(m => m.Name == "GetPower" && m.GetParameters().Length == 0 && m.IsGenericMethodDefinition)?
                .MakeGenericMethod(powerType)
                .Invoke(creature, null) as PowerModel;
            return power;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[IU] GetExistingPower: {ex.Message}");
            return null;
        }
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
