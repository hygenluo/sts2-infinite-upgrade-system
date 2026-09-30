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

    // 回退（v2.3）
    private readonly Button? _refundButton;
    private readonly MinusIcon? _refundIcon;
    private readonly MegaLabel? _refundAmountLabel;
    private readonly StyleBoxFlat? _refundIdle;
    private readonly StyleBoxFlat? _refundHover;
    private readonly StyleBoxFlat? _refundArmed;
    private bool _refundArmedState;
    private Tween? _refundArmTween;
    private bool _levelPositive;

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

    /// <summary>回退成功事件（返还点数飘字）。参数 = 返还点数。</summary>
    public event Action<int>? Refunded;

    /// <summary>只读模式（局内查看）：隐藏加号与回退按钮，屏蔽交互。</summary>
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

        // ── 回退按钮（v2.3：仅可回退项且等级 > 0 时显示，位于成本徽标左侧）──
        if (def.CanRefund)
        {
            _refundIdle = UpgradeTheme.PlusStylebox(false, true);
            _refundIdle.BgColor = new Color(0f, 0f, 0f, 0f);
            _refundIdle.BorderColor = new Color(UpgradeTheme.CostColor.R, UpgradeTheme.CostColor.G,
                UpgradeTheme.CostColor.B, 0.45f);
            _refundHover = UpgradeTheme.PlusStylebox(false, true);
            _refundHover.BgColor = new Color(UpgradeTheme.CostColor.R, UpgradeTheme.CostColor.G,
                UpgradeTheme.CostColor.B, 0.16f);
            _refundHover.BorderColor = UpgradeTheme.HoverGlow;
            _refundArmed = UpgradeTheme.PlusStylebox(false, false);
            _refundArmed.BgColor = new Color(UpgradeTheme.Danger.R, UpgradeTheme.Danger.G,
                UpgradeTheme.Danger.B, 0.22f);
            _refundArmed.BorderColor = UpgradeTheme.Danger;

            _refundButton = new Button
            {
                Flat = true,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(26, 26),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseDefaultCursorShape = CursorShape.PointingHand,
                Visible = false,
                TooltipText = RefundTooltip(),
            };
            _refundButton.AddThemeStyleboxOverride("normal", _refundIdle);
            _refundButton.AddThemeStyleboxOverride("hover", _refundHover);
            _refundButton.AddThemeStyleboxOverride("pressed", _refundArmed);
            _refundButton.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            UpgradeTheme.ApplyGameFont(_refundButton);
            _refundButton.Pressed += OnRefundPressed;

            _refundIcon = new MinusIcon { IconColor = UpgradeTheme.CostColor };
            _refundButton.AddChild(_refundIcon);
            _refundIcon.SetAnchorsPreset(LayoutPreset.FullRect);

            // 返还点数小字（挂在按钮右侧的独立标签，避免挤在按钮里）
            _refundAmountLabel = UpgradeTheme.Label(
                $"+{UpgradeRefundService.RefundOf(def.Cost)}", 11, UpgradeTheme.CostColor,
                align: HorizontalAlignment.Right)
            ;
            _refundAmountLabel.CustomMinimumSize = new Vector2(22, 0);
            _refundAmountLabel.MouseFilter = MouseFilterEnum.Ignore;
            _refundAmountLabel.Visible = false;
            row.AddChild(_refundAmountLabel);
            row.AddChild(_refundButton);
            // 排版：[名称][值][−][+返还][成本][+][MAX] —— 减号与返还额紧邻，读作「回退一级返还 N」
            row.MoveChild(_refundButton, _costChip.GetIndex());
            row.MoveChild(_refundAmountLabel, _refundButton.GetIndex() + 1);
        }

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

        // 只读模式（局内查看）：隐藏加号与回退，仅显示当前值 / MAX
        if (ReadOnly)
        {
            _plusButton.Visible = false;
            _costChip.Visible = false;
            if (_refundButton != null) _refundButton.Visible = false;
            if (_refundAmountLabel != null) _refundAmountLabel.Visible = false;
            return;
        }

        _plusButton.Visible = !isMaxed;
        _costChip.Visible = _def.Cost > 0;
        _costLabel.Text = _def.Cost > 0 ? _def.Cost.ToString() : "";
        _costChip.AddThemeStyleboxOverride("panel", affordable ? _chipOk : _chipBad);
        _costLabel.AddThemeColorOverride(FontColorKey,
            affordable ? UpgradeTheme.CostColor : UpgradeTheme.Danger);

        // 回退（等级 > 0 才可用；MAX 徽标与回退并存 —— 满级项同样可以退回）
        _levelPositive = level > 0;
        if (_refundButton != null)
        {
            _refundButton.Visible = _levelPositive;
            if (!_levelPositive) DisarmRefund();
        }
        if (_refundAmountLabel != null)
        {
            _refundAmountLabel.Visible = _levelPositive;
            _refundAmountLabel.Text = $"+{UpgradeRefundService.RefundOf(_def.Cost)}";
            _refundAmountLabel.AddThemeColorOverride(FontColorKey,
                UpgradeRefundService.RefundOf(_def.Cost) > 0
                    ? UpgradeTheme.CostColor
                    : UpgradeTheme.TextSecondary);
        }

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

    // ═══════════════════════════════════════════════════════════════
    // 回退（v2.3）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>返还点数提示文本（0 点时会说明原因）。</summary>
    private string RefundTooltip()
    {
        int refund = UpgradeRefundService.RefundOf(_def.Cost);
        return refund > 0
            ? UpgradeLoc.Format(UpgradeLoc.UiRefundTooltip, "回退 1 级 · 返还 {0} 点（{1} 的 80%）", refund, _def.Cost)
            : UpgradeLoc.Format(UpgradeLoc.UiRefundTooltipZero, "回退 1 级 · 返还 0 点（{0} 点 × 80% 下取整）", _def.Cost);
    }

    /// <summary>
    /// 回退按钮：返还 &gt; 0 时需要**二次点击确认**（首次点击进入确认态，2.5 秒后自动取消），
    /// 避免误触造成 20% 点数损失。
    /// </summary>
    private async void OnRefundPressed()
    {
        if (_def.RefundKind == null || _def.RefundTarget == null) return;

        if (UpgradeRefundService.RefundOf(_def.Cost) > 0 && !_refundArmedState)
        {
            ArmRefund();
            return;
        }
        DisarmRefund();

        var entry = new RefundEntry
        {
            Kind = _def.RefundKind.Value,
            Target = _def.RefundTarget,
            PaidCost = _def.Cost,
            DisplayName = _def.DisplayName,
            Refundable = true,
        };
        var ok = await UpgradeRefundService.Enqueue(entry);
        if (ok) Refunded?.Invoke(entry.Refund);
        else Shake();
    }

    /// <summary>进入确认态：减号变红，提示改为「再次点击确认」。</summary>
    private void ArmRefund()
    {
        if (_refundButton == null) return;
        _refundArmedState = true;
        _refundButton.AddThemeStyleboxOverride("normal", _refundArmed);
        if (_refundIcon != null) _refundIcon.IconColor = UpgradeTheme.Danger;
        _refundButton.TooltipText =
            UpgradeLoc.Get(UpgradeLoc.UiRefundConfirm, "再次点击确认回退");

        _refundArmTween?.Kill();
        _refundArmTween = CreateTween();
        _refundArmTween.TweenInterval(2.5f);
        _refundArmTween.TweenCallback(Callable.From(DisarmRefund));
    }

    private void DisarmRefund()
    {
        _refundArmTween?.Kill();
        _refundArmTween = null;
        _refundArmedState = false;
        if (_refundButton == null) return;
        _refundButton.AddThemeStyleboxOverride("normal", _refundIdle);
        if (_refundIcon != null) _refundIcon.IconColor = UpgradeTheme.CostColor;
        _refundButton.TooltipText = RefundTooltip();
    }

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
