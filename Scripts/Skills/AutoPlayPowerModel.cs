using System.Threading.Tasks;
using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 技能「每当你抽到能力牌时，自动打出」的官方范式实现（v1.4.12，参照地狱狂徒 HellraiserPower）：
/// 作为 CustomPowerModel，其 AfterCardDrawnEarly 在游戏抽牌管线内被 await → 多人两端确定性一致。
/// 隐藏 power（IsVisibleInternal=false，不显示图标），由 ApplySkillCombatStartPowers 在战斗开始
/// 给已购技能（auto_play_power_on_draw）的玩家施加。替代旧的 fire-and-forget Harmony patch
/// （SkillAutoPlayPowerPatch：async 施加与 checksum 时序竞争导致分歧）。
/// </summary>
public sealed class AutoPlayPowerModel : CustomPowerModel
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool IsVisibleInternal => false;

    public override async Task AfterCardDrawnEarly(PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        if (card?.Owner?.Creature == Owner && card.Type == CardType.Power)
            await CardCmd.AutoPlay(choiceContext, card, null);
    }
}
