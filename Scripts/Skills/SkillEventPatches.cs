using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 最近一次可用的 PlayerChoiceContext。
/// 部分钩子（如 AfterSideTurnStart）不提供 context，复用最近一次打牌/事件的 context。
/// </summary>
public static class SkillContextCache
{
    public static PlayerChoiceContext? Last { get; set; }
}

/// <summary>1. 每当有一张牌被消耗时，抽一张牌（铁甲战士）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
public static class SkillExhaustPatch
{
    public static void Postfix(PlayerChoiceContext choiceContext, CardModel card)
    {
        SkillContextCache.Last = choiceContext;
        var player = card?.Owner;
        if (player == null || !SkillRegistry.Has("draw_on_exhaust")) return;
        _ = HandleAsync(choiceContext, player);
    }

    private static async Task HandleAsync(PlayerChoiceContext context, Player player)
    {
        try
        {
            await CardPileCmd.Draw(context, player);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Skill exhaust draw error: {ex}");
        }
    }
}

/// <summary>2. 每当失去生命值时，抽一张牌（铁甲战士）——每段伤害触发一次（多段攻击 6×3 触发 3 次）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
public static class SkillHpLossPatch
{
    public static void Postfix(PlayerChoiceContext choiceContext, Creature target)
    {
        SkillContextCache.Last = choiceContext;
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null || target != player.Creature) return;
        if (!SkillRegistry.Has("draw_on_hp_loss")) return;
        _ = HandleAsync(choiceContext, player);
    }

    private static async Task HandleAsync(PlayerChoiceContext context, Player player)
    {
        try
        {
            await CardPileCmd.Draw(context, player);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Skill hp-loss draw error: {ex}");
        }
    }
}

/// <summary>3. 每当丢弃一张牌时，给予所有敌人1层虚弱（静默猎手）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDiscarded))]
public static class SkillDiscardPatch
{
    public static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card)
    {
        SkillContextCache.Last = choiceContext;
        var player = card?.Owner;
        if (player == null || !SkillRegistry.Has("weak_on_discard")) return;
        _ = HandleAsync(combatState, player);
    }

    private static async Task HandleAsync(ICombatState combatState, Player player)
    {
        try
        {
            foreach (var enemy in combatState.Enemies)
                await AbilityOperationHelper.ApplyOnePower(enemy, "weak", 1);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Skill discard weak error: {ex}");
        }
    }
}

/// <summary>4. 每当给予敌人中毒时，获得1格挡（静默猎手）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPowerAmountChanged))]
public static class SkillPoisonPatch
{
    public static void Postfix(PlayerChoiceContext choiceContext, PowerModel power, decimal amount, Creature applier)
    {
        SkillContextCache.Last = choiceContext;
        if (power == null || applier == null || amount <= 0) return;
        if (power.GetType().Name != "PoisonPower") return; // 只响应中毒
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null || applier != player.Creature) return; // 必须由玩家施加
        if (!SkillRegistry.Has("block_on_poison")) return;
        // 数值调整（2026-08-04 用户确认）：一次施加中毒（含多层）触发 1 次，格挡 1 → 3
        _ = CreatureCmd.GainBlock(player.Creature, 3m, default, null, false);
    }
}

/// <summary>5. 每回合开始时，获取消耗牌堆数等量格挡（通用）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
public static class SkillTurnStartPatch
{
    public static void Postfix(PlayerChoiceContext choiceContext, Player player)
    {
        SkillContextCache.Last = choiceContext;
        if (player == null || !SkillRegistry.Has("block_at_turn_start")) return;
        _ = HandleAsync(player);
    }

    private static async Task HandleAsync(Player player)
    {
        try
        {
            var exhaustCount = player.Piles.FirstOrDefault(p => p.Type == PileType.Exhaust)?.Cards.Count ?? 0;
            if (exhaustCount > 0)
                await CreatureCmd.GainBlock(player.Creature, exhaustCount, default, null, false);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Skill turn-start block error: {ex}");
        }
    }
}

/// <summary>
/// 你可以在休息处选择任意数量的选项（通用，参照遗物 微型帐篷 MiniatureTent）。
/// 休息处逻辑调用 Hook.ShouldDisableRemainingRestSiteOptions 决定是否禁用剩余选项；
/// 技能已购时强制返回 false（永不禁用）。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldDisableRemainingRestSiteOptions))]
public static class SkillRestSitePatch
{
    public static void Postfix(ref bool __result)
    {
        if (SkillRegistry.Has("rest_all_options"))
            __result = false;
    }
}

/// <summary>6. 回合结束时，每有一个充能球，对所有敌人造成1点伤害（故障机器人）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnStart))]
public static class SkillOrbTurnEndPatch
{
    public static void Postfix(ICombatState combatState, CombatSide side)
    {
        if (side != CombatSide.Enemy) return; // 敌方回合开始 = 玩家回合结束
        if (!SkillRegistry.Has("orb_dmg_at_turn_end")) return;
        var player = CardOperationHelper.GetLocalPlayer();
        var context = SkillContextCache.Last;
        if (player?.Creature == null || context == null || combatState == null) return;
        _ = HandleAsync(combatState, context, player);
    }

    private static async Task HandleAsync(ICombatState combatState, PlayerChoiceContext context, Player player)
    {
        try
        {
            var orbCount = player.PlayerCombatState?.OrbQueue?.Orbs?.Count ?? 0;
            if (orbCount <= 0) return;
            await CreatureCmd.Damage(context, combatState.Enemies, orbCount, default, player.Creature);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Skill orb damage error: {ex}");
        }
    }
}

/// <summary>回合结束时，你的格挡翻倍（铁甲战士）。BeforeSideTurnEnd 在格挡清零前触发。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnEnd))]
public static class SkillDoubleBlockPatch
{
    public static void Postfix(CombatSide side)
    {
        if (side != CombatSide.Player) return; // 玩家回合结束
        if (!SkillRegistry.Has("double_block_at_turn_end")) return;
        var player = CardOperationHelper.GetLocalPlayer();
        if (player?.Creature == null) return;
        _ = HandleAsync(player);
    }

    private static async Task HandleAsync(Player player)
    {
        try
        {
            var block = player.Creature.Block;
            if (block > 0)
                await CreatureCmd.GainBlock(player.Creature, block, default, null, false);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Skill double block error: {ex}");
        }
    }
}

/// <summary>每铸造一次，君王之剑永久获取1格挡（储君）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterForge))]
public static class SkillSovereignBladeBlockPatch
{
    public static void Postfix(ICombatState combatState, decimal amount, Player forger, AbstractModel? source)
    {
        if (forger == null || !SkillRegistry.Has("sovereign_blade_block_on_forge")) return;
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null || forger != player) return;
        try
        {
            // 君王之剑格挡 = CalculationBase + CalculationExtra × Parry → bump 基础值即永久 +1（随卡牌跨战斗持久化，同 AddDamage）
            foreach (var blade in ForgeCmd.GetSovereignBlades(forger, includeExhausted: true))
                blade.DynamicVars.CalculationBase.BaseValue += 1m;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Skill sovereign blade block error: {ex}");
        }
    }
}

/// <summary>每当你抽到能力牌时，自动打出（故障机器人，免费）。参照铁甲战士「地狱狂徒」HellraiserPower.AfterCardDrawnEarly → CardCmd.AutoPlay。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
public static class SkillAutoPlayPowerPatch
{
    public static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        if (card?.Owner == null || !SkillRegistry.Has("auto_play_power_on_draw")) return;
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null || card.Owner != player) return;
        if (card.Type != CardType.Power) return;
        _ = HandleAsync(choiceContext, card);
    }

    private static async Task HandleAsync(PlayerChoiceContext context, CardModel card)
    {
        try
        {
            await CardCmd.AutoPlay(context, card, null);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Skill auto-play power error: {ex}");
        }
    }
}
