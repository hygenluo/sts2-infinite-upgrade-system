using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 技能效果统一承载（v2.0，官方范式）。
///
/// 背景：本 mod 的技能/能力效果原本做在 Harmony `Hook.AfterXxx` Postfix + fire-and-forget async 里，
/// 与 checksum 生成存在时序竞争 → 多人反复分歧。官方范式是「效果做成模型的 hook 回调」——
/// `Hook.AfterXxx` 会遍历 combatState 所有模型（含 creature 的 powers）并在游戏 action 管线内 await，
/// 因此做成隐藏 power 后两端确定性一致（参照官方地狱狂徒 HellraiserPower）。
///
/// 本类承载全部技能/能力效果：战斗开始时给「有任意技能或能力」的玩家施加（隐藏、不可叠加），
/// 各 hook 按「主题玩家（power 所属玩家）」判断已购技能并施加。
/// </summary>
public sealed class SkillEffectsPower : CustomPowerModel
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override bool IsVisibleInternal => false;

    private Player? OwnerPlayer => Owner?.Player;

    /// <summary>「下一张牌免费」技能内部状态（照 VoidFormPower 的 per-instance data 范式）。</summary>
    private sealed class FreeData
    {
        public int freeCardCharge;
    }

    public override object InitInternalData() => new FreeData();

    private FreeData MyData => GetInternalData<FreeData>();

    // ═══════════════════════════════════════════════════════════════
    // 打出牌触发
    // ═══════════════════════════════════════════════════════════════

    public override async Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        var player = cardPlay?.Player;
        if (player == null || player != OwnerPlayer) return;
        var card = cardPlay!.Card;
        if (card == null) return;
        var creature = player.Creature;
        if (creature == null) return;

        SkillRegistry.AddCombatPlay(player); // 更新 UI 打牌计数缓存（store 两端一致）

        // 每4张+力量/敏捷（确定性计数：官方 History.CardPlaysStarted；plays<=0 防御 off-by-one）
        int plays = CombatManager.Instance?.History?.CardPlaysStarted?.Count(e => e.CardPlay?.Player == player) ?? 0;
        if (plays > 0 && plays % 4 == 0)
        {
            if (UpgradeDataStore.HasSkill(player, "strength_every_4_plays"))
                AbilityOperationHelper.ApplyPowerSync(creature, "strength", 1);
            if (UpgradeDataStore.HasSkill(player, "agility_every_4_plays"))
                AbilityOperationHelper.ApplyPowerSync(creature, "dexterity", 1);
        }

        if (UpgradeDataStore.HasSkill(player, "block_on_play"))
            await CreatureCmd.GainBlock(creature, 1m, default, null, false);

        if (UpgradeDataStore.HasSkill(player, "forge_on_play"))
            await ForgeCmd.Forge(1m, player, card);

        if (UpgradeDataStore.HasSkill(player, "vigor_on_skill_play") && card.Type == CardType.Skill)
            await AbilityOperationHelper.ApplyPower(creature, "vigor", 2);

        if (UpgradeDataStore.HasSkill(player, "summon_on_play"))
            await OstyCmd.Summon(choiceContext, player, 1m, card);

        var enemies = creature.CombatState?.Enemies;
        if (enemies != null)
        {
            if (UpgradeDataStore.HasSkill(player, "dmg_on_ethereal_play") && card.Keywords.Contains(CardKeyword.Ethereal))
                await CreatureCmd.Damage(choiceContext, enemies, 3m, default, creature);

            if (UpgradeDataStore.HasSkill(player, "poison_all_on_card_play"))
                foreach (var enemy in enemies)
                    await AbilityOperationHelper.ApplyPower(enemy, "poison", 1, creature, null);
        }

        if (UpgradeDataStore.HasSkill(player, "draw_on_power_play") && card.Type == CardType.Power)
            await CardPileCmd.Draw(choiceContext, player);

        // 技能（通用）：每打出1张能力牌，下一张牌免费（费用/辉星为0；打出后其余牌恢复原价）
        if (UpgradeDataStore.HasSkill(player, "free_next_card_on_power"))
        {
            if (card.Type == CardType.Power)
                MyData.freeCardCharge = 1;  // 打出能力牌 → 授予「下一张免费」
            else if (MyData.freeCardCharge > 0)
                MyData.freeCardCharge = 0;  // 打出非能力牌 → 消耗免费额度
        }
    }

    public override async Task AfterHandEmptied(PlayerChoiceContext choiceContext, Player player)
    {
        if (player == null || player != OwnerPlayer) return;
        if (!UpgradeDataStore.HasSkill(player, "draw_when_no_hand")) return;
        await CardPileCmd.Draw(choiceContext, player);
    }

    public override async Task AfterCardExhausted(PlayerChoiceContext choiceContext, CardModel card, bool causedByEthereal)
    {
        var player = card?.Owner;
        if (player == null || player != OwnerPlayer) return;

        if (UpgradeDataStore.HasSkill(player, "draw_on_exhaust"))
            await CardPileCmd.Draw(choiceContext, player);

        // 技能（铁甲战士）：每消耗1张牌，全体敌人 +2 易伤
        if (UpgradeDataStore.HasSkill(player, "vulnerable_on_exhaust"))
        {
            var enemies = player.Creature?.CombatState?.Enemies;
            if (enemies != null)
                foreach (var enemy in enemies)
                    await AbilityOperationHelper.ApplyPower(enemy, "vulnerable", 2);
        }
    }

    public override int ModifyAttackHitCount(AttackCommand attack, int hitCount)
    {
        // 奥斯提是召唤物（pet），attacker.Player 恒为 null，须用 PetOwner 定位召唤者
        var attacker = attack?.Attacker;
        if (attacker?.Monster is not Osty) return hitCount;
        var owner = attacker.PetOwner;
        if (owner == null || owner != OwnerPlayer) return hitCount;
        if (!UpgradeDataStore.HasSkill(owner, "osty_extra_attack")) return hitCount;
        return hitCount * 2;
    }

    // ═══════════════════════════════════════════════════════════════
    // 「下一张牌免费」费用修改（照 VoidFormPower 的 TryModifyXxxCost 范式）
    // ═══════════════════════════════════════════════════════════════

    public override bool TryModifyEnergyCostInCombat(CardModel card, decimal originalCost, out decimal modifiedCost)
    {
        modifiedCost = originalCost;
        if (!CanFreeThisCard(card)) return false;
        modifiedCost = 0;
        return true;
    }

    public override bool TryModifyStarCost(CardModel card, decimal originalCost, out decimal modifiedCost)
    {
        modifiedCost = originalCost;
        if (!CanFreeThisCard(card)) return false;
        modifiedCost = 0;
        return true;
    }

    /// <summary>有「下一张免费」额度且卡属于本玩家（手牌/打出堆）→ 可免费。</summary>
    private bool CanFreeThisCard(CardModel card)
    {
        if (Owner == null || MyData.freeCardCharge <= 0) return false;
        if (card?.Owner?.Creature != Owner) return false;
        if (card.Pile?.Type is not (PileType.Hand or PileType.Play)) return false;
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // 回合开始触发
    // ═══════════════════════════════════════════════════════════════

    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        if (player == null || player != OwnerPlayer) return;
        var creature = player.Creature;
        if (creature == null) return;

        MyData.freeCardCharge = 0; // 回合开始清空上一回合残留的「下一张免费」额度

        // 能力：每回合辉星 +N（update06 BUG修复：由战斗开始一次性改为每回合开始时发放）
        int stars = UpgradeDataStore.GetBoost(player, "stars");
        if (stars > 0)
            await PlayerCmd.GainStars(stars, player);

        // 能力：每回合铸造 +5N（update06 BUG修复：此前 forge boost 从未被应用）
        int forge = UpgradeDataStore.GetBoost(player, "forge");
        if (forge > 0)
            await ForgeCmd.Forge(5m * forge, player, null);

        // 技能（通用）：回合开始时，对所有敌人造成 x 点伤害 y 次（x=力量+1,y=敏捷+1,最低1）
        if (UpgradeDataStore.HasSkill(player, "dmg_x_times_at_turn_start"))
        {
            var enemies = creature.CombatState?.Enemies;
            if (enemies != null && enemies.Count > 0)
            {
                int str = creature.GetPower<StrengthPower>()?.Amount ?? 0;
                int dex = creature.GetPower<DexterityPower>()?.Amount ?? 0;
                int x = Math.Max(1, str + 1);
                int y = Math.Max(1, dex + 1);
                for (int i = 0; i < y; i++)
                    await CreatureCmd.Damage(choiceContext, enemies, x, default, creature);
            }
        }

        if (UpgradeDataStore.HasSkill(player, "block_at_turn_start"))
        {
            int exhaust = player.Piles.FirstOrDefault(p => p.Type == PileType.Exhaust)?.Cards.Count ?? 0;
            if (exhaust > 0)
                await CreatureCmd.GainBlock(creature, exhaust, default, null, false);
        }

        if (UpgradeDataStore.HasSkill(player, "str_at_turn_start"))
            await AbilityOperationHelper.ApplyPower(creature, "strength", 2);

        // 技能（铁甲战士）：回合开始时，获取全体敌人易伤总和点力量
        if (UpgradeDataStore.HasSkill(player, "str_from_vulnerable_at_turn_start"))
        {
            var enemies = creature.CombatState?.Enemies;
            if (enemies != null)
            {
                int sum = 0;
                foreach (var enemy in enemies)
                    sum += enemy.GetPower<VulnerablePower>()?.Amount ?? 0;
                if (sum > 0)
                    await AbilityOperationHelper.ApplyPower(creature, "strength", sum);
            }
        }

        if (UpgradeDataStore.HasSkill(player, "heal_at_turn_start"))
            await CreatureCmd.Heal(creature, 3);

        if (UpgradeDataStore.HasSkill(player, "draw_at_turn_start"))
            await CardPileCmd.Draw(choiceContext, player);

        if (UpgradeDataStore.HasSkill(player, "summon_at_turn_start"))
            await OstyCmd.Summon(choiceContext, player, 2m, null);

        if (UpgradeDataStore.HasSkill(player, "orb_at_turn_start"))
        {
            var orb = OrbModel.GetRandomOrb(player.RunState.Rng.CombatOrbGeneration).ToMutable();
            await OrbCmd.Channel(choiceContext, orb, player);
        }

        // 技能（储君）：回合开始时，手牌添加一张升级过的无色牌，本回合免费
        if (UpgradeDataStore.HasSkill(player, "add_colorless_card_at_turn_start"))
        {
            var pool = ModelDb.CardPool<ColorlessCardPool>();
            var cards = pool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint);
            CardModel? generated = CardFactory.GetDistinctForCombat(player, cards, 1, player.RunState.Rng.CombatCardGeneration).FirstOrDefault();
            if (generated != null)
            {
                CardCmd.Upgrade(generated);
                generated.SetToFreeThisTurn();
                await CardPileCmd.AddGeneratedCardToCombat(generated, PileType.Hand, player);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 回合结束触发
    // ═══════════════════════════════════════════════════════════════

    public override async Task AfterSideTurnStart(CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
    {
        if (side != CombatSide.Enemy) return; // 敌方回合开始 = 玩家回合结束
        var player = OwnerPlayer;
        if (player?.Creature == null || combatState == null) return;
        if (!UpgradeDataStore.HasSkill(player, "orb_dmg_at_turn_end")) return;
        int orbCount = player.PlayerCombatState?.OrbQueue?.Orbs?.Count ?? 0;
        if (orbCount <= 0) return;
        await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), combatState.Enemies, orbCount, default, player.Creature);
    }

    public override async Task BeforeSideTurnEnd(PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (side != CombatSide.Player) return; // 玩家回合结束
        var player = OwnerPlayer;
        if (player?.Creature == null) return;

        // 技能（铁甲战士）：回合结束时，格挡翻倍
        if (UpgradeDataStore.HasSkill(player, "double_block_at_turn_end"))
        {
            int block = player.Creature.Block;
            if (block > 0)
                await CreatureCmd.GainBlock(player.Creature, block, default, null, false);
        }

        // 技能（亡灵契约师）：回合结束时，打出消耗牌堆中的所有虚无牌（排除诅咒/状态牌）
        if (UpgradeDataStore.HasSkill(player, "play_ethereal_from_exhaust"))
        {
            var exhaustPile = player.Piles.FirstOrDefault(p => p.Type == PileType.Exhaust);
            var etherealCards = exhaustPile?.Cards
                .Where(c => c.Keywords.Contains(CardKeyword.Ethereal) && c.Type != CardType.Curse && c.Type != CardType.Status)
                .ToList();
            if (etherealCards != null)
                foreach (var c in etherealCards)
                {
                    c.ExhaustOnNextPlay = true; // 打出后回到消耗牌堆（而非弃牌/抽牌堆）
                    await CardCmd.AutoPlay(choiceContext, c, null);
                }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 事件触发
    // ═══════════════════════════════════════════════════════════════

    public override async Task AfterCardDrawnEarly(PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        if (card?.Owner == null || card.Owner != OwnerPlayer) return;
        if (!UpgradeDataStore.HasSkill(card.Owner, "auto_play_power_on_draw")) return;
        if (card.Type != CardType.Power) return;
        await CardCmd.AutoPlay(choiceContext, card, null);
    }

    public override async Task AfterDamageReceived(PlayerChoiceContext choiceContext, Creature target, DamageResult result, ValueProp props, Creature? dealer, CardModel? cardSource)
    {
        if (target?.Player == null || target.Player != OwnerPlayer) return;
        if (!UpgradeDataStore.HasSkill(target.Player, "draw_on_hp_loss")) return;
        await CardPileCmd.Draw(choiceContext, target.Player);
    }

    public override async Task AfterCardDiscarded(PlayerChoiceContext choiceContext, CardModel card)
    {
        var player = card?.Owner;
        if (player == null || player != OwnerPlayer) return;
        var creature = player.Creature;

        // 技能（静默猎手）：每丢弃一张牌 +1 敏捷
        if (creature != null && UpgradeDataStore.HasSkill(player, "dexterity_on_discard"))
            await AbilityOperationHelper.ApplyPower(creature, "dexterity", 1);

        // 技能（静默猎手）：每丢弃一张牌，全体敌人 +1 虚弱
        if (UpgradeDataStore.HasSkill(player, "weak_on_discard"))
        {
            var enemies = creature?.CombatState?.Enemies;
            if (enemies != null)
                foreach (var enemy in enemies)
                    await AbilityOperationHelper.ApplyPower(enemy, "weak", 1);
        }
    }

    public override async Task AfterPowerAmountChanged(PlayerChoiceContext choiceContext, PowerModel power, decimal amount, Creature? applier, CardModel? cardSource)
    {
        if (power == null || applier == null || amount <= 0) return;

        // 技能（亡灵契约师）：每当你给予敌人灾厄时，该敌人失去1点力量
        if (power is DoomPower)
        {
            var giver = applier.Player;
            if (giver != null && giver == OwnerPlayer && UpgradeDataStore.HasSkill(giver, "enemy_lose_str_on_doom"))
            {
                var enemy = power.Owner;
                if (enemy != null)
                    await AbilityOperationHelper.ApplyStrengthLoss(enemy, 1, applier);
            }
        }

        // 技能（静默猎手）：每当你给予敌人中毒时，获得3格挡
        if (power is not PoisonPower) return;
        var player = applier.Player;
        if (player == null || player != OwnerPlayer) return;
        if (!UpgradeDataStore.HasSkill(player, "block_on_poison")) return;
        await CreatureCmd.GainBlock(player.Creature, 3m, default, null, false);
    }

    public override async Task AfterForge(decimal amount, Player forger, AbstractModel? source)
    {
        if (forger == null || forger != OwnerPlayer) return;
        if (!UpgradeDataStore.HasSkill(forger, "sovereign_blade_block_on_forge")) return;
        await AbilityOperationHelper.AddSovereignBladeForgeParry(forger);
    }

    public override async Task AfterStarsGained(int amount, Player gainer)
    {
        if (gainer == null || gainer != OwnerPlayer) return;
        if (amount <= 0) return;
        if (!UpgradeDataStore.HasSkill(gainer, "enemy_lose_str_on_star")) return;
        var enemies = gainer.Creature?.CombatState?.Enemies;
        if (enemies == null) return;
        foreach (var enemy in enemies)
            await AbilityOperationHelper.ApplyStrengthLoss(enemy, amount, gainer.Creature);
    }

    public override async Task AfterDeath(PlayerChoiceContext choiceContext, Creature creature, bool wasRemovalPrevented, float deathAnimLength)
    {
        if (creature == null || wasRemovalPrevented) return;
        if (!creature.IsEnemy) return;
        var player = OwnerPlayer;
        if (player == null || UpgradeDataStore.GetBoost(player, "gold_on_kill") <= 0) return;
        await PlayerCmd.GainGold(20m, player);
    }
}
