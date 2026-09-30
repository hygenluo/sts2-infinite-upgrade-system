using System;
using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 条目行（UI v3 排版）：一行一个加点项，视觉层次为
/// `名称 ………… 当前值  [成本]  ( + )`。
///
/// 结构：PanelContainer（行底：斑马纹 / 悬停高亮）
///   └ HBoxContainer
///       ├ MegaRichTextLabel 名称（ExpandFill，支持搜索命中 BBCode 高亮）
///       ├ MegaLabel 当前值（右对齐，金）
///       ├ PanelContainer 成本徽标（金边小圆片 + MegaLabel 数字，点数不足转红）
///       ├ Button 加号（圆形金边 + 子 MegaLabel "+"）/ MAX 徽标（满级时替换）
///
/// 全部文本节点都是游戏自带 MegaLabel / MegaRichTextLabel（见 UpgradeTheme 说明）。
/// 状态机（Refresh）：
/// - Stat 无上限：显示等级数字，加号可重复购买
/// - Stat 限等级（MaxLevel&gt;0）：达上限 → 加号换成 MAX 金徽标
/// - Action：当前值显示 —，加号可重复购买
/// - 点数不足：成本徽标转红 + 加号灰化（仍可点击以触发抖动反馈）
/// - ReadOnly（局内只读）：隐藏加号与成本，仅显示当前值
/// </summary>
public sealed partial class UpgradeItemRow : PanelContainer
{
    private readonly UpgradeItemDef _def;
    private readonly MegaRichTextLabel _nameLabel;
    private readonly MegaLabel _valueLabel;
    private readonly PanelContainer _costChip;
    private readonly MegaLabel _costLabel;
    private readonly Button _plusButton;
    private readonly MegaLabel _plusLabel;
    private readonly PanelContainer _maxBadge;

    private readonly StyleBoxFlat _baseStyle;
    private readonly StyleBoxFlat _hoverStyle;
    private readonly StyleBoxFlat _chipOk;
    private readonly StyleBoxFlat _chipBad;
    private readonly StyleBoxFlat _plusOk;
    private readonly StyleBoxFlat _plusHover;
    private readonly StyleBoxFlat _plusDisabled;

    private Tween? _shakeTween;
    private bool _hovered;

    public UpgradeItemDef Def => _def;

    /// <summary>购买成功事件（+1 飘字与点数跳动）。</summary>
    public event Action? Purchased;

    /// <summary>只读模式（局内查看）：隐藏加号，屏蔽购买交互。</summary>
    public bool ReadOnly { get; set; }

    public UpgradeItemRow(UpgradeItemDef def, bool zebra = false)
    {
        _def = def;
        MouseFilter = MouseFilterEnum.Pass;
        CustomMinimumSize = new Vector2(0, 30);

        _baseStyle = UpgradeTheme.RowStylebox(zebra, false);
        _hoverStyle = UpgradeTheme.RowStylebox(zebra, true);
        AddThemeStyleboxOverride("panel", _baseStyle);

        _chipOk = UpgradeTheme.ChipStylebox(true);
        _chipBad = UpgradeTheme.ChipStylebox(false);
        _plusOk = UpgradeTheme.PlusStylebox(false, true);
        _plusHover = UpgradeTheme.PlusStylebox(true, true);
        _plusDisabled = UpgradeTheme.PlusStylebox(false, false);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);
        row.SetAnchorsPreset(LayoutPreset.FullRect);

        // ── 名称（支持 BBCode 高亮；Label 不支持 BBCode，故用游戏自带 MegaRichTextLabel）──
        _nameLabel = UpgradeTheme.RichLabel(14, UpgradeTheme.TextMain);
        _nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // FitContent 的高度 = 文本高度；用 ShrinkCenter 让它垂直居中（否则文字贴行顶，
        // 与垂直居中的值/成本/加号错位）
        _nameLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _nameLabel.Text = def.DisplayName;
        row.AddChild(_nameLabel);

        // ── 当前值 ──
        _valueLabel = UpgradeTheme.Label("0", 14, UpgradeTheme.Gold, bold: true,
            align: HorizontalAlignment.Right);
        _valueLabel.CustomMinimumSize = new Vector2(66, 0);
        _valueLabel.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(_valueLabel);

        // ── 成本徽标 ──
        _costChip = new PanelContainer
        {
            CustomMinimumSize = new Vector2(38, 20),
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _costChip.AddThemeStyleboxOverride("panel", _chipOk);
        _costLabel = UpgradeTheme.Label("", 12, UpgradeTheme.CostColor, bold: true,
            align: HorizontalAlignment.Center);
        _costLabel.MouseFilter = MouseFilterEnum.Ignore;
        _costChip.AddChild(_costLabel);
        row.AddChild(_costChip);

        // ── 加号按钮（游戏风格：Flat 按钮 + 子 MegaLabel）──
        _plusButton = new Button
        {
            Flat = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(26, 26),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            TooltipText = UpgradeLoc.Format(UpgradeLoc.UiCostTooltip, $"需要 {def.Cost} 点", def.Cost),
        };
        _plusButton.AddThemeStyleboxOverride("normal", _plusOk);
        _plusButton.AddThemeStyleboxOverride("hover", _plusHover);
        _plusButton.AddThemeStyleboxOverride("pressed", _plusHover);
        _plusButton.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        UpgradeTheme.ApplyGameFont(_plusButton);
        _plusButton.Pressed += async () =>
        {
            var ok = await def.OnClick();
            if (ok) Purchased?.Invoke();
            else Shake(); // 点数不足 / 操作失败 → 抖动提示
        };
        _plusLabel = UpgradeTheme.Label("+", 16, UpgradeTheme.Gold, bold: true,
            align: HorizontalAlignment.Center);
        _plusLabel.MouseFilter = MouseFilterEnum.Ignore;
        _plusButton.AddChild(_plusLabel);
        _plusLabel.SetAnchorsPreset(LayoutPreset.FullRect);
        row.AddChild(_plusButton);

        // ── MAX 徽标（满级时替换加号）──
        _maxBadge = new PanelContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(36, 22),
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _maxBadge.AddThemeStyleboxOverride("panel", UpgradeTheme.MaxBadgeStylebox());
        var badgeLabel = UpgradeTheme.Label("MAX", 11, UpgradeTheme.PanelBg, bold: true,
            align: HorizontalAlignment.Center);
        badgeLabel.MouseFilter = MouseFilterEnum.Ignore;
        _maxBadge.AddChild(badgeLabel);
        row.AddChild(_maxBadge);

        // ── 悬停提亮（行底样式切换 + 加号高亮）──
        MouseEntered += () =>
        {
            _hovered = true;
            AddThemeStyleboxOverride("panel", _hoverStyle);
            if (_plusButton.Visible && !ReadOnly)
                _plusButton.AddThemeStyleboxOverride("normal", _plusHover);
        };
        MouseExited += () =>
        {
            _hovered = false;
            AddThemeStyleboxOverride("panel", _baseStyle);
            if (_plusButton.Visible && !ReadOnly)
                _plusButton.AddThemeStyleboxOverride("normal", _plusOk);
        };
    }

    /// <summary>
    /// 搜索命中高亮：命中片段用亮金色 BBCode 标出；query 为空恢复原文。
    /// 仅在显示名包含查询词时生效。
    /// </summary>
    public void SetSearchHighlight(string? query)
    {
        var text = _def.DisplayName;
        var q = query ?? "";
        var idx = q.Length == 0 ? -1 : text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            _nameLabel.Text = text;
            return;
        }
        var matched = text.Substring(idx, q.Length);
        _nameLabel.Text = $"{text[..idx]}[color=#{UpgradeTheme.HoverGlow.ToHtml(false)}]{matched}[/color]{text[(idx + q.Length)..]}";
    }

    /// <summary>按当前点数刷新状态：值文本 / 满级徽标 / 成本颜色 / 加号可用性。</summary>
    public void Refresh(int currentPoints)
    {
        var level = _def.LevelProvider?.Invoke() ?? 0;
        var isMaxed = _def.MaxLevel > 0 && level >= _def.MaxLevel;
        var affordable = currentPoints >= _def.Cost;

        if (_def.Kind == UpgradeItemKind.Action)
            _valueLabel.Text = "—";
        else if (_def.ValueText != null)
            _valueLabel.Text = _def.ValueText();
        else
            _valueLabel.Text = level.ToString();

        _maxBadge.Visible = isMaxed;
        _valueLabel.AddThemeColorOverride(FontColorKey,
            _def.Kind == UpgradeItemKind.Action ? UpgradeTheme.TextSecondary : UpgradeTheme.Gold);

        // 只读模式（局内查看）：隐藏加号与成本，仅显示当前值 / MAX
        if (ReadOnly)
        {
            _plusButton.Visible = false;
            _costChip.Visible = false;
            return;
        }

        _plusButton.Visible = !isMaxed;
        _costChip.Visible = _def.Cost > 0;
        _costLabel.Text = _def.Cost > 0 ? _def.Cost.ToString() : "";
        _costChip.AddThemeStyleboxOverride("panel", affordable ? _chipOk : _chipBad);
        _costLabel.AddThemeColorOverride(FontColorKey,
            affordable ? UpgradeTheme.CostColor : UpgradeTheme.Danger);

        // 点数不足：加号灰化（保持可点击 → 点击抖动反馈）
        var plusStyle = affordable ? (_hovered ? _plusHover : _plusOk) : _plusDisabled;
        _plusButton.AddThemeStyleboxOverride("normal", plusStyle);
        _plusButton.AddThemeStyleboxOverride("hover", affordable ? _plusHover : _plusDisabled);
        _plusLabel.AddThemeColorOverride(FontColorKey,
            affordable ? UpgradeTheme.Gold : new Color(1f, 1f, 1f, 0.35f));
        _plusButton.MouseDefaultCursorShape = affordable ? CursorShape.PointingHand : CursorShape.Arrow;
    }

    /// <summary>MegaLabel 的文本颜色主题项名。</summary>
    private static readonly StringName FontColorKey = "font_color";

    /// <summary>操作失败 / 点数不足的抖动提示（水平 ±4px 三次）。</summary>
    public void Shake()
    {
        _shakeTween?.Kill();
        var baseX = Position.X;
        _shakeTween = CreateTween();
        for (int i = 0; i < 3; i++)
        {
            _shakeTween.TweenProperty(this, "position:x", baseX + 4, 0.04f);
            _shakeTween.TweenProperty(this, "position:x", baseX - 4, 0.04f);
        }
        _shakeTween.TweenProperty(this, "position:x", baseX, 0.04f);
    }
}
