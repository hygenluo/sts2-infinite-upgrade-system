using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
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
    ApplyEvery4, // v1.4.9：每4张+力量/敏捷，走 action 避免 fire-and-forget 与 checksum 时序竞争
    AddCard,     // UI v3：向牌组添加一张牌（Key = ModelId 字符串，两端添加同一张牌）
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
                if (!Succeeded)
                    Log.Warn($"IU CardMod FAILED on this peer — op={_modType} id={_cardIdentity} cost={_cost} " +
                             $"pts={UpgradeDataStore.GetPoints(_player)} arg={_key}. " +
                             "两端此处结果必须一致，否则会 checksum 分歧（请核对两端日志）。");
                break;
            case UpgradeDataOp.AddCard:
                // 加牌：选牌端把选中的 ModelId 传过来，两端添加**同一张**牌
                // （本地随机/选池结果只作为参数传输，不参与确定性模拟）。
                Succeeded = await CardOperationHelper.TryApplyAddCard(_player, _key);
                break;
            case UpgradeDataOp.ApplyEvery4:
                // 每4张+力量/敏捷：走 action 由 ActionExecutor 等待完成（checksum 在其后），避免
                // fire-and-forget async 施加与 checksum 的时序竞争导致两端落地时机不同。
                if (_player?.Creature != null)
                {
                    if (UpgradeDataStore.HasSkill(_player, "strength_every_4_plays"))
                        await AbilityOperationHelper.ApplyPower(_player.Creature, "strength", 1);
                    if (UpgradeDataStore.HasSkill(_player, "agility_every_4_plays"))
                        await AbilityOperationHelper.ApplyPower(_player.Creature, "dexterity", 1);
                }
                Succeeded = true;
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

    /// <summary>每4张+力量/敏捷（由 host 检测到 %4 时入队，owner=打牌玩家，跨端同步执行）。</summary>
    public static Task<bool> EnqueueApplyEvery4(Player owner)
        => EnqueueFor(owner, UpgradeDataOp.ApplyEvery4, "", 0, 0, false, false, "", 0);

    /// <summary>
    /// 向牌组添加一张牌（跨端同步）：调用端先选好牌，把 ModelId 字符串（"cards.STRIKE"）传过来，
    /// 两端各自用 `RunState.CreateCard` 创建并加入牌组 —— 结果一致。
    /// </summary>
    public static Task<bool> EnqueueAddCard(string modelIdString)
        => EnqueueLocal(UpgradeDataOp.AddCard, modelIdString, 0, 0, false, false, "", 0);

    private static Task<bool> EnqueueLocal(UpgradeDataOp op, string key, int cost, int amount,
        bool flag1, bool flag2, string cardIdentity, int modType)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return Task.FromResult(false);
        return EnqueueFor(player, op, key, cost, amount, flag1, flag2, cardIdentity, modType);
    }

    private static async Task<bool> EnqueueFor(Player owner, UpgradeDataOp op, string key, int cost, int amount,
        bool flag1, bool flag2, string cardIdentity, int modType)
    {
        var action = new UpgradeDataAction(owner, (int)op, key, cost, amount, flag1, flag2, cardIdentity, modType);
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
