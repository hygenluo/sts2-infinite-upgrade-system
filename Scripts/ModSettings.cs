using System;
using Godot;
using InfiniteUpgradeSystem.UiComponents;
using MegaCrit.Sts2.Core.Logging;
using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 模组配置模型（RitsuLib `ModDataStore` 持久化的根对象，一个存档一份 JSON）。
///
/// 注意：**不要用 float.NaN 表示「未保存」** —— System.Text.Json 默认拒绝序列化
/// NaN/Infinity（需要 `JsonNumberHandling.AllowNamedFloatingPointLiterals`），
/// 一旦写盘就会抛异常。这里改用 <see cref="HasFloatingPosition"/> 显式标记。
/// </summary>
public sealed class UpgradeSettings
{
    /// <summary>局内是否显示悬浮窗（默认开）。</summary>
    public bool ShowFloatingWindow { get; set; } = true;

    /// <summary>是否记录过悬浮窗位置（false = 用默认位：右侧边缘垂直居中）。</summary>
    public bool HasFloatingPosition { get; set; }

    /// <summary>悬浮窗左上角 X（<see cref="HasFloatingPosition"/> 为 true 时有效）。</summary>
    public float FloatingX { get; set; }

    /// <summary>悬浮窗左上角 Y。</summary>
    public float FloatingY { get; set; }
}

/// <summary>
/// 模组配置读写 + 设置页注册（UI设计.md §0.8）。
///
/// 组成：
/// 1. **数据槽**：`ModDataStore.Register&lt;UpgradeSettings&gt;`，`SaveScope.Profile`
///    → 配置**按存档独立**保存（用户选定），文件 `settings.json`。
/// 2. **设置页**：`RitsuLibFramework.RegisterModSettings` 注册到游戏内「模组设置」，
///    文本走 `ModSettingsText.Dynamic` + `UpgradeLoc` 复用本模组自加载的双语本地化
///    （`has_pck: false` 下游戏 LocString 表拿不到本模组的 key，见 DEBUG.md 陷阱 5）。
/// 3. **值缓存**：UI 侧不直接 `Read()`（每帧读会反复查 store），只读静态缓存
///    <see cref="ShowFloatingWindow"/>；缓存由 <see cref="Refresh"/> 刷新：
///    写入事件（即时）+ 悬浮窗每 20 帧轮询一次（兜底，防止事件早于档案初始化而丢失）。
/// </summary>
public static class ModSettings
{
    /// <summary>数据槽 key（同时是设置页绑定的 dataKey）。</summary>
    private const string DataKey = "settings";

    /// <summary>配置文件名（按 Profile 作用域归档）。</summary>
    private const string FileName = "settings.json";

    /// <summary>模组显示名兜底（本地化缺失时用）。</summary>
    private const string FallbackName = "无限升级系统";

    // ── 绑定（null = RitsuLib 缺失 / 注册失败 → 全部回退默认值）──

    private static ModSettingsValueBinding<UpgradeSettings, bool>? s_showFloating;
    private static ModSettingsValueBinding<UpgradeSettings, bool>? s_hasPosition;
    private static ModSettingsValueBinding<UpgradeSettings, float>? s_positionX;
    private static ModSettingsValueBinding<UpgradeSettings, float>? s_positionY;

    private static bool s_ready;
    private static bool s_readErrorLogged;

    // ── 值缓存（UI 只读这里）──

    private static bool s_showFloatingWindow = true;
    private static bool s_hasFloatingPosition;
    private static Vector2 s_floatingPosition;

    /// <summary>局内是否显示悬浮窗（默认 true；配置读取失败时保持默认）。</summary>
    public static bool ShowFloatingWindow => s_showFloatingWindow;

    // ═══════════════════════════════════════════════════════════════
    // 注册（Entry.Init 中调用，早于 UI 创建）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>注册数据槽 + 设置页，并读一次初始值。失败时记日志并保持默认值，不阻断模组启动。</summary>
    public static void Register()
    {
        if (s_ready) return;

        try
        {
            // ModDataStore 注册作用域：把条目的立即初始化推迟到作用域结束
            // （与 UpgradeDataStore 一致，见 CODE_GUIDE.md「启动流程」）
            using (RitsuLibFramework.BeginModDataRegistration(Entry.ModId))
            {
                ModDataStore.For(Entry.ModId).Register(
                    key: DataKey,
                    fileName: FileName,
                    scope: SaveScope.Profile, // 按存档独立
                    defaultFactory: () => new UpgradeSettings(),
                    autoCreateIfMissing: true);
            }

            s_showFloating = Binding<bool>(static s => s.ShowFloatingWindow,
                static (s, v) => s.ShowFloatingWindow = v);
            s_hasPosition = Binding<bool>(static s => s.HasFloatingPosition,
                static (s, v) => s.HasFloatingPosition = v);
            s_positionX = Binding<float>(static s => s.FloatingX, static (s, v) => s.FloatingX = v);
            s_positionY = Binding<float>(static s => s.FloatingY, static (s, v) => s.FloatingY = v);

            s_ready = true;
            Refresh();
            Log.Info($"InfiniteUpgradeSystem: ModSettings registered (profile scope, showFloating={s_showFloatingWindow}).");
        }
        catch (Exception ex)
        {
            s_ready = false;
            Log.Error($"InfiniteUpgradeSystem: ModSettings 注册失败（RitsuLib 缺失？）: {ex.Message}");
            return;
        }

        RegisterPage();
    }

    /// <summary>创建一条指向配置模型的绑定（与设置页控件共用同一个 dataKey）。</summary>
    private static ModSettingsValueBinding<UpgradeSettings, T> Binding<T>(
        Func<UpgradeSettings, T> getter, Action<UpgradeSettings, T> setter) =>
        new(Entry.ModId, DataKey, SaveScope.Profile, getter, setter);

    /// <summary>注册「模组设置 → 无限升级系统」页面。</summary>
    private static void RegisterPage()
    {
        try
        {
            RitsuLibFramework.RegisterModSettings(Entry.ModId, page => page
                .WithTitle(Text(UpgradeLoc.UiTitle, FallbackName))
                .WithModDisplayName(Text(UpgradeLoc.UiTitle, FallbackName))
                // 主菜单 / 局内暂停 / 战斗中暂停都能进（改完返回游戏即生效）
                .WithVisibleOnHostSurfaces(ModSettingsHostSurface.All)
                .AddSection("general", section => section
                    .WithTitle(Text(UpgradeLoc.SettingsSectionGeneral, "通用"))
                    .AddToggle(
                        "show_floating_window",
                        Text(UpgradeLoc.SettingsShowFloating, "显示悬浮窗"),
                        s_showFloating!,
                        Text(UpgradeLoc.SettingsShowFloatingDesc,
                            "局内在屏幕右侧显示可拖拽的悬浮窗，点击可打开无限升级系统面板。"
                            + "关闭后仍可按 P 打开。"))));
            Log.Info("InfiniteUpgradeSystem: ModSettings page registered.");
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgradeSystem: 设置页注册失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 设置页文本：`Dynamic` 每次解析时现算，跟随游戏语言即时切换
    /// （不能只用 `Literal`，否则语言切换后设置页还是旧语言）。
    /// </summary>
    private static ModSettingsText Text(string key, string fallback) =>
        ModSettingsText.Dynamic(() => UpgradeLoc.Get(key, fallback));

    // ═══════════════════════════════════════════════════════════════
    // 读
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 store 重新读取配置到静态缓存。
    /// 档案（profile）尚未初始化时 `Read()` 可能抛异常 —— 此时保持上一次的值，
    /// 且只记一次日志（否则主菜单期间会每 20 帧刷一条）。
    /// 由写入事件即时调用，另由悬浮窗每 20 帧兜底轮询。
    /// </summary>
    public static void Refresh()
    {
        if (!s_ready) return;
        try
        {
            s_showFloatingWindow = s_showFloating!.Read();

            bool has = s_hasPosition!.Read();
            if (has)
                s_floatingPosition = new Vector2(s_positionX!.Read(), s_positionY!.Read());
            s_hasFloatingPosition = has;

            s_readErrorLogged = false;
        }
        catch (Exception ex)
        {
            if (s_readErrorLogged) return;
            s_readErrorLogged = true;
            Log.Warn($"InfiniteUpgradeSystem: 配置读取失败（档案可能尚未初始化）: {ex.Message}");
        }
    }

    /// <summary>取已保存的悬浮窗位置（无记录 / 读取失败时返回 false → 调用方用默认位）。</summary>
    public static bool TryGetFloatingPosition(out Vector2 position)
    {
        position = s_floatingPosition;
        return s_ready && s_hasFloatingPosition;
    }

    // ═══════════════════════════════════════════════════════════════
    // 写
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 保存悬浮窗位置（拖拽结束时调用）。
    /// 三个字段共用同一个 dataKey → 只需刷新一次缓存、`Save()` 一次即可整份落盘。
    /// </summary>
    public static void SaveFloatingPosition(Vector2 position)
    {
        s_floatingPosition = position;
        s_hasFloatingPosition = true;
        if (!s_ready) return;

        try
        {
            s_positionX!.Write(position.X);
            s_positionY!.Write(position.Y);
            s_hasPosition!.Write(true);
            s_positionX.Save(); // dataKey 级保存 → 整份 settings.json
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgradeSystem: 悬浮窗位置保存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 订阅设置页写入事件：玩家一拨开关就把新值同步到缓存（无需等待下一次轮询）。
    /// 锚点用 `SceneTree.Root` —— 它永不退出场景树，等于一个进程级订阅，没有生命周期风险。
    /// </summary>
    public static void SubscribeWriteEvents()
    {
        if (!s_ready) return;

        var root = ((SceneTree?)Engine.GetMainLoop())?.Root;
        if (root == null)
        {
            Log.Warn("InfiniteUpgradeSystem: SceneTree.Root 不可用，配置写入事件未订阅（轮询仍会生效）。");
            return;
        }

        try
        {
            ModSettingsBindingWriteEvents.SubscribeValueWrittenWhileNodeAlive(root, binding =>
            {
                if (binding.ModId == Entry.ModId && binding.DataKey == DataKey) Refresh();
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgradeSystem: 配置写入事件订阅失败（轮询仍会生效）: {ex.Message}");
        }
    }
}
