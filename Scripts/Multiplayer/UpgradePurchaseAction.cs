using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem.Multiplayer;

/// <summary>通用升级数据操作类型（v1.4.3 合并，减少 INetAction 类型以稳定 net-id）。</summary>
public enum UpgradeDataOp
{
    Purchase,
    AddPoints,
    SpendPoints,
    CardMod,
}

/// <summary>
/// 通用数据变更 INetAction（v1.4.3）：购买/加点/扣点/卡牌修改统一走此类型。
/// 类型被 ActionTypes 通过 GetSubtypesInMods&lt;INetAction&gt; 自动发现；mod 仅此一个
/// INetAction 类型 → 两端类型集合稳定，新增操作不改变 net-id。
/// </summary>
public struct NetUpgradeDataAction : INetAction, IPacketSerializable
{
    public int Op;                    // UpgradeDataOp
    public string Key = "";           // 购买 key / CardMod 的 keyword/enchant 名
    public int Cost;                  // 购买/卡牌 cost
    public int Amount;                // 加点/扣点金额
    public bool Flag1;                // 购买 IsSkill
    public bool Flag2;                // 购买 IsImmediate / CardMod add
    public string CardIdentity = "";  // CardMod 卡牌身份 "TemplateId__实例序号"
    public int ModType;               // CardMod 的 CardUpgradeTracker.ModType

    public NetUpgradeDataAction() { }

    public GameAction ToGameAction(Player player) =>
        new UpgradeDataAction(player, Op, Key, Cost, Amount, Flag1, Flag2, CardIdentity, ModType);

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Op);
        writer.WriteString(Key);
        writer.WriteInt(Cost);
        writer.WriteInt(Amount);
        writer.WriteBool(Flag1);
        writer.WriteBool(Flag2);
        writer.WriteString(CardIdentity);
        writer.WriteInt(ModType);
    }

    public void Deserialize(PacketReader reader)
    {
        Op = reader.ReadInt();
        Key = reader.ReadString() ?? "";
        Cost = reader.ReadInt();
        Amount = reader.ReadInt();
        Flag1 = reader.ReadBool();
        Flag2 = reader.ReadBool();
        CardIdentity = reader.ReadString() ?? "";
        ModType = reader.ReadInt();
    }

    public override string ToString() => $"NetUpgradeDataAction op={Op} key={Key} id={CardIdentity}";
}

/// <summary>
/// 通用数据变更 GameAction：两端在同一 action id 上执行同一操作（购买/加点/扣点/卡牌修改）。
/// ExecuteAction 内做权威校验（点数不足等则 no-op，两端结论一致）。
/// </summary>
public sealed class UpgradeDataAction : GameAction
{
    private readonly Player _player;
    private readonly UpgradeDataOp _op;
    private readonly string _key;
    private readonly int _cost;
    private readonly int _amount;
    private readonly bool _flag1;
    private readonly bool _flag2;
    private readonly string _cardIdentity;
    private readonly int _modType;

    /// <summary>本次操作是否成功。</summary>
    public bool Succeeded { get; private set; }

    public override ulong OwnerId => _player.NetId;
    public override GameActionType ActionType => GameActionType.Any; // 战斗/非战斗都可入队

    public UpgradeDataAction(Player player, int op, string key, int cost, int amount,
        bool flag1, bool flag2, string cardIdentity, int modType)
    {
        _player = player;
        _op = (UpgradeDataOp)op;
        _key = key;
        _cost = cost;
        _amount = amount;
        _flag1 = flag1;
        _flag2 = flag2;
        _cardIdentity = cardIdentity;
        _modType = modType;
    }

    public override async Task ExecuteAction()
    {
        switch (_op)
        {
            case UpgradeDataOp.Purchase:
                if (_flag1)
                {
                    Succeeded = SkillRegistry.TryPurchase(_player, _key, _cost);
                }
                else
                {
                    Succeeded = AbilityOperationHelper.TryPurchase(_player, _key, _cost);
                    if (Succeeded && _flag2)
                        await AbilityOperationHelper.ApplyImmediate(_player, _key);
                }
                break;
            case UpgradeDataOp.AddPoints:
                UpgradePointManager.AddPoints(_player, _amount);
                Succeeded = true;
                break;
            case UpgradeDataOp.SpendPoints:
                Succeeded = UpgradePointManager.TrySpendPoints(_player, _amount);
                break;
            case UpgradeDataOp.CardMod:
                Succeeded = await CardOperationHelper.TryApplyCardMod(_player, _cardIdentity, _cost, (CardUpgradeTracker.ModType)_modType, _key, _flag2);
                break;
        }
        await Task.CompletedTask;
    }

    public override INetAction ToNetAction() => new NetUpgradeDataAction
    {
        Op = (int)_op,
        Key = _key,
        Cost = _cost,
        Amount = _amount,
        Flag1 = _flag1,
        Flag2 = _flag2,
        CardIdentity = _cardIdentity,
        ModType = _modType,
    };

    public override string ToString() => $"UpgradeDataAction {_player.NetId} op={_op} key={_key} id={_cardIdentity}";
}

/// <summary>升级数据操作入口（UI 调用）。走同步 action，两端一致后返回是否成功。</summary>
public static class UpgradePurchaseFlow
{
    /// <summary>购买能力/技能（跨端同步）。</summary>
    public static Task<bool> EnqueuePurchase(string key, int cost, bool isSkill, bool isImmediate)
        => EnqueueLocal(UpgradeDataOp.Purchase, key, cost, 0, isSkill, isImmediate, "", 0);

    /// <summary>加点（跨端同步）。</summary>
    public static Task<bool> EnqueueAddPoints(int amount)
        => EnqueueLocal(UpgradeDataOp.AddPoints, "", 0, amount, false, false, "", 0);

    /// <summary>扣点（带校验，跨端同步）。</summary>
    public static Task<bool> EnqueueSpendPoints(int amount)
        => EnqueueLocal(UpgradeDataOp.SpendPoints, "", 0, amount, false, false, "", 0);

    /// <summary>卡牌修改（选卡后入队，原子扣点+改卡+记录，跨端同步）。</summary>
    public static Task<bool> EnqueueCardMod(int cost, string cardIdentity, int modType, string arg, bool add)
        => EnqueueLocal(UpgradeDataOp.CardMod, arg, cost, 0, false, add, cardIdentity, modType);

    private static async Task<bool> EnqueueLocal(UpgradeDataOp op, string key, int cost, int amount,
        bool flag1, bool flag2, string cardIdentity, int modType)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return false;
        var action = new UpgradeDataAction(player, (int)op, key, cost, amount, flag1, flag2, cardIdentity, modType);
        var sync = RunManager.Instance?.ActionQueueSynchronizer;
        if (sync == null) // 无 run / 同步器未就绪：本地执行
        {
            await action.ExecuteAction();
            return action.Succeeded;
        }
        sync.RequestEnqueue(action);
        await action.CompletionTask;
        return action.Succeeded;
    }
}
