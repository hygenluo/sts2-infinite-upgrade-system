using System;
using System.IO;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 点数持久化 — 每个 Run 独立一个文件，通过 RunState.Rng.StringSeed 区分。
/// 新局 = 新 seed = 新文件（不存在） → 5 点起步
/// 读档 = 同 seed = 同文件（存在）  → 恢复保存的点数
/// 无需检测新局/读档，完全消除误判。
/// </summary>
public static class PointsPersistence
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    private static string GetFilePath(string seed) => SavePaths.GetFilePath("points", seed);

    public static void SavePoints(int points, string seed)
    {
        try
        {
            var path = GetFilePath(seed);

            var data = new PointsData { Points = points };
            File.WriteAllText(path, JsonSerializer.Serialize(data, s_jsonOptions));
            Log.Info($"InfiniteUpgrade: SAVED {points} pts → seed={seed}");
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: FAILED to save points: {ex.GetType().Name} — {ex.Message}");
        }
    }

    public static int LoadPoints(string seed)
    {
        try
        {
            var path = GetFilePath(seed);
            if (!File.Exists(path))
            {
                Log.Info($"InfiniteUpgrade: no file for seed={seed}, defaulting to 5.");
                return 5;
            }

            var data = JsonSerializer.Deserialize<PointsData>(File.ReadAllText(path), s_jsonOptions);
            if (data != null)
            {
                Log.Info($"InfiniteUpgrade: LOADED {data.Points} pts for seed={seed}.");
                return data.Points;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: FAILED to load points: {ex.GetType().Name} — {ex.Message}");
        }

        return 5;
    }

    private class PointsData
    {
        public int Points { get; set; } = 5;
    }
}
