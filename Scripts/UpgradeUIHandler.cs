using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using InfiniteUpgradeSystem.UiComponents;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

public sealed partial class UpgradeUIHandler : Control
{
    private const Key ToggleHotkey = Key.P;
    private const float PanelWidth = 640f;
    private const float PanelHeight = 620f;

    private static UpgradeUIHandler? s_instance;

    // UI 组件
    private ColorRect? _background;
    private Panel? _mainPanel;
    private Control? _titleBar;
    private Label? _pointsLabel;
    private LineEdit? _searchBox;
    private ScrollContainer? _scrollContainer;
    private VBoxContainer? _scrollContent;
    private bool _isOpen;

    // 拖拽
    private bool _isDragging;
    private Vector2 _dragOffset;

    // 操作项数据 + 控件映射
    private readonly List<UpgradeItemData> _allItems = new();
    private readonly Dictionary<UpgradeItemData, Control> _itemControls = new();

    public static void CreateInstance()
    {
        if (s_instance != null) return;
        s_instance = new UpgradeUIHandler { Name = "InfiniteUpgradeUI" };
        var tree = (SceneTree?)Engine.GetMainLoop();
        tree?.Root?.CallDeferred(Node.MethodName.AddChild, s_instance);
    }

    public static UpgradeUIHandler? Instance => s_instance;

    public override void _Ready()
    {
        PopulateItems();
        BuildUI();
        SetUIVisible(false);
        Log.Info("InfiniteUpgradeUI: ready.");
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
        {
            ResizeBackground();
            CenterMainPanel();
        }
    }

    public override void _Input(InputEvent @event)
    {
        // 自动隐藏：RunState 不存在了（回到主菜单）
        if (_isOpen && RunManager.Instance?.DebugOnlyGetState() == null)
        {
            HideUI();
            return;
        }

        // 快捷键
        if (@event is InputEventKey { Pressed: true, Keycode: ToggleHotkey, Echo: false })
        {
            GetViewport().SetInputAsHandled();
            ToggleUI();
        }
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape, Echo: false } && _isOpen)
        {
            GetViewport().SetInputAsHandled();
            HideUI();
        }

        // 拖拽标题栏移动面板
        if (_isOpen && _mainPanel != null)
        {
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed && IsMouseInTitleBar())
                {
                    _isDragging = true;
                    _dragOffset = _mainPanel.Position - GetGlobalMousePosition();
                    GetViewport().SetInputAsHandled();
                }
                else
                {
                    _isDragging = false;
                }
            }

            if (@event is InputEventMouseMotion && _isDragging)
            {
                _mainPanel.Position = GetGlobalMousePosition() + _dragOffset;
                ClampPanelToScreen();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    /// <summary>鼠标是否在标题栏区域（面板顶部 42px）。</summary>
    private bool IsMouseInTitleBar()
    {
        if (_mainPanel == null) return false;
        var mousePos = GetGlobalMousePosition();
        var panelPos = _mainPanel.GlobalPosition;
        return mousePos.X >= panelPos.X && mousePos.X <= panelPos.X + PanelWidth
            && mousePos.Y >= panelPos.Y && mousePos.Y <= panelPos.Y + 42;
    }

    /// <summary>限制面板不超出屏幕。</summary>
    private void ClampPanelToScreen()
    {
        if (_mainPanel == null) return;
        var vpSize = GetViewportRect().Size;
        _mainPanel.Position = new Vector2(
            Mathf.Clamp(_mainPanel.Position.X, -PanelWidth + 80, vpSize.X - 80),
            Mathf.Clamp(_mainPanel.Position.Y, 0, vpSize.Y - 60));
    }

    // ═══════════════════════════════════════════════════════════════
    // UI 构建
    // ═══════════════════════════════════════════════════════════════

    private void BuildUI()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorRight = 1;
        AnchorBottom = 1;

        // 全屏半透明背景
        _background = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.65f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        _background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_background);

        // 居中面板
        _mainPanel = new Panel
        {
            CustomMinimumSize = new Vector2(PanelWidth, PanelHeight),
            MouseFilter = MouseFilterEnum.Stop,
        };
        AddChild(_mainPanel);

        var outerMargin = 12;
        var innerWidth = PanelWidth - outerMargin * 2;
        var innerHeight = PanelHeight - outerMargin * 2;

        var outerVBox = new VBoxContainer
        {
            Position = new Vector2(outerMargin, outerMargin),
            Size = new Vector2(innerWidth, innerHeight),
        };
        _mainPanel.AddChild(outerVBox);

        // 标题栏（可拖拽）
        _titleBar = new Control { CustomMinimumSize = new Vector2(0, 32) };
        var title = new Label
        {
            Text = "⋮⋮ 无限升级系统 （拖拽此处移动）",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Pass,
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
        _titleBar.AddChild(title);
        title.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        outerVBox.AddChild(_titleBar);

        // 点数 + 提示行
        var infoRow = new HBoxContainer();
        _pointsLabel = new Label { HorizontalAlignment = HorizontalAlignment.Left };
        _pointsLabel.AddThemeFontSizeOverride("font_size", 18);
        infoRow.AddChild(_pointsLabel);

        var hintLabel = new Label
        {
            Text = "[P] 打开/关闭  [Esc] 关闭",
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill,
        };
        hintLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        infoRow.AddChild(hintLabel);
        outerVBox.AddChild(infoRow);

        outerVBox.AddChild(new HSeparator());

        // 搜索框
        _searchBox = new LineEdit
        {
            PlaceholderText = "搜索加点项目...",
            ClearButtonEnabled = true,
        };
        _searchBox.TextChanged += OnSearchTextChanged;
        outerVBox.AddChild(_searchBox);

        outerVBox.AddChild(new HSeparator());

        // 可滚动区域
        _scrollContainer = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.Expand | SizeFlags.Fill,
            FollowFocus = true,
        };
        _scrollContent = new VBoxContainer();
        _scrollContainer.AddChild(_scrollContent);
        outerVBox.AddChild(_scrollContainer);

        // 生成分类按钮
        BuildCategorySections();

        outerVBox.AddChild(new HSeparator());

        // 关闭按钮
        var closeButton = new Button { Text = "关闭" };
        closeButton.Pressed += OnCloseClicked;
        outerVBox.AddChild(closeButton);
    }

    private void BuildCategorySections()
    {
        var categories = _allItems.Select(i => i.Category).Distinct();
        foreach (var category in categories)
        {
            var header = new Label
            {
                Text = $"── {category} ──",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            header.AddThemeColorOverride("font_color", new Color(0.8f, 0.7f, 0.3f));
            header.AddThemeFontSizeOverride("font_size", 16);
            _scrollContent!.AddChild(header);

            foreach (var item in _allItems.Where(i => i.Category == category))
            {
                var button = new Button
                {
                    Text = item.ButtonLabel,
                    TooltipText = item.DisplayName,
                };
                button.Pressed += async () =>
                {
                    GD.Print($"[InfiniteUpgrade] Clicked: {item.DisplayName}");
                    await item.OnClick();
                    RefreshPointsLabel();
                };
                _scrollContent.AddChild(button);
                _itemControls[item] = button;
            }

            _scrollContent.AddChild(new HSeparator());
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 搜索
    // ═══════════════════════════════════════════════════════════════

    private void OnSearchTextChanged(string text)
    {
        var filter = text.Trim().ToLower();
        foreach (var item in _allItems)
        {
            if (_itemControls.TryGetValue(item, out var control))
            {
                control.Visible = string.IsNullOrEmpty(filter)
                    || item.SearchText.Contains(filter);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 操作项定义（47 项）—— onClick 在 Phase 3+ 实现
    // ═══════════════════════════════════════════════════════════════

    private void PopulateItems()
    {
        // === 卡牌操作 (29) ===
        _allItems.Add(new("卡牌操作", "升级卡牌", 5, async () =>
        {
            if (!UpgradePointManager.TrySpendPoints(CardOperationHelper.UpgradeCost)) return;
            var player = CardOperationHelper.GetLocalPlayer();
            if (player == null) return;
            SetUIVisible(false);
            try
            {
                var card = await CardOperationHelper.SelectCardFromDeck(player);
                if (card == null)
                {
                    UpgradePointManager.AddPoints(CardOperationHelper.UpgradeCost);
                    RefreshPointsLabel();
                    SetUIVisible(true);
                    return;
                }
                CardOperationHelper.PerformInfiniteUpgrade(card);
                RefreshPointsLabel();
                HideUI();
            }
            catch (Exception ex)
            {
                Log.Error($"Upgrade error: {ex.Message}");
                UpgradePointManager.AddPoints(CardOperationHelper.UpgradeCost);
                RefreshPointsLabel();
                SetUIVisible(true);
            }
        }));

        _allItems.Add(new("卡牌操作", "攻击+1", 1, () => CardOperationHelper.ModifyDamage(1)));
        _allItems.Add(new("卡牌操作", "格挡+1", 1, () => CardOperationHelper.ModifyBlock(1)));
        _allItems.Add(new("卡牌操作", "抽牌+1", 7, () => CardOperationHelper.ModifyDrawCount(7)));
        _allItems.Add(new("卡牌操作", "重放+1", 7, () => CardOperationHelper.ModifyReplayCount(7)));
        _allItems.Add(new("卡牌操作", "耗能-1", 12, () => CardOperationHelper.ReduceEnergyCost(12)));
        _allItems.Add(new("卡牌操作", "添加消耗", 12, () => CardOperationHelper.ToggleKeyword(12, CardKeyword.Exhaust, true)));
        _allItems.Add(new("卡牌操作", "移除消耗", 20, () => CardOperationHelper.ToggleKeyword(20, CardKeyword.Exhaust, false)));
        _allItems.Add(new("卡牌操作", "添加奇巧", 7, () => CardOperationHelper.ToggleKeyword(7, CardKeyword.Sly, true)));
        _allItems.Add(new("卡牌操作", "添加保留", 7, () => CardOperationHelper.ToggleKeyword(7, CardKeyword.Retain, true)));
        _allItems.Add(new("卡牌操作", "移除保留", 7, () => CardOperationHelper.ToggleKeyword(7, CardKeyword.Retain, false)));
        _allItems.Add(new("卡牌操作", "添加固有", 7, () => CardOperationHelper.ToggleKeyword(7, CardKeyword.Innate, true)));
        _allItems.Add(new("卡牌操作", "移除固有", 7, () => CardOperationHelper.ToggleKeyword(7, CardKeyword.Innate, false)));
        _allItems.Add(new("卡牌操作", "添加虚无", 8, () => CardOperationHelper.ToggleKeyword(8, CardKeyword.Ethereal, true)));
        _allItems.Add(new("卡牌操作", "移除虚无", 8, () => CardOperationHelper.ToggleKeyword(8, CardKeyword.Ethereal, false)));
        _allItems.Add(new("卡牌操作", "添加永恒", 10, () => CardOperationHelper.ToggleKeyword(10, CardKeyword.Eternal, true)));
        _allItems.Add(new("卡牌操作", "移除永恒", 30, () => CardOperationHelper.ToggleKeyword(30, CardKeyword.Eternal, false)));

        // === 能力操作 (12) ===
        _allItems.Add(new("能力操作", "初始力量+1", 10, () => { if (AbilityOperationHelper.TryPurchase("strength", 10)) RefreshPointsLabel(); return Task.CompletedTask; }));
        _allItems.Add(new("能力操作", "初始敏捷+1", 10, () => { if (AbilityOperationHelper.TryPurchase("dexterity", 10)) RefreshPointsLabel(); return Task.CompletedTask; }));
        _allItems.Add(new("能力操作", "初始集中+1", 15, () => { if (AbilityOperationHelper.TryPurchase("focus", 15)) RefreshPointsLabel(); return Task.CompletedTask; }));
        _allItems.Add(new("能力操作", "生命+1", 5, async () => { if (AbilityOperationHelper.TryPurchase("hp", 5)) { await AbilityOperationHelper.ApplyImmediate("hp"); RefreshPointsLabel(); } }));
        _allItems.Add(new("能力操作", "每回合能量+1", 40, async () => { if (AbilityOperationHelper.TryPurchase("energy", 40)) { await AbilityOperationHelper.ApplyImmediate("energy"); RefreshPointsLabel(); } }));
        _allItems.Add(new("能力操作", "每回合辉星+1", 20, () => { if (AbilityOperationHelper.TryPurchase("stars", 20)) RefreshPointsLabel(); return Task.CompletedTask; }));
        _allItems.Add(new("能力操作", "每回合铸造+5", 20, () => { if (AbilityOperationHelper.TryPurchase("forge", 20)) RefreshPointsLabel(); return Task.CompletedTask; }));
        _allItems.Add(new("能力操作", "初始覆甲+1", 15, () => { if (AbilityOperationHelper.TryPurchase("plating", 15)) RefreshPointsLabel(); return Task.CompletedTask; }));
        _allItems.Add(new("能力操作", "初始荆棘+1", 10, () => { if (AbilityOperationHelper.TryPurchase("thorns", 10)) RefreshPointsLabel(); return Task.CompletedTask; }));
        _allItems.Add(new("能力操作", "初始人工制品+1", 20, () => { if (AbilityOperationHelper.TryPurchase("artifact", 20)) RefreshPointsLabel(); return Task.CompletedTask; }));
        _allItems.Add(new("能力操作", "充能球栏位+1", 15, async () => { if (AbilityOperationHelper.TryPurchase("orbSlot", 15)) { await AbilityOperationHelper.ApplyImmediate("orbSlot"); RefreshPointsLabel(); } }));
        _allItems.Add(new("能力操作", "格挡跨回合不消失", 40, () => { if (AbilityOperationHelper.TryPurchase("blockKeep", 40)) RefreshPointsLabel(); return Task.CompletedTask; }));

        // === 牌组操作 (5) ===
        _allItems.Add(new("牌组操作", "从牌组删除一张牌", 0, () => CardOperationHelper.RemoveCardFromDeck()));

        // === 测试操作 (1) ===
        _allItems.Add(new("测试操作", "点数+999", 0, () =>
        {
            UpgradePointManager.AddPoints(999);
            RefreshPointsLabel();
            GD.Print($"[InfiniteUpgrade] +999 points → {UpgradePointManager.CurrentPoints}");
            return Task.CompletedTask;
        }));
    }

    // ═══════════════════════════════════════════════════════════════
    // UI 状态
    // ═══════════════════════════════════════════════════════════════

    private void ToggleUI()
    {
        if (_isOpen) HideUI();
        else ShowUI();
    }

    private void ShowUI()
    {
        if (CombatManager.Instance is { IsOverOrEnding: false })
        {
            GD.Print("战斗中无法打开无限升级系统。");
            return;
        }
        if (RunManager.Instance?.DebugOnlyGetState() == null)
        {
            GD.Print("没有正在进行的游戏，无法打开升级系统。");
            return;
        }
        _isOpen = true;
        RefreshPointsLabel();
        if (_searchBox != null) _searchBox.Text = "";
        SetUIVisible(true);
        ResizeBackground();
        CenterMainPanel();
    }

    public void HideUI()
    {
        _isOpen = false;
        SetUIVisible(false);
    }

    public void SetUIVisible(bool visible)
    {
        Visible = visible;
        MouseFilter = visible ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
    }

    public void RefreshPointsLabel()
    {
        if (_pointsLabel != null)
            _pointsLabel.Text = $"当前点数: {UpgradePointManager.CurrentPoints}";
    }

    private void OnCloseClicked() => HideUI();

    private void ResizeBackground()
    {
        if (_background != null)
            _background.Size = GetViewportRect().Size;
    }

    private void CenterMainPanel()
    {
        if (_mainPanel == null) return;
        var vpSize = GetViewportRect().Size;
        _mainPanel.Position = new Vector2(
            (vpSize.X - PanelWidth) / 2,
            (vpSize.Y - PanelHeight) / 2);
    }
}
