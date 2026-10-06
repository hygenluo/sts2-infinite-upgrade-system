using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace InfiniteUpgradeSystem;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public const string ModId = "InfiniteUpgradeSystem";

    public static void Init()
    {
        // 迁移旧存档（mod 目录/runs → user:// 数据目录），并清掉 mod 目录里的残留 JSON
        SavePaths.MigrateLegacy();

        var harmony = new Harmony(ModId);
        harmony.PatchAll(typeof(Entry).Assembly);
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);

        // 注册 RitsuLib 每玩家数据槽位（须在 RunStateHook.Subscribe 前）
        UpgradeDataStore.Register();

        // 模组配置（RitsuLib 设置页 + ModDataStore，按存档独立保存）
        ModSettings.Register();
        ModSettings.SubscribeWriteEvents();

        // 订阅游戏生命周期事件（直接 C# 事件，非 Harmony）
        RunStateHook.Subscribe();

        // Create the UI overlay and attach it to the scene tree
        UpgradeUIHandler.CreateInstance();

        // 可拖拽悬浮窗（HUD 常驻入口，点击唤出加点面板；可由模组配置关闭）
        InfiniteUpgradeSystem.UiComponents.UpgradeFloatingButton.CreateInstance();

        Log.Info("InfiniteUpgradeSystem mod initialized. BUILD=v57-20261007-modsettings");
    }
}
