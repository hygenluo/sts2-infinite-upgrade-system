using System;
using System.IO;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 点数持久化 — 通过 JSON 文件在存档/读档间保存点数。
/// 文件存储在 mod DLL 所在目录（mods/InfiniteUpgradeSystem/），保证可读写。
/// </summary>
public static class PointsPersistence
{
    private const string FileName = "points.json";

    private static string? s_cachedPath;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    private static string GetFilePath()
    {
        if (s_cachedPath != null) return s_cachedPath;

        // 使用 mod DLL 所在目录，保证存在且可写
        var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location);
        if (!string.IsNullOrEmpty(modDir))
        {
            s_cachedPath = Path.Combine(modDir, FileName);
            Log.Info($"InfiniteUpgrade: persistence path = {s_cachedPath}");
            return s_cachedPath;
        }

        // 极端情况 fallback
        s_cachedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfiniteUpgradeSystem", FileName);
        Log.Warn($"InfiniteUpgrade: using fallback persistence path = {s_cachedPath}");
        return s_cachedPath;
    }

    public static void SavePoints(int points)
    {
        try
        {
            var data = new PointsData { Points = points };
            var json = JsonSerializer.Serialize(data, s_jsonOptions);
            var path = GetFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, json);
            Log.Info($"InfiniteUpgrade: SAVED {points} pts → {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: FAILED to save points: {ex.GetType().Name} — {ex.Message}");
        }
    }

    public static int LoadPoints()
    {
        try
        {
            var path = GetFilePath();
            Log.Info($"InfiniteUpgrade: attempting to load from {path} (exists={File.Exists(path)})");

            if (!File.Exists(path))
            {
                Log.Info("InfiniteUpgrade: no saved points file, return 5.");
                return 5;
            }

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<PointsData>(json, s_jsonOptions);
            if (data != null)
            {
                Log.Info($"InfiniteUpgrade: LOADED {data.Points} pts from disk.");
                return data.Points;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: FAILED to load points: {ex.GetType().Name} — {ex.Message}");
        }

        return 5;
    }

    public static void DeleteSavedPoints()
    {
        try
        {
            var path = GetFilePath();
            if (File.Exists(path))
            {
                File.Delete(path);
                Log.Info("InfiniteUpgrade: deleted saved points file.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: failed to delete points file: {ex.Message}");
        }
    }

    private class PointsData
    {
        public int Points { get; set; } = 5;
    }
}
