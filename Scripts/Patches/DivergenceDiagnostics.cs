using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Checksums;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem.Patches;

/// <summary>
/// 分歧状态转储（v1.4.8 排查工具）：host 检测到 checksum 不匹配 / client 收到分歧消息时，
/// 打印本端各玩家的 powers / 点数 / 技能 / 计数，供两端日志对比精确定位。
/// </summary>
public static class DivergenceDiagnostics
{
    [HarmonyPatch(typeof(ChecksumTracker), nameof(ChecksumTracker.CompareChecksums))]
    public static class HostPatch
    {
        static void Postfix(ChecksumTracker.TrackedChecksum localChecksum, NetChecksumData remoteChecksum, ulong remoteId)
        {
            if (localChecksum.data.checksum == remoteChecksum.checksum) return;
            Log.Info(Dump($"HOST divergence vs {remoteId} local={localChecksum.data.checksum} remote={remoteChecksum.checksum} ctx={localChecksum.context}"));
        }
    }

    [HarmonyPatch(typeof(ChecksumTracker), nameof(ChecksumTracker.OnReceivedStateDivergenceMessage))]
    public static class ClientPatch
    {
        static void Postfix(StateDivergenceMessage message)
        {
            Log.Info(Dump("CLIENT received divergence"));
        }
    }

    private static string Dump(string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"===== IU {header} =====");
        var state = RunManager.Instance?.State;
        if (state != null)
        {
            foreach (var player in state.Players)
            {
                if (player == null) continue;
                sb.Append($"player {player.NetId}:");
                if (player.Creature != null)
                {
                    foreach (var p in player.Creature.Powers)
                        sb.Append($" power[{p.Id.Entry}:{p.Amount}]");
                }
                var d = UpgradeDataStore.For(player);
                sb.Append($" pts={d.Points} cp={d.CombatPlayCount}");
                sb.Append($" skills=[{string.Join(",", d.Skills)}]");
                sb.AppendLine($" boosts=[{string.Join(",", d.Boosts)}]");
            }
        }
        else
        {
            sb.AppendLine("(no run state)");
        }
        return sb.ToString();
    }
}
