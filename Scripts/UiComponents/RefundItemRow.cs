using System;
using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 回退流水行（v2.3 回退分区）。
///
/// 视觉语言与 <see cref="UpgradeItemRow"/> 一致（斑马纹 + 悬停高亮 + 成本徽标），
/// 但语义是「撤销一条已购买的加点」：
/// `名称 / 对象  |  已付 N  |  [−] +返还`
///
/// - 可回退：右侧是减号按钮 + 返还点数；返还 &gt; 0 时需**二次点击确认**（防误触 20% 损失）。
/// - 不可回退（升级卡牌 / 已不在牌组等）：显示「不可回退」灰色徽标 + 原因 tooltip。
/// - 只读模式（战斗内）：隐藏按钮，只展示流水。
/// </summary>
public sealed partial class RefundItemRow : PanelContainer
{
    private readonly RefundEntry _entry;
    private readonly StyleBoxFlat _baseStyle;
    private readonly StyleBoxFlat _hoverStyle;
    private readonly StyleBoxFlat _chipStyle;
    private readonly PanelContainer _paidChip;
    private readonly MegaLabel _paidLabel;
    private readonly PanelContainer? _refundChip;
    private readonly MegaLabel? _refundLabel;
    private readonly Button? _refundButton;
    private readonly MinusIcon? _refundIcon;
    private readonly StyleBoxFlat? _refundIdle;
    private readonly StyleBoxFlat? _refundHover;
    private readonly StyleBoxFlat? _refundArmed;
    private bool _armed;
    private Tween? _armTween;

    /// <summary>回退成功（参数 = 返还点数）。</summary>
    public event Action<int>? Refunded;

    public RefundEntry Entry => _entry;

    /// <summary>只读模式（局内）：隐藏回退按钮。</summary>
    public bool ReadOnly { get; set; }

    /// <summary>行是否可回退（含按钮可见性判定）。</summary>
    public bool Refundable => _entry.Refundable && _refundButton != null;

    public RefundItemRow(RefundEntry entry, bool zebra = false)
    {
        _entry = entry;
        MouseFilter = MouseFilterEnum.Pass;
        CustomMinimumSize = new Vector2(0, 30);

        _baseStyle = UpgradeTheme.RowStylebox(zebra, false);
        _hoverStyle = UpgradeTheme.RowStylebox(zebra, true);
        AddThemeStyleboxOverride("panel", _baseStyle);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);
        row.SetAnchorsPreset(LayoutPreset.FullRect);

        // 名称（ExpandFill）
        var name = UpgradeTheme.Label(entry.DisplayName, 13,
            entry.Refundable ? UpgradeTheme.TextMain : UpgradeTheme.TextSecondary);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(name);

        // 对象说明（卡牌名 / 词条 / 更早修改提示）
        if (!string.IsNullOrEmpty(entry.Detail))
        {
            var detail = UpgradeTheme.Label(entry.Detail, 11, UpgradeTheme.TextSecondary,
                align: HorizontalAlignment.Right);
            detail.MouseFilter = MouseFilterEnum.Ignore;
            row.AddChild(detail);
        }

        // 已付点数徽标
        _chipStyle = UpgradeTheme.ChipStylebox(true);
        _paidChip = new PanelContainer
        {
            CustomMinimumSize = new Vector2(44, 20),
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            TooltipText = UpgradeLoc.Get(UpgradeLoc.UiRefundPaid, "已付点数"),
        };
        _paidChip.AddThemeStyleboxOverride("panel", _chipStyle);
        _paidLabel = UpgradeTheme.Label(
            UpgradeLoc.Format(UpgradeLoc.UiRefundPaidShort, "已付 {0}", entry.PaidCost),
            11, UpgradeTheme.CostColor, align: HorizontalAlignment.Center);
        _paidLabel.MouseFilter = MouseFilterEnum.Ignore;
        _paidChip.AddChild(_paidLabel);
        row.AddChild(_paidChip);

        if (!entry.Refundable)
        {
            // 不可回退：灰色徽标 + 原因 tooltip
            var badge = new PanelContainer
            {
                CustomMinimumSize = new Vector2(56, 20),
                MouseFilter = MouseFilterEnum.Ignore,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                TooltipText = entry.Note ?? "",
            };
            var badgeStyle = new StyleBoxFlat { BgColor = new Color(1f, 1f, 1f, 0.10f) };
            badgeStyle.SetCornerRadiusAll(4);
            badgeStyle.ContentMarginLeft = 7;
            badgeStyle.ContentMarginRight = 7;
            badgeStyle.ContentMarginTop = 1;
            badgeStyle.ContentMarginBottom = 1;
            badge.AddThemeStyleboxOverride("panel", badgeStyle);
            var badgeLabel = UpgradeTheme.Label(
                UpgradeLoc.Get(UpgradeLoc.UiRefundLocked, "不可回退"), 11,
                UpgradeTheme.TextSecondary, align: HorizontalAlignment.Center);
            badgeLabel.MouseFilter = MouseFilterEnum.Ignore;
            badge.AddChild(badgeLabel);
            row.AddChild(badge);
        }
        else
        {
            // 返还点数徽标
            _refundChip = new PanelContainer
            {
                CustomMinimumSize = new Vector2(44, 20),
                MouseFilter = MouseFilterEnum.Ignore,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            _refundChip.AddThemeStyleboxOverride("panel", UpgradeTheme.ChipStylebox(true));
            _refundLabel = UpgradeTheme.Label(
                $"+{entry.Refund}", 11, UpgradeTheme.Gold, bold: true,
                align: HorizontalAlignment.Center);
            _refundLabel.MouseFilter = MouseFilterEnum.Ignore;
            _refundChip.AddChild(_refundLabel);
            row.AddChild(_refundChip);

            // 回退按钮
            _refundIdle = UpgradeTheme.PlusStylebox(false, true);
            _refundIdle.BgColor = new Color(0f, 0f, 0f, 0f);
            _refundIdle.BorderColor = new Color(UpgradeTheme.Gold.R, UpgradeTheme.Gold.G,
                UpgradeTheme.Gold.B, 0.55f);
            _refundHover = UpgradeTheme.PlusStylebox(true, true);
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
                TooltipText = entry.Refund > 0
                    ? UpgradeLoc.Format(UpgradeLoc.UiRefundTooltip, "回退 · 返还 {0} 点（{1} 的 80%）",
                        entry.Refund, entry.PaidCost)
                    : UpgradeLoc.Get(UpgradeLoc.UiRefundConfirm, "点击回退（不返还点数）"),
            };
            _refundButton.AddThemeStyleboxOverride("normal", _refundIdle);
            _refundButton.AddThemeStyleboxOverride("hover", _refundHover);
            _refundButton.AddThemeStyleboxOverride("pressed", _refundArmed);
            _refundButton.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            UpgradeTheme.ApplyGameFont(_refundButton);
            _refundButton.Pressed += OnRefundPressed;
            _refundIcon = new MinusIcon { IconColor = UpgradeTheme.Gold };
            _refundButton.AddChild(_refundIcon);
            _refundIcon.SetAnchorsPreset(LayoutPreset.FullRect);
            row.AddChild(_refundButton);
        }

        MouseEntered += () => AddThemeStyleboxOverride("panel", _hoverStyle);
        MouseExited += () => AddThemeStyleboxOverride("panel", _baseStyle);
    }

    /// <summary>只读模式刷新（战斗内隐藏回退按钮）。</summary>
    public void RefreshReadOnly()
    {
        if (_refundButton != null) _refundButton.Visible = !ReadOnly;
        if (_refundChip != null && _entry.Refund > 0) _refundChip.Visible = !ReadOnly;
        if (ReadOnly) Disarm();
    }

    private async void OnRefundPressed()
    {
        if (_entry.Refund > 0 && !_armed)
        {
            Arm();
            return;
        }
        Disarm();

        var ok = await UpgradeRefundService.Enqueue(_entry);
        if (ok) Refunded?.Invoke(_entry.Refund);
    }

    private void Arm()
    {
        if (_refundButton == null) return;
        _armed = true;
        _refundButton.AddThemeStyleboxOverride("normal", _refundArmed);
        if (_refundIcon != null) _refundIcon.IconColor = UpgradeTheme.Danger;
        _refundButton.TooltipText = UpgradeLoc.Get(UpgradeLoc.UiRefundConfirm, "再次点击确认回退");
        _armTween?.Kill();
        _armTween = CreateTween();
        _armTween.TweenInterval(2.5f);
        _armTween.TweenCallback(Callable.From(Disarm));
    }

    private void Disarm()
    {
        _armTween?.Kill();
        _armTween = null;
        _armed = false;
        if (_refundButton == null) return;
        _refundButton.AddThemeStyleboxOverride("normal", _refundIdle);
        if (_refundIcon != null) _refundIcon.IconColor = UpgradeTheme.Gold;
        _refundButton.TooltipText = _entry.Refund > 0
            ? UpgradeLoc.Format(UpgradeLoc.UiRefundTooltip, "回退 · 返还 {0} 点（{1} 的 80%）",
                _entry.Refund, _entry.PaidCost)
            : UpgradeLoc.Get(UpgradeLoc.UiRefundConfirm, "点击回退（不返还点数）");
    }
}
