using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem.Multiplayer;

/// <summary>
/// 购买 INetAction：客户端 → host 仲裁 → 广播 → 两端在同一 action id 执行同一购买。
/// 类型被 ActionTypes 通过 GetSubtypesInMods&lt;INetAction&gt; 自动发现，两端 mod 集一致则 net-id 一致。
/// </summary>
public struct NetUpgradePurchaseAction : INetAction, IPacketSerializable
{
    public string Key = "";
    public int Cost;
    public bool IsSkill;
    public bool IsImmediate;

    public NetUpgradePurchaseAction() { }

    public GameAction ToGameAction(Player player) => new UpgradePurchaseAction(player, Key, Cost, IsSkill, IsImmediate);

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(Key);
        writer.WriteInt(Cost);
        writer.WriteBool(IsSkill);
        writer.WriteBool(IsImmediate);
    }

    public void Deserialize(PacketReader reader)
    {
        Key = reader.ReadString() ?? "";
        Cost = reader.ReadInt();
        IsSkill = reader.ReadBool();
        IsImmediate = reader.ReadBool();
    }

    public override string ToString() => $"NetUpgradePurchaseAction {Key} x{IsSkill}";
}

/// <summary>
/// 购买 GameAction（方案 B）：两端在同一 action id 上执行同一购买。
/// ExecuteAction 内做权威校验（点数不足则 no-op，两端结论一致）并写 store + 即时效果。
/// </summary>
public sealed class UpgradePurchaseAction : GameAction
{
    private readonly Player _player;
    private readonly string _key;
    private readonly int _cost;
    private readonly bool _isSkill;
    private readonly bool _isImmediate;

    /// <summary>本次购买是否成功（点数足够 + 非满级）。</summary>
    public bool Succeeded { get; private set; }

    public override ulong OwnerId => _player.NetId;
    public override GameActionType ActionType => GameActionType.Any; // 战斗/非战斗都可入队，战斗结束不被取消

    public UpgradePurchaseAction(Player player, string key, int cost, bool isSkill, bool isImmediate)
    {
        _player = player;
        _key = key;
        _cost = cost;
        _isSkill = isSkill;
        _isImmediate = isImmediate;
    }

    public override async Task ExecuteAction()
    {
        if (_isSkill)
        {
            Succeeded = SkillRegistry.TryPurchase(_player, _key, _cost);
        }
        else
        {
            Succeeded = AbilityOperationHelper.TryPurchase(_player, _key, _cost);
            if (Succeeded && _isImmediate)
                await AbilityOperationHelper.ApplyImmediate(_player, _key);
        }
        await Task.CompletedTask;
    }

    public override INetAction ToNetAction() => new NetUpgradePurchaseAction
    {
        Key = _key,
        Cost = _cost,
        IsSkill = _isSkill,
        IsImmediate = _isImmediate,
    };

    public override string ToString() => $"UpgradePurchaseAction {_player.NetId} {_key}";
}

/// <summary>购买入口（UI 调用）。走同步 action，两端一致后返回是否成功。</summary>
public static class UpgradePurchaseFlow
{
    /// <summary>入队购买 action 并等待完成。await 在调用上下文（主线程）继续，UI 刷新安全。</summary>
    public static async Task<bool> EnqueuePurchase(string key, int cost, bool isSkill, bool isImmediate)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return false;
        var sync = RunManager.Instance?.ActionQueueSynchronizer;
        if (sync == null) // 无 run / 同步器未就绪：回退本地购买
            return isSkill
                ? SkillRegistry.TryPurchase(player, key, cost)
                : AbilityOperationHelper.TryPurchase(player, key, cost);
        var action = new UpgradePurchaseAction(player, key, cost, isSkill, isImmediate);
        sync.RequestEnqueue(action);
        await action.CompletionTask;
        return action.Succeeded;
    }
}
