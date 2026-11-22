using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 【仅用于旧存档迁移】路线 A 之后本模组不再使用复合附魔：附魔回到原版语义（一卡一附魔）。
///
/// 为什么这个类型不能删：旧存档里卡上写的是 `ENCHANTMENT.COMPOSITE_ENCHANTMENT`。
/// 若把类型删掉，<c>SaveUtil.EnchantmentOrDeprecated</c> 会拿 <c>DeprecatedEnchantment</c> 顶上，
/// 那个空壳会**永久占住唯一的附魔槽位**（原版一卡一附魔），玩家从此再也附不了魔。
/// 留着它，<see cref="CardUpgradeTracker.ReapplyAll"/> 就能识别并
/// <c>ClearEnchantmentInternal()</c> 把槽位清出来，旧记录里的第一个附魔会在随后的重放里补回。
///
/// 它不再持有子附魔、不再转发任何效果，也永远不会被当成正常附魔施加（CanEnchant = false）。
/// </summary>
public sealed class CompositeEnchantment : EnchantmentModel
{
    /// <summary>旧壳没有附魔卡面附加文本（子附魔信息已不存于存档）。</summary>
    public override bool HasExtraCardText => false;

    /// <summary>禁止新施加：它只是旧存档的墓碑，不是可用附魔。</summary>
    public override bool CanEnchant(CardModel card) => false;
}
