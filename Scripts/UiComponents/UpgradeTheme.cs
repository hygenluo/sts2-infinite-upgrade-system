using System;
using System.IO;
using System.Reflection;
using Godot;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// UI v2 主题：色板 + 字体（贴 STS2 原版质感：暗色羊皮纸底 + 金色描边）。
///
/// 字体：思源宋体 SC（Source Han Serif SC，Adobe 官方构建，OFL 协议），
/// 随 mod 打包在 resources/fonts/ 目录，与 DLL 一起部署 —— 加载路径基于
/// Assembly.Location，因此无论 mod 从 mods/、workshop 目录还是其他位置加载
/// 都能找到字体。加载失败时回退 Godot 默认字体（不阻断 UI）。
/// </summary>
public static class UpgradeTheme
{
    // ═══════════════════════════════════════════════════════════════
    // 色板（UI 设计.md §Phase 0 基建）
    // ═══════════════════════════════════════════════════════════════

    public static readonly Color PanelBg = new("1e1b16");        // 面板底：暗羊皮纸褐
    public static readonly Color PanelBgAlt = new("262019");     // 分区头/次级底色
    public static readonly Color PanelBorder = new("c9a45c");    // 金边
    public static readonly Color TitleGold = new("e8c97a");      // 标题金
    public static readonly Color TextMain = new("e8e2d4");       // 正文
    public static readonly Color TextSecondary = new("9a927f");  // 次级文字
    public static readonly Color CostColor = new("a9a193");      // 成本小字
    public static readonly Color Danger = new("d46a5a");         // 危险红（点数不足）
    public static readonly Color HoverGlow = new("f0d48a");      // 悬停发光金

    // ═══════════════════════════════════════════════════════════════
    // 字体
    // ═══════════════════════════════════════════════════════════════

    private static FontFile? s_regular;
    private static FontFile? s_semibold;
    private static bool s_fontLoaded;

    /// <summary>正文用字（Regular，wght=400）。加载失败时回退默认字体。</summary>
    public static Font Regular => GetFont(400) ?? ThemeDB.FallbackFont;

    /// <summary>标题用字（SemiBold，wght=600）。加载失败时回退默认字体。</summary>
    public static Font SemiBold => GetFont(600) ?? ThemeDB.FallbackFont;

    private static Font? GetFont(int weight)
    {
        LoadFontsOnce();
        return weight >= 600 ? s_semibold : s_regular;
    }

    /// <summary>从 DLL 同目录 resources/fonts/ 加载字体，结果缓存；失败回退（仅报错一次）。</summary>
    private static void LoadFontsOnce()
    {
        if (s_fontLoaded) return;
        s_fontLoaded = true;
        try
        {
            var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dllDir == null) return;
            var fontsDir = Path.Combine(dllDir, "resources", "fonts");
            var regularPath = Path.Combine(fontsDir, RegularFile);
            var semiboldPath = Path.Combine(fontsDir, SemiBoldFile);
            if (!File.Exists(regularPath) || !File.Exists(semiboldPath))
            {
                GD.PrintErr($"[InfiniteUpgrade] 字体未找到: {fontsDir} — 回退默认字体。");
                return;
            }

            s_regular = LoadFont(regularPath);
            s_semibold = LoadFont(semiboldPath);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] 字体加载异常: {ex.Message} — 回退默认字体。");
            s_regular?.Dispose();
            s_semibold?.Dispose();
            s_regular = null;
            s_semibold = null;
        }
    }

    private const string RegularFile = "SourceHanSerifSC-Regular.otf";
    private const string SemiBoldFile = "SourceHanSerifSC-SemiBold.otf";

    private static FontFile? LoadFont(string path)
    {
        var font = new FontFile();
        if (font.LoadDynamicFont(path) != Error.Ok)
        {
            font.Dispose();
            return null;
        }
        // 2K 下游戏以 canvas_items 内容缩放渲染（1920 基准 → 2K=1.333×，finalScale 已实测确认），
        // 普通动态字体的字形贴图按画布尺寸栅格化后，再被画布变换双线性放大 → 文字发糊。
        // 启用 MSDF（有符号距离场）：字形以固定尺寸栅格化，任意缩放均锐利。
        // 注意：MSDF 下 Hinting 不生效；必须使用官方 Adobe 构建（Google Fonts 转换版
        // 存在重叠字形，MSDF 渲染会出现噪音）。变量字体在 MSDF 下也有兼容问题，
        // 因此使用官方静态 OTF（Regular + SemiBold 两个文件）。
        font.MultichannelSignedDistanceField = true;
        font.MsdfSize = 48;
        font.MsdfPixelRange = 16;
        // 以下参数仅在 MSDF 关闭时生效（保留作为回退路径）
        font.Antialiasing = TextServer.FontAntialiasing.Lcd;
        font.Hinting = TextServer.Hinting.Light;
        font.SubpixelPositioning = TextServer.SubpixelPositioning.OneQuarter;
        return font;
    }

    /// <summary>
    /// 构建面板 StyleBoxFlat（羊皮纸底 + 金边 + 圆角）。
    /// </summary>
    public static StyleBoxFlat BuildPanelStylebox()
    {
        var sb = new StyleBoxFlat();
        sb.BgColor = PanelBg;
        sb.BorderColor = PanelBorder;
        sb.SetBorderWidthAll(2);
        sb.SetCornerRadiusAll(8);
        sb.SetContentMarginAll(12);
        return sb;
    }
}
