using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 技能效果（Phase S2：打出牌触发类 9 项）。
/// 所有效果先查 SkillRegistry.Has(id) —— 未购买零开销。
/// 采用 fire-and-forget + try/catch 包装：任何异常都不得抛进游戏主流程。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
public static class SkillCardPlayPatch
{
    // Harmony 按参数名绑定原方法参数 —— 必须与原签名一致（combatState/choiceContext/cardPlay）
    public static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        SkillContextCache.Last = choiceContext;
        var player = cardPlay?.Player;
        var card = cardPlay?.Card;
        if (player == null || card == null || combatState == null) return;
        _ = HandleAsync(combatState, choiceContext, player, card);
    }

    private static async Task HandleAsync(ICombatState state, PlayerChoiceContext context, Player player, CardModel card)
    {
        try
        {
            SkillRegistry.AddCombatPlay();
            if (SkillRegistry.AnyOwned())
                GD.Print($"[IU-Skill] AfterCardPlayed: plays={SkillRegistry.CombatPlayCount} " +
                         $"block={SkillRegistry.Has("block_on_play")} str4={SkillRegistry.Has("strength_every_4_plays")} " +
                         $"agi4={SkillRegistry.Has("agility_every_4_plays")} forge={SkillRegistry.Has("forge_on_play")} " +
                         $"vigor={SkillRegistry.Has("vigor_on_skill_play")} summon={SkillRegistry.Has("summon_on_play")} " +
                         $"ethereal={SkillRegistry.Has("dmg_on_ethereal_play")} power={SkillRegistry.Has("draw_on_power_play")}");

            // 1. 每打出1张牌，都获得1格挡
            if (SkillRegistry.Has("block_on_play"))
                await CreatureCmd.GainBlock(player.Creature, 1m, default, null, false);

            // 2/3. 每当打出4张牌，获得1敏捷/力量（计数 4 的倍数触发）
            if (SkillRegistry.CombatPlayCount % 4 == 0)
            {
                if (SkillRegistry.Has("agility_every_4_plays"))
                    await AbilityOperationHelper.ApplyOnePower(player.Creature, "dexterity", 1);
                if (SkillRegistry.Has("strength_every_4_plays"))
                    await AbilityOperationHelper.ApplyOnePower(player.Creature, "strength", 1);
            }

            // 4. 每打出1张牌时，铸造1
            if (SkillRegistry.Has("forge_on_play"))
                await ForgeCmd.Forge(1m, player, card);

            // 5. 每打出1张技能牌时，获得2点活力
            if (SkillRegistry.Has("vigor_on_skill_play") && card.Type == CardType.Skill)
                await AbilityOperationHelper.ApplyOnePower(player.Creature, "vigor", 2);

            // 6. 每打出1张牌时，召唤1
            if (SkillRegistry.Has("summon_on_play"))
                await OstyCmd.Summon(context, player, 1m, card);

            // 7. 每打出1张虚无牌，对所有敌人造成3点伤害
            if (SkillRegistry.Has("dmg_on_ethereal_play") && card.Keywords.Contains(CardKeyword.Ethereal))
                await CreatureCmd.Damage(context, state.Enemies, 3m, default, player.Creature);

            // 8. 每打出1张能力牌，抽一张牌
            if (SkillRegistry.Has("draw_on_power_play") && card.Type == CardType.Power)
                await CardPileCmd.Draw(context, player);

            // 9. 每打出1张牌，给予所有敌人1层中毒
            if (SkillRegistry.Has("poison_all_on_card_play"))
                foreach (var enemy in state.Enemies)
                    await AbilityOperationHelper.ApplyOnePower(enemy, "poison", 1);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] Skill AfterCardPlayed error: {ex.Message}");
        }
    }

    /// <summary>每场战斗重置打牌计数（BeforeCombatStart）。</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
    public static class ResetCounterPatch
    {
        public static void Postfix() => SkillRegistry.ResetCombatPlay();
    }
}

/// <summary>9. 在你的回合，当你没有手牌时，抽1张牌（参照遗物 不休陀螺 UnceasingTop）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterHandEmptied))]
public static class SkillHandEmptyPatch
{
    // 参数名与原方法一致（choiceContext/player）
    public static void Postfix(PlayerChoiceContext choiceContext, Player player)
    {
        if (player == null || !SkillRegistry.Has("draw_when_no_hand")) return;
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
            GD.PrintErr($"[InfiniteUpgrade] Skill AfterHandEmptied error: {ex}");
        }
    }
}
