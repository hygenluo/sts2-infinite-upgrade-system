using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 卡牌升级追踪器 — 每 Run 独立文件，通过 RunState.Rng.StringSeed 区分。
/// </summary>
public static class CardUpgradeTracker
{
    private static readonly Dictionary<string, int> s_upgrades = new();

    private static string GetFilePath(string seed)
    {
        var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".";
        var dir = Path.Combine(modDir, "runs");
        return Path.Combine(dir, $"card_upgrades_{seed}.json");
    }

    public static void RecordUpgrade(CardModel card, string seed)
    {
        var key = card.Id.Entry;
        s_upgrades.TryGetValue(key, out int count);
        s_upgrades[key] = count + 1;
        Save(seed);
    }

    public static void ReapplyAllUpgrades(Player player)
    {
        if (s_upgrades.Count == 0) return;

        int applied = 0;
        foreach (var card in player.Deck.Cards)
        {
            if (s_upgrades.TryGetValue(card.Id.Entry, out int count) && count > 0)
            {
                for (int i = 0; i < count; i++)
                    CardOperationHelper.UpgradeWithoutTracking(card);
                applied++;
            }
        }
        Log.Info($"InfiniteUpgrade: reapplied upgrades to {applied} cards.");
    }

    public static void Save(string seed)
    {
        try
        {
            var path = GetFilePath(seed);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(s_upgrades));
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: failed to save card upgrades: {ex.Message}");
        }
    }

    public static void Load(string seed)
    {
        s_upgrades.Clear();
        try
        {
            var path = GetFilePath(seed);
            if (!File.Exists(path)) return;

            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path));
            if (data != null)
                foreach (var kv in data) s_upgrades[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: failed to load card upgrades: {ex.Message}");
        }
    }
}
