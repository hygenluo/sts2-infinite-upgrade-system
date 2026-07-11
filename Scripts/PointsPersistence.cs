using System;
using System.IO;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 点数持久化 — 通过 JSON 文件在存档/读档间保存点数。
///
/// 背景：游戏退出重启后所有 static 变量归零。RunStarted 事件在新局 AND 读档时都会触发
/// （NGame.LoadRun 和 NGame.StartRun 都调用了 RunManager.Launch()），因此需要：
/// 1. 新局 → 重置为 5
/// 2. 读档 → 从文件恢复
///
/// 数据存储位置：游戏存档目录下的 infinite_upgrade_points.json
/// </summary>
public static class PointsPersistence
{
    private const string FileName = "infinite_upgrade_points.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
    };

    private static string GetFilePath()
    {
        try
        {
            return SaveManager.Instance.GetProfileScopedPath(FileName);
        }
        catch
        {
            // Fallback: 模组目录
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "mods", "InfiniteUpgradeSystem", FileName);
        }
    }

    public static void SavePoints(int points)
    {
        try
        {
            var data = new PointsData { Points = points, SavedAt = DateTime.UtcNow.Ticks };
            var json = JsonSerializer.Serialize(data, s_jsonOptions);
            File.WriteAllText(GetFilePath(), json);
            Log.Info($"InfiniteUpgrade: saved {points} points to disk.");
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: failed to save points: {ex.Message}");
        }
    }

    public static int LoadPoints()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
            {
                Log.Info("InfiniteUpgrade: no saved points file, defaulting to 5.");
                return 5;
            }

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<PointsData>(json, s_jsonOptions);
            if (data != null)
            {
                Log.Info($"InfiniteUpgrade: loaded {data.Points} points from disk.");
                return data.Points;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: failed to load points: {ex.Message}");
        }

        return 5;
    }

    public static void DeleteSavedPoints()
    {
        try
        {
            var path = GetFilePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: failed to delete points file: {ex.Message}");
        }
    }

    private class PointsData
    {
        public int Points { get; set; } = 5;
        public long SavedAt { get; set; }
    }
}
