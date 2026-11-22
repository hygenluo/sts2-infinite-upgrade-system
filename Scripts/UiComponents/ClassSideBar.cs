using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 技能分区的**角色选择列**（UI v4）。
///
/// 上一版（`ClassTabBar`）把 6 个角色挤成一行标签，「亡灵契约师 / 故障机器人」这类长名在
/// 720px 面板里必须缩到 11px 才不裁切，且右列宽度被标签行占掉一整行。UI v4 改成左右分栏：
/// **左列选角色、右列显示该角色的加点项**，角色名有整列宽度可用。
///
/// 结构：PanelContainer（次级面板底 + 圆角描边）
///   └ VBoxContainer
///       ├ MegaLabel「角色」列标题
///       └ SkillRoleButton × N（名称 + 已拥有/总数）
///
/// 交互：点击角色 → 金色高亮 + **向右滑出并放大**（见 <see cref="SkillRoleButton.SetSelected"/>），
/// 同时由 `UpgradeUIHandler` 切换右列内容并播放淡入。
/// 角色按钮只在「该角色有技能」时显示（`SetCount` 里按 total 判断）—— 新角色机制
/// 可以先落地常量、技能随后补，界面不会出现空按钮。
/// </summary>
public sealed partial class ClassSideBar : PanelContainer
{
    /// <summary>列宽（含内边距）：720px 面板下右列仍有 ~540px 给加点行。</summary>
    public const float SidebarWidth = 132f;

    private readonly VBoxContainer _list;
    private readonly Dictionary<string, SkillRoleButton> _buttons = new();
    private string? _selected;

    /// <summary>当前选中的角色（构建后必有值）。</summary>
    public string? SelectedClass => _selected;

    /// <summary>角色切换事件（参数 = <see cref="SkillClasses"/> 里的角色 key）。</summary>
    public event Action<string>? ClassSelected;

    public ClassSideBar()
    {
        CustomMinimumSize = new Vector2(SidebarWidth, 0);
        // 列自身不随右列高度拉伸：始终贴顶，避免高亮块被拉成一整根长条
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        // Pass 而非 Stop：列里空白处的滚轮事件要继续冒泡给 ScrollContainer，否则鼠标停在
        // 左列上时面板滚不动（按钮本身是 Stop，点选不受影响）
        MouseFilter = MouseFilterEnum.Pass;
        AddThemeStyleboxOverride("panel", ColumnStylebox());

        _list = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        _list.AddThemeConstantOverride("separation", 4);
        AddChild(_list);

        var header = UpgradeTheme.Label(
            UpgradeLoc.Get(UpgradeLoc.UiSkillRoleList, "角色"), 11, UpgradeTheme.TextSecondary);
        header.MouseFilter = MouseFilterEnum.Ignore;
        _list.AddChild(header);

        foreach (var cls in SkillClasses.All)
        {
            var key = cls;
            var button = new SkillRoleButton(UpgradeLoc.ClassTitle(cls));
            button.Pressed += () => Select(key);
            _list.AddChild(button);
            _buttons[cls] = button;
        }
    }

    /// <summary>选中角色并刷新高亮；首次调用（构建期）不播放动画。</summary>
    public void Select(string cls)
    {
        if (!_buttons.ContainsKey(cls)) return;
        var hadSelection = _selected != null;
        var changed = _selected != cls;
        _selected = cls;
        foreach (var (key, button) in _buttons)
            button.SetSelected(key == cls, animate: changed && hadSelection);
        ClassSelected?.Invoke(cls);
    }

    /// <summary>设置某角色的「已拥有 / 总数」（显示在名称下方；total=0 时隐藏该角色）。</summary>
    public void SetCount(string cls, int owned, int total)
    {
        if (!_buttons.TryGetValue(cls, out var button)) return;
        button.SetCount(owned, total);
        button.Visible = total > 0;
    }

    /// <summary>左列面板底：比主面板亮一档的冷灰蓝 + 1px 描边，把「选择列」从右列内容里分出来。</summary>
    private static StyleBoxFlat ColumnStylebox()
    {
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(UpgradeTheme.PanelBgAlt.R, UpgradeTheme.PanelBgAlt.G,
                UpgradeTheme.PanelBgAlt.B, 0.92f),
            BorderColor = new Color(1f, 1f, 1f, 0.07f),
        };
        sb.SetBorderWidthAll(1);
        sb.SetCornerRadiusAll(6);
        sb.ContentMarginLeft = 8;
        sb.ContentMarginRight = 8;
        sb.ContentMarginTop = 8;
        sb.ContentMarginBottom = 8;
        return sb;
    }
}

/// <summary>
/// 角色列里的单个角色按钮（两行：名称 / 已拥有·总数）。
///
/// 选中态 = 金色半透明底 + 金描边 + 左侧金色强调条 + 名称转亮金 + **滑出变大**：
/// `Scale` 1 → 1.06（轴心在左中，向右生长）+ 内容整体右移 6px。
/// 只对 `Scale` / 子节点 `Position` 做补间：不动容器布局，所以不会引起列内其它按钮跳动，
/// 也不会在滚动/展开收起时被打回原形（父容器只接管自身位置与尺寸，不接管 Scale）。
/// 强调条单独挂一个 `ColorRect`：内容右移时强调条留在左边，视觉上就是「被选中项从列里探出来」。
/// </summary>
public sealed partial class SkillRoleButton : Button
{
    private const float SlideDistance = 6f;
    private const float SelectedScale = 1.06f;

    private readonly MegaLabel _nameLabel;
    private readonly MegaLabel _countLabel;
    private readonly MarginContainer _body;
    private readonly ColorRect _accent;
    private readonly StyleBoxFlat _idleStyle;
    private readonly StyleBoxFlat _hoverStyle;
    private readonly StyleBoxFlat _selectedStyle;
    private Tween? _tween;
    private bool _selected;
    private bool _hovered;

    public SkillRoleButton(string title)
    {
        Flat = false; // 必须非 Flat：高亮底与描边全靠 StyleBox，Flat 按钮不保证绘制
        FocusMode = FocusModeEnum.None;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(0, 44);
        MouseDefaultCursorShape = CursorShape.PointingHand;
        ClipContents = false; // 放大后的按钮要能溢出到右列间隙里

        _idleStyle = BuildStyle(hovered: false, selected: false);
        _hoverStyle = BuildStyle(hovered: true, selected: false);
        _selectedStyle = BuildStyle(hovered: false, selected: true);
        AddThemeStyleboxOverride("normal", _idleStyle);
        AddThemeStyleboxOverride("hover", _idleStyle);
        AddThemeStyleboxOverride("pressed", _idleStyle);
        // hover_pressed 也要盖掉，否则「悬停中按下」会闪一下游戏默认按钮底
        AddThemeStyleboxOverride("hover_pressed", _idleStyle);
        AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        AddThemeStyleboxOverride("disabled", _idleStyle);
        UpgradeTheme.ApplyGameFont(this);

        // 左侧强调条（选中亮金 / 未选中极淡白）
        _accent = new ColorRect
        {
            Color = new Color(1f, 1f, 1f, 0.10f),
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0f,
            AnchorRight = 0f,
            AnchorTop = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 6f,
            OffsetRight = 9f,
            OffsetTop = 9f,
            OffsetBottom = -9f,
        };
        AddChild(_accent);

        // 正文（名称 + 计数）—— 用 MarginContainer 撑内边距，滑出动画只动它的 Position
        _body = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        _body.AddThemeConstantOverride("margin_left", 16);
        _body.AddThemeConstantOverride("margin_right", 6);
        _body.AddThemeConstantOverride("margin_top", 5);
        _body.AddThemeConstantOverride("margin_bottom", 5);
        AddChild(_body);
        _body.SetAnchorsPreset(LayoutPreset.FullRect);

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 0);
        _body.AddChild(column);

        _nameLabel = UpgradeTheme.AutoLabel(title, 9, 13, UpgradeTheme.TextSecondary, bold: true);
        _nameLabel.MouseFilter = MouseFilterEnum.Ignore;
        column.AddChild(_nameLabel);

        // 初始给 "0/0" 而不是空串：空文本的 MegaLabel 最小高度为 0，会让按钮在首次
        // SetCount 前后高度跳一下
        _countLabel = UpgradeTheme.Label("0/0", 10, UpgradeTheme.TextSecondary);
        _countLabel.MouseFilter = MouseFilterEnum.Ignore;
        column.AddChild(_countLabel);

        // 缩放轴心固定在左中：放大时向右/上下对称溢出（从列里"探出去"的方向）
        Resized += () =>
        {
            PivotOffset = new Vector2(0f, Size.Y * 0.5f);
            _body.Position = new Vector2(_selected ? SlideDistance : 0f, 0f);
        };

        MouseEntered += () =>
        {
            _hovered = true;
            ApplyStyle();
        };
        MouseExited += () =>
        {
            _hovered = false;
            ApplyStyle();
        };
    }

    /// <summary>设置「已拥有 / 总数」；0/0 时按钮由外部隐藏。</summary>
    public void SetCount(int owned, int total)
    {
        _countLabel.Text = total > 0 ? $"{owned}/{total}" : "";
        _countLabel.AddThemeColorOverride("font_color", owned > 0
            ? UpgradeTheme.CostColor
            : new Color(1f, 1f, 1f, 0.26f));
    }

    /// <summary>
    /// 选中态切换。animate=true 时播放「滑出变大」：Scale 1 → 1.06 + 内容右移，
    /// 取消选中反向收回（更短，避免连点角色时排队动画）。
    /// </summary>
    public void SetSelected(bool selected, bool animate)
    {
        var changed = _selected != selected;
        _selected = selected;
        ApplyStyle();

        var targetScale = selected ? new Vector2(SelectedScale, SelectedScale) : Vector2.One;
        var targetX = selected ? SlideDistance : 0f;

        if (!animate || !changed)
        {
            _tween?.Kill();
            _tween = null;
            Scale = targetScale;
            _body.Position = new Vector2(targetX, 0f);
            return;
        }

        _tween?.Kill();
        _tween = CreateTween().SetParallel(true)
            .SetTrans(Tween.TransitionType.Back)
            .SetEase(Tween.EaseType.Out);
        _tween.TweenProperty(this, "scale", targetScale, selected ? 0.20f : 0.12f);
        _tween.TweenProperty(_body, "position:x", targetX, selected ? 0.20f : 0.12f);
    }

    private void ApplyStyle()
    {
        var idle = _selected ? _selectedStyle : (_hovered ? _hoverStyle : _idleStyle);
        AddThemeStyleboxOverride("normal", idle);
        AddThemeStyleboxOverride("pressed", idle);
        AddThemeStyleboxOverride("hover_pressed", idle);
        // 选中项不做 hover 变色（高亮已经最强），未选中项悬停提亮
        AddThemeStyleboxOverride("hover", _selected ? _selectedStyle : _hoverStyle);
        _accent.Color = _selected
            ? UpgradeTheme.Gold
            : new Color(1f, 1f, 1f, _hovered ? 0.22f : 0.10f);
        _nameLabel.AddThemeColorOverride("font_color", _selected
            ? UpgradeTheme.HoverGlow
            : (_hovered ? UpgradeTheme.TextMain : UpgradeTheme.TextSecondary));
    }

    private static StyleBoxFlat BuildStyle(bool hovered, bool selected)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = selected
                ? new Color(UpgradeTheme.Gold.R, UpgradeTheme.Gold.G, UpgradeTheme.Gold.B, 0.16f)
                : (hovered ? UpgradeTheme.RowHover : new Color(0f, 0f, 0f, 0f)),
            BorderColor = selected
                ? new Color(UpgradeTheme.Gold.R, UpgradeTheme.Gold.G, UpgradeTheme.Gold.B, 0.85f)
                : new Color(1f, 1f, 1f, hovered ? 0.14f : 0.06f),
        };
        sb.SetBorderWidthAll(1);
        sb.SetCornerRadiusAll(5);
        // 内边距由 _body 的 MarginContainer 提供，样式框本身不留边
        sb.ContentMarginLeft = 0;
        sb.ContentMarginRight = 0;
        sb.ContentMarginTop = 0;
        sb.ContentMarginBottom = 0;
        if (selected)
        {
            // 选中项外侧一圈弱金辉光，把「探出来」的那一档和列底色分开
            sb.ShadowColor = new Color(UpgradeTheme.Gold.R, UpgradeTheme.Gold.G, UpgradeTheme.Gold.B, 0.22f);
            sb.ShadowSize = 4;
        }
        return sb;
    }
}
