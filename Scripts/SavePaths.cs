using System;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 存档路径统一管理。
///
/// 存档必须放在游戏用户数据目录（user://），绝不能放在 mod 安装目录：
/// - 游戏会递归扫描 mod 目录下所有 .json 当候选 manifest（要求含 id 的 JSON 对象），
///   runs/*.json 会被误报"缺少 id 字段 / 不是预期的 JSON 对象"。
/// - mod 安装目录不稳定：Steam Workshop 校验/更新会重建目录，
///   本地 mods/ 目录每次构建部署时会被清空（csproj post-build RemoveDir）。
/// </summary>
public static class SavePaths
{
    private static string? s_runsDir;

    /// <summary>%APPDATA%\SlayTheSpire2\mod_data\InfiniteUpgradeSystem\runs</summary>
    public static string GetRunsDir()
    {
        if (s_runsDir != null) return s_runsDir;

        string userData;
        try
        {
            userData = OS.GetUserDataDir();
        }
        catch (Exception)
        {
            userData = "";
        }
        if (string.IsNullOrEmpty(userData))
            userData = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2");

        s_runsDir = Path.Combine(userData, "mod_data", Entry.ModId, "runs");
        return s_runsDir;
    }

    /// <summary>确保目录存在并返回 {prefix}_{seed}.json 的完整路径。</summary>
    public static string GetFilePath(string prefix, string seed)
    {
        var dir = GetRunsDir();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{prefix}_{seed}.json");
    }

    /// <summary>
    /// 把旧位置（mod 安装目录/runs）的存档迁移到新位置，并删除旧目录。
    /// 同时清掉 Workshop 目录里被 manifest 扫描器误报的垃圾 JSON。
    /// Entry.Init 时调用一次。
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location);
            if (string.IsNullOrEmpty(modDir)) return;
            var legacyDir = Path.Combine(modDir, "runs");
            if (!Directory.Exists(legacyDir)) return;

            var newDir = GetRunsDir();
            if (!Directory.Exists(newDir))
                Directory.CreateDirectory(newDir);

            int migrated = 0;
            bool allOk = true;
            foreach (var src in Directory.GetFiles(legacyDir, "*.json"))
            {
                var dest = Path.Combine(newDir, Path.GetFileName(src));
                try
                {
                    // 目标已存在时保留较新的那份
                    if (!File.Exists(dest) ||
                        File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest))
                        File.Copy(src, dest, overwrite: true);
                    File.Delete(src);
                    migrated++;
                }
                catch (Exception ex)
                {
                    allOk = false;
                    Log.Warn($"InfiniteUpgrade: migrate {Path.GetFileName(src)} failed: {ex.Message}");
                }
            }

            // 只有全部迁移成功才删目录，避免误删未迁移的存档
            if (allOk)
                Directory.Delete(legacyDir, recursive: true);
            Log.Info($"InfiniteUpgrade: migrated {migrated} save files from {legacyDir} to {newDir}");
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: legacy save migration failed: {ex.Message}");
        }
    }
}
