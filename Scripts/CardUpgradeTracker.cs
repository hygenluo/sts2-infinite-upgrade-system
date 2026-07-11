using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 卡牌升级追踪器 — 将升级次数持久化到 JSON，读档后重新应用。
///
/// 为什么不用游戏自带的 CurrentUpgradeLevel：
/// CardModel.FromSerializable 在恢复时会先设置 CurrentUpgradeLevel，再循环调用
/// UpgradeInternal()。如果 CurrentUpgradeLevel > MaxUpgradeLevel（默认 1），
/// 属性 setter 的校验会抛出异常。而且 UpgradeInternal() 内部又对 level++，
/// 导致升级次数每次都翻倍（level = N → set → upgrade × N → level = 2N）。
///
/// 因此我们用独立的 JSON 文件追踪每张卡牌的升级次数，
/// 读档后通过 CardOperationHelper.PerformInfiniteUpgrade 重新应用。
/// </summary>
public static class CardUpgradeTracker
{
    private const string FileName = "card_upgrades.json";

    /// <summary>卡牌 Id.Entry → 升级次数</summary>
    private static readonly Dictionary<string, int> s_upgrades = new();

    public static void RecordUpgrade(CardModel card)
    {
        var key = card.Id.Entry;
        s_upgrades.TryGetValue(key, out int count);
        s_upgrades[key] = count + 1;
        Save();
    }

    public static int GetUpgradeCount(CardModel card)
    {
        s_upgrades.TryGetValue(card.Id.Entry, out int count);
        return count;
    }

    /// <summary>读档后将累积的升级重新应用到牌组中所有匹配的卡牌。</summary>
    public static void ReapplyAllUpgrades(Player player)
    {
        if (s_upgrades.Count == 0) return;

        int applied = 0;
        foreach (var card in player.Deck.Cards)
        {
            if (s_upgrades.TryGetValue(card.Id.Entry, out int count) && count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    CardOperationHelper.UpgradeWithoutTracking(card);
                }
                applied++;
            }
        }
        Log.Info($"InfiniteUpgrade: reapplied upgrades to {applied} cards across {s_upgrades.Count} templates.");
    }

    public static void Save()
    {
        try
        {
            var path = GetFilePath();
            var json = JsonSerializer.Serialize(s_upgrades);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: failed to save card upgrades: {ex.Message}");
        }
    }

    public static void Load()
    {
        s_upgrades.Clear();
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (data != null)
            {
                foreach (var kv in data)
                    s_upgrades[kv.Key] = kv.Value;
                Log.Info($"InfiniteUpgrade: loaded {s_upgrades.Count} card upgrade templates.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: failed to load card upgrades: {ex.Message}");
        }
    }

    public static void Delete()
    {
        s_upgrades.Clear();
        try
        {
            var path = GetFilePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static string GetFilePath()
    {
        var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location);
        return Path.Combine(modDir ?? ".", FileName);
    }
}
