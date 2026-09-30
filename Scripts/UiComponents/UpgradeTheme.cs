using System;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.addons.mega_text;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// UI v3 主题：**游戏原生观感**。
///
/// 与 v2 的关键差异（详见 DEBUG.md「UI v3：MegaLabel + 游戏原生字体」）：
/// 1. **字体不再自备**：v2 打包了 49MB 思源宋体 OTF（MSDF 手动加载）。v3 直接使用游戏自带字体资源
///    —— 非 CJK 语言用 `res://themes/kreon_regular_shared.tres`（游戏主 UI 字体），
///    CJK/RUS/THA 语言用 `FontManager.GetSubstituteFont()`（游戏自己的语言替换表，
///    zhs = Noto Sans Mono CJK SC / 思源宋体），完全跟随游戏语言切换。
/// 2. **文本控件统一走游戏自带的 `MegaLabel` / `MegaRichTextLabel`**（`MegaCrit.Sts2.addons.mega_text`）：
///    - 自动语言字体替换（`RefreshFont` → `ApplyLocaleFontSubstitution`）
///    - 自动字号适配（`AutoSizeEnabled` + `Min/MaxFontSize`，游戏原生排版行为）
///    - 文本色统一走 `font_color` 主题项
///    **注意**：MegaLabel._Ready 会断言 `font` 主题覆盖存在（否则抛异常），
///    因此所有文本节点必须在**加入场景树之前**通过本类工厂创建/设置字体覆盖。
/// 3. **配色取自游戏自身**：`StsColors`（gold #EFC851 / cream #FFF6E2 / red #FF5555 …）
///    + 游戏 StyleBoxFlat 实测值（控件底色 #363D4A、面板黑 25% + 1px 白描边、圆角 5~6）。
///
/// 改风格只需改本文件的色板常量与工厂默认值。
/// </summary>
public static class UpgradeTheme
{
    // ═══════════════════════════════════════════════════════════════
    // 色板（游戏原生）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>全屏遮罩（游戏 screenBackdrop 为黑 80%，此处略轻以免压暗游戏背景）。</summary>
    public static readonly Color Backdrop = new(0f, 0f, 0f, 0.72f);

    /// <summary>主面板底：冷灰蓝（游戏控件底色 #363D4A 的暗化版）。</summary>
    public static readonly Color PanelBg = new("20242b");

    /// <summary>顶栏 / 分区头底色（比面板底略亮一档）。</summary>
    public static readonly Color PanelBgAlt = new("272d37");

    /// <summary>条目行底色（斑马纹两档）。</summary>
    public static readonly Color RowBg = new("242932");
    public static readonly Color RowBgAlt = new("282e38");

    /// <summary>条目行悬停底色。</summary>
    public static readonly Color RowHover = new("333c49");

    /// <summary>游戏默认控件底色（Button / LineEdit normal，实测 #363D4A）。</summary>
    public static readonly Color Surface = new("363d4a");

    /// <summary>面板 / 分区描边。</summary>
    public static readonly Color PanelBorder = new("454e5c");

    /// <summary>强调金（StsColors.gold）。</summary>
    public static readonly Color Gold = new("efc851");

    /// <summary>暗金（金边弱化 / 次级金色文本）。</summary>
    public static readonly Color GoldDim = new("a98b3c");

    /// <summary>正文（StsColors.cream）。</summary>
    public static readonly Color TextMain = new("fff6e2");

    /// <summary>次级文字（说明、计数、提示）。</summary>
    public static readonly Color TextSecondary = new("a7b0bd");

    /// <summary>成本数字（弱化金）。</summary>
    public static readonly Color CostColor = new("d9c58a");

    /// <summary>危险红（点数不足，StsColors.red）。</summary>
    public static readonly Color Danger = new("ff5555");

    /// <summary>成功 / 已拥有（柔和绿）。</summary>
    public static readonly Color Good = new("8ed46a");

    /// <summary>悬停高亮（亮金）。</summary>
    public static readonly Color HoverGlow = new("ffe9a8");

    // ═══════════════════════════════════════════════════════════════
    // 字体（游戏自带资源，无自备字体文件）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>游戏主 UI 字体（非 CJK 语言）。</summary>
    private const string RegularPath = "res://themes/kreon_regular_shared.tres";
    private const string BoldPath = "res://themes/kreon_bold_shared.tres";
    private const string ItalicPath = "res://themes/bitter_medium_italic_glyph_space_one.tres";

    private static Font? s_regular;
    private static Font? s_bold;
    private static Font? s_italic;

    /// <summary>
    /// 本地化系统是否已就绪（`LocManager.Initialize()` 在 ModManager 加载模组**之后**执行，
    /// 因此模组初始化瞬间 Instance 可能为 null —— 语言替换字体此时不可用）。
    /// </summary>
    public static bool LocaleReady => TryGetLanguage() is { Length: > 0 };

    /// <summary>正文 / 标题字体（跟随游戏语言：zhs → 思源宋体 / Noto Sans Mono CJK）。</summary>
    public static Font FontRegular => Pick(FontType.Regular, RegularPath, ref s_regular);

    /// <summary>加粗字体（分区标题、数值、徽标）。</summary>
    public static Font FontBold => Pick(FontType.Bold, BoldPath, ref s_bold);

    /// <summary>斜体字体。</summary>
    public static Font FontItalic => Pick(FontType.Italic, ItalicPath, ref s_italic);

    private static string? TryGetLanguage()
    {
        try { return LocManager.Instance?.Language; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// 字体解析：语言替换字体优先（zhs/jpn/kor/rus/pol/tha → 游戏自带对应字体），
    /// 否则加载游戏主 UI 字体（Kreon），全部失败才回退 Godot 内置字体。
    ///
    /// 注意：本地化系统尚未就绪时**不缓存**结果（下次调用重试）—— 否则中文语言下会把
    /// Kreon（无 CJK 字形）当成最终字体缓存，中文全部显示为缺字方框。
    /// </summary>
    private static Font Pick(FontType type, string fallbackPath, ref Font? cache)
    {
        if (cache != null) return cache;

        var lang = TryGetLanguage();
        if (string.IsNullOrEmpty(lang))
            return LoadResource(fallbackPath) ?? ThemeDB.FallbackFont; // 不缓存，稍后重试

        Font? substitute = null;
        try { substitute = FontManager.GetSubstituteFont(lang, type); }
        catch (Exception ex) { GD.PrintErr($"[InfiniteUpgrade] 语言字体替换失败 ({lang}/{type}): {ex.Message}"); }

        cache = substitute ?? LoadResource(fallbackPath) ?? ThemeDB.FallbackFont;
        return cache;
    }

    private static Font? LoadResource(string path)
    {
        try
        {
            return ResourceLoader.Load<Font>(path, null, ResourceLoader.CacheMode.Reuse);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] 字体资源加载失败 {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 给非 MegaLabel 的游戏原生控件（Button / LineEdit）应用游戏字体与语言替换。
    /// 等价于游戏源码里 `this.ApplyLocaleFontSubstitution(FontType.Regular, "font")` 的用法
    /// （见 NMegaLineEdit / NSubMenuButton），失败不阻断 UI。
    /// </summary>
    public static void ApplyGameFont(Control control, bool bold = false, int fontSize = 0,
        StringName? fontThemeName = null)
    {
        var fontName = fontThemeName ?? "font";
        try
        {
            control.AddThemeFontOverride(fontName, bold ? FontBold : FontRegular);
            control.ApplyLocaleFontSubstitution(bold ? FontType.Bold : FontType.Regular, fontName);
        }
        catch (Exception)
        {
            // LocManager 未就绪等 → 至少保证有字体覆盖，不抛异常
            try { control.AddThemeFontOverride(fontName, bold ? FontBold : FontRegular); } catch { /* ignore */ }
        }

        if (fontSize > 0)
            control.AddThemeFontSizeOverride("font_size", fontSize);
    }

    // ═══════════════════════════════════════════════════════════════
    // 文本控件工厂（全部为游戏自带 MegaLabel / MegaRichTextLabel）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 固定字号文本（MegaLabel，AutoSize 关闭 → 严格按 fontSize 渲染）。
    /// 必须在加入场景树前调用本工厂（MegaLabel._Ready 断言字体覆盖存在）。
    /// </summary>
    public static MegaLabel Label(string text, int fontSize, Color color, bool bold = false,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new MegaLabel
        {
            Text = text,
            AutoSizeEnabled = false,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeFontOverride("font", bold ? FontBold : FontRegular);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>
    /// 自适应字号文本（MegaLabel，AutoSize 开启）：在 [min, max] 范围内自动适配控件矩形，
    /// 用于标题、按钮文字等宽度不定的文本（游戏原生排版行为）。
    /// </summary>
    public static MegaLabel AutoLabel(string text, int minFontSize, int maxFontSize, Color color,
        bool bold = true, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new MegaLabel
        {
            Text = text,
            AutoSizeEnabled = true,
            MinFontSize = minFontSize,
            MaxFontSize = maxFontSize,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeFontOverride("font", bold ? FontBold : FontRegular);
        label.AddThemeFontSizeOverride("font_size", maxFontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>
    /// 支持游戏 BBCode（[gold] [red] [green] [jitter] … + 标准 [color=…]）的富文本，
    /// 使用游戏自带 MegaRichTextLabel（自带 BBCode 特效集与语言字体替换）。
    /// 固定字号（AutoSize 关闭 + FitContent），与同尺寸 MegaLabel 视觉一致。
    /// </summary>
    public static MegaRichTextLabel RichLabel(int fontSize, Color color, bool bold = false)
    {
        var label = new MegaRichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AutoSizeEnabled = false, // FitContent 与 AutoSize 互斥，必须关闭（否则引擎告警并禁用）
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var font = bold ? FontBold : FontRegular;
        // 主题项名直接用 Godot 标准字符串：游戏 DLL 里 `ThemeConstants` 的成员名在不同版本
        // （beta camelCase / stable PascalCase）不一致，写死字符串可避免版本耦合。
        label.AddThemeFontOverride("normal_font", font);
        label.AddThemeFontOverride("bold_font", FontBold);
        label.AddThemeFontOverride("italics_font", FontItalic);
        label.AddThemeFontOverride("bold_italics_font", FontBold);
        label.AddThemeFontOverride("mono_font", FontRegular);
        foreach (var sizeName in RichTextFontSizeNames)
            label.AddThemeFontSizeOverride(sizeName, fontSize);
        label.AddThemeColorOverride("default_color", color);
        return label;
    }

    /// <summary>RichTextLabel 的 5 个字号主题项（Godot 标准名）。</summary>
    private static readonly StringName[] RichTextFontSizeNames =
    [
        "normal_font_size", "bold_font_size", "bold_italics_font_size",
        "italics_font_size", "mono_font_size",
    ];

    /// <summary>
    /// 游戏风格按钮：Flat 按钮 + 子 MegaLabel（游戏自身做法，见 NRestSiteButton / NSubMenuButton）
    /// —— 保证按钮文字也走游戏字体与语言替换，并允许独立控制文字颜色/字号。
    /// 注意：Button 无文本时最小高度只由样式框决定，必须显式给 CustomMinimumSize。
    /// </summary>
    public static Button TextButton(string text, int fontSize, Color textColor, Action onPressed,
        bool bold = false, float minHeight = 30f, float minWidth = 0f)
    {
        var button = new Button
        {
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(minWidth, minHeight),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        // 悬停/按下的视觉由样式框（SetButtonStyle）提供，去掉默认的两个空绘制态
        button.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
        button.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        ApplyGameFont(button);
        button.Pressed += onPressed;

        var label = AutoLabel(text, Math.Max(8, fontSize - 4), fontSize, textColor, bold,
            HorizontalAlignment.Center);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        button.AddChild(label);
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return button;
    }

    /// <summary>取按钮内的 MegaLabel（用于状态切换时改文字/颜色）。</summary>
    public static MegaLabel? ButtonLabel(Button button)
    {
        foreach (var child in button.GetChildren())
            if (child is MegaLabel label) return label;
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // 样式框
    // ═══════════════════════════════════════════════════════════════

    private static StyleBoxFlat Box(Color bg, Color border, int borderWidth, int radius,
        int padH = 0, int padV = 0)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
        };
        sb.SetBorderWidthAll(borderWidth);
        sb.SetCornerRadiusAll(radius);
        sb.ContentMarginLeft = padH;
        sb.ContentMarginRight = padH;
        sb.ContentMarginTop = padV;
        sb.ContentMarginBottom = padV;
        return sb;
    }

    /// <summary>主面板样式：冷灰蓝底 + 1px 描边 + 圆角 + 投影。</summary>
    public static StyleBoxFlat PanelStylebox() => new()
    {
        BgColor = PanelBg,
        BorderColor = PanelBorder,
        ShadowColor = new Color(0f, 0f, 0f, 0.45f),
        ShadowSize = 12,
        ShadowOffset = new Vector2(0, 4),
        CornerRadiusTopLeft = 8,
        CornerRadiusTopRight = 8,
        CornerRadiusBottomLeft = 8,
        CornerRadiusBottomRight = 8,
        BorderWidthLeft = 1,
        BorderWidthTop = 1,
        BorderWidthRight = 1,
        BorderWidthBottom = 1,
        ContentMarginLeft = 0,
        ContentMarginRight = 0,
        ContentMarginTop = 0,
        ContentMarginBottom = 0,
    };

    /// <summary>次级面板（附魔弹窗 / 牌组子面板容器）。</summary>
    public static StyleBoxFlat SubPanelStylebox() => Box(PanelBgAlt, PanelBorder, 1, 6, 14, 12);

    /// <summary>条目行底（斑马纹 / 悬停）。</summary>
    public static StyleBoxFlat RowStylebox(bool alt, bool hover) => Box(
        hover ? RowHover : (alt ? RowBgAlt : RowBg),
        hover ? GoldDim : new Color(1f, 1f, 1f, 0.05f),
        1, 5, 10, 4);

    /// <summary>成本 / 点数徽标底（金边小圆片）。</summary>
    public static StyleBoxFlat ChipStylebox(bool affordable) => Box(
        affordable ? new Color(Gold.R, Gold.G, Gold.B, 0.14f) : new Color(Danger.R, Danger.G, Danger.B, 0.14f),
        affordable ? new Color(Gold.R, Gold.G, Gold.B, 0.55f) : new Color(Danger.R, Danger.G, Danger.B, 0.65f),
        1, 4, 7, 1);

    /// <summary>MAX 徽标底（实心金，深色字）。</summary>
    public static StyleBoxFlat MaxBadgeStylebox() => Box(Gold, Gold, 0, 4, 7, 1);

    /// <summary>加号按钮样式（圆形金边，悬停亮金填充）。</summary>
    public static StyleBoxFlat PlusStylebox(bool hover, bool affordable)
    {
        var border = affordable ? (hover ? HoverGlow : Gold) : new Color(1f, 1f, 1f, 0.16f);
        var bg = affordable
            ? (hover ? new Color(Gold.R, Gold.G, Gold.B, 0.30f) : new Color(Gold.R, Gold.G, Gold.B, 0.12f))
            : new Color(1f, 1f, 1f, 0.04f);
        var sb = Box(bg, border, 1, 15, 0, 0);
        return sb;
    }

    /// <summary>普通按钮样式（游戏控件底色）。</summary>
    public static StyleBoxFlat ButtonStylebox(bool hover = false, bool pressed = false, bool flat = false)
    {
        var bg = flat ? new Color(0f, 0f, 0f, 0f) : (pressed ? PanelBg : (hover ? Surface : new Color(Surface.R, Surface.G, Surface.B, 0.85f)));
        return Box(bg, hover ? GoldDim : new Color(1f, 1f, 1f, 0.10f), 1, 5, 10, 4);
    }

    /// <summary>搜索框 / 输入框样式。</summary>
    public static StyleBoxFlat InputStylebox(bool focus)
    {
        var sb = Box(focus ? new Color(0f, 0f, 0f, 0.35f) : new Color(0f, 0f, 0f, 0.25f),
            focus ? Gold : new Color(1f, 1f, 1f, 0.16f), 1, 5, 10, 5);
        return sb;
    }

    /// <summary>给按钮套上完整状态样式（normal/hover/pressed/disabled）。</summary>
    public static void StyleButton(Button button, bool flat = false)
    {
        button.AddThemeStyleboxOverride("normal", ButtonStylebox(flat: flat));
        button.AddThemeStyleboxOverride("hover", ButtonStylebox(hover: true, flat: flat));
        button.AddThemeStyleboxOverride("pressed", ButtonStylebox(pressed: true, flat: flat));
        button.AddThemeStyleboxOverride("focus", ButtonStylebox(hover: true, flat: flat));
        button.AddThemeStyleboxOverride("disabled", Box(new Color(0f, 0f, 0f, 0f), new Color(1f, 1f, 1f, 0.06f), 1, 5, 10, 4));
    }

    /// <summary>细分割线（1px 半透明白）。</summary>
    public static HSeparator Divider()
    {
        var sep = new HSeparator();
        var sb = new StyleBoxLine { Color = new Color(1f, 1f, 1f, 0.08f), Thickness = 1 };
        sep.AddThemeStyleboxOverride("separator", sb);
        return sep;
    }
}
