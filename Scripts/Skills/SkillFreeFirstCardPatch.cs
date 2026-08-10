using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>储君技能「你的回合开始时，免费打出第一张牌」共享状态：本回合已打出牌数。</summary>
public static class SkillFreeFirstCardState
{
    /// <summary>本回合已打出的牌数（AfterPlayerTurnStart 归零，AfterCardPlayed 自增）。</summary>
    public static int CardsPlayedThisTurn;
}

/// <summary>回合开始归零打牌计数（每回合重置，首牌免费资格刷新）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
public static class SkillFreeFirstCardResetPatch
{
    public static void Postfix(PlayerChoiceContext choiceContext, Player player)
    {
        SkillContextCache.Last = choiceContext;
        SkillFreeFirstCardState.CardsPlayedThisTurn = 0;
    }
}

/// <summary>每打出一张牌自增计数（只统计拥有该技能的玩家）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
public static class SkillFreeFirstCardCountPatch
{
    public static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        SkillContextCache.Last = choiceContext;
        var player = cardPlay?.Player;
        if (player == null || !SkillRegistry.Has("free_first_card")) return;
        SkillFreeFirstCardState.CardsPlayedThisTurn++;
    }
}

/// <summary>
/// 首牌费用归零：ModifyEnergyCostInCombat 在扣费（SpendEnergy → LoseEnergy）之前调用，
/// 而 AfterCardPlayed 自增发生在扣费之后 —— 因此计数==0 时把费用设为 0，首牌免费，次牌恢复原价。
/// 已知副作用：本回合首牌打出前，所有牌的费用计算都会返回 0（语义正确——首张打出的牌免费）。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyEnergyCostInCombat))]
public static class SkillFreeFirstCardCostPatch
{
    public static void Postfix(CombatState combatState, CardModel card, ref decimal __result)
    {
        if (!SkillRegistry.Has("free_first_card")) return;
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null || card?.Owner == null || card.Owner != player) return;
        if (SkillFreeFirstCardState.CardsPlayedThisTurn > 0) return;
        __result = 0m;
    }
}
