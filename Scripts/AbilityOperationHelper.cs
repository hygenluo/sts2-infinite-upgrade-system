using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace InfiniteUpgradeSystem;

public static class AbilityOperationHelper
{
    private static readonly Dictionary<string, int> s_boosts = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static bool s_appliedThisRun;

    /// <summary>购买能力（方案 B）：扣点 + 加 boost 写入指定玩家的 store。由同步 action 两端执行。</summary>
    public static bool TryPurchase(Player player, string key, int cost)
    {
        if (player == null) return false;
        if (UpgradeDataStore.GetPoints(player) < cost) return false;
        UpgradeDataStore.Mutate(player, d =>
        {
            d.Points -= cost;
            d.Boosts.TryGetValue(key, out int cur);
            d.Boosts[key] = cur + 1;
        });
        var seed = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        SaveCheckpoint(seed);
        return true;
    }

    public static int GetBoost(string key) => s_boosts.TryGetValue(key, out int v) ? v : 0;

    /// <summary>s_boosts 副本（供 UpgradeDataStore 旧档迁移，Step 1）。</summary>
    public static Dictionary<string, int> SnapshotBoosts() => new(s_boosts);

    /// <summary>购买即时生效能力（hp/energy/orbSlot）。由同步 action 两端执行。</summary>
    public static async Task ApplyImmediate(Player player, string key)
    {
        if (player == null) return;
        int count = UpgradeDataStore.GetBoost(player, key);
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

    /// <summary>
    /// 战斗开始：给所有玩家按各自的 store 数据施加持久化 boost（方案 B，遍历不按本地玩家）。
    /// CombatSetUp 事件在两端同一逻辑点触发，store 数据两端一致则结果确定性一致。
    /// </summary>
    public static async void ApplyInitialBoosts(CombatState state)
    {
        if (s_appliedThisRun) return;
        s_appliedThisRun = true;
        if (state == null) return;
        foreach (var player in state.Players)
        {
            if (player?.Creature == null) continue;
            foreach (var kv in UpgradeDataStore.For(player).Boosts)
            {
                if (kv.Value <= 0) continue;
                await ApplyPower(player.Creature, kv.Key, kv.Value);
            }
        }
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
    /// <summary>
    /// 同步施加 Power（publicized 直接调用，无 await；用于两端本地确定性同步的场合，如每4张触发）。
    /// 不触发 Hook 链（Before/AfterPowerAmountChanged）——PowerCmd.Apply 在战斗内有 CustomScaledWait
    /// 跨帧 await，fire-and-forget 调用会与 checksum 时序竞争；此处两端一致优先。
    /// 已有同类 power 时叠加（PowerCmd 的重复应用检查对同类会抛异常）。
    /// </summary>
    public static void ApplyPowerSync(Creature creature, string key, int count)
    {
        if (creature == null || count <= 0) return;
        try
        {
            switch (key)
            {
                case "strength": ApplySync<StrengthPower>(creature, count); break;
                case "dexterity": ApplySync<DexterityPower>(creature, count); break;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[IU] ApplyPowerSync {key}: {ex}");
        }
    }

    private static void ApplySync<T>(Creature creature, int count) where T : PowerModel
    {
        var existing = creature.GetPower<T>();
        if (existing != null)
        {
            existing.SetAmount(existing.Amount + count, false);
            return;
        }
        var power = ModelDb.Power<T>().ToMutable();
        power.ApplyInternal(creature, count, false);
    }

    /// <summary>
    /// 应用 Power（方案 B：走游戏原生 PowerCmd.Apply，进同步 Hook 链路）。
    /// key 映射：能力系统 + 技能效果（vigor/weak 等）。
    /// PowerCmd.Apply 自带叠加（HasPower → ModifyAmount）与完整 Hook 链
    /// （BeforePowerAmountChanged / ModifyPowerAmountGiven/Received / AfterPowerAmountChanged），
    /// 替代旧反射 ApplyInternal（绕过 Hook，导致多人 checksum 分歧）。
    /// 注意：战斗内其内部有 CustomScaledWait 跨帧 await——fire-and-forget 调用会与 checksum
    /// 时序竞争，需走 action（如 ApplyEvery4）或改用 ApplyPowerSync。
    /// </summary>
    public static async Task ApplyPower(Creature creature, string key, int count, Creature? applier = null, CardModel? cardSource = null, PlayerChoiceContext? context = null)
    {
        if (creature == null || count <= 0) return;
        // PowerCmd.Apply 需要 PlayerChoiceContext；skill hook 有 context，无 context 场景（如 CombatSetUp）
        // 用无操作 BlockingPlayerChoiceContext（两端确定性）。
        context ??= new BlockingPlayerChoiceContext();
        try
        {
            switch (key)
            {
                case "strength": await PowerCmd.Apply<StrengthPower>(context, creature, count, applier, cardSource); break;
                case "dexterity": await PowerCmd.Apply<DexterityPower>(context, creature, count, applier, cardSource); break;
                case "focus": await PowerCmd.Apply<FocusPower>(context, creature, count, applier, cardSource); break;
                case "plating": await PowerCmd.Apply<PlatingPower>(context, creature, count, applier, cardSource); break;
                case "thorns": await PowerCmd.Apply<ThornsPower>(context, creature, count, applier, cardSource); break;
                case "artifact": await PowerCmd.Apply<ArtifactPower>(context, creature, count, applier, cardSource); break;
                case "blockKeep": await PowerCmd.Apply<BarricadePower>(context, creature, count, applier, cardSource); break;
                case "vigor": await PowerCmd.Apply<VigorPower>(context, creature, count, applier, cardSource); break;
                case "weak": await PowerCmd.Apply<WeakPower>(context, creature, count, applier, cardSource); break;
                case "poison": await PowerCmd.Apply<PoisonPower>(context, creature, count, applier, cardSource); break;
                case "parry": await PowerCmd.Apply<ParryPower>(context, creature, count, applier, cardSource); break;
                case "freeFirstCard": await PowerCmd.Apply<VoidFormPower>(context, creature, count, applier, cardSource); break;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[IU] ApplyPower {key}: {ex}");
        }
    }

    /// <summary>
    /// 技能「每铸造一次君王之剑永久+1格挡」：铸造时给 forger +1 招架。
    /// 招架数作为持久 boost 写入每玩家 store（方案 B），随 run 存档持久化，
    /// 每场战斗开始由 ApplyInitialBoosts 重新应用 → 跨战斗永久。
    /// </summary>
    public static async Task AddSovereignBladeForgeParry(Player player)
    {
        if (player == null) return;
        UpgradeDataStore.Mutate(player, d =>
        {
            d.Boosts.TryGetValue("parry", out int cur);
            d.Boosts["parry"] = cur + 1;
        });
        if (player.Creature != null)
            await ApplyPower(player.Creature, "parry", 1);
    }

    /// <summary>用 store 数据替换本地缓存 s_boosts（UI 显示用，方案 B 本地玩家缓存）。</summary>
    public static void ReplaceBoosts(Dictionary<string, int> boosts)
    {
        s_boosts.Clear();
        foreach (var kv in boosts)
            s_boosts[kv.Key] = kv.Value;
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
