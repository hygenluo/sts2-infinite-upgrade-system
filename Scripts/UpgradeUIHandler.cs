using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using InfiniteUpgradeSystem.Multiplayer;
using InfiniteUpgradeSystem.UiComponents;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 加点面板主控制器（UI v3：游戏原生字体 MegaLabel + 冷灰蓝/金配色 + 属性面板排版）。
///
/// 视图：Main（基础属性/能力/技能/牌组入口/测试）↔ CardOps（牌组操作子面板），整页切换。
/// 所有文本节点由 `UpgradeTheme` 工厂创建 —— 即游戏自带 `MegaLabel` / `MegaRichTextLabel`
/// （自带语言字体替换与字号自适应），装饰性符号由 `UiIcons` 矢量绘制。
/// </summary>
public sealed partial class UpgradeUIHandler : Control
{
    private const Key ToggleHotkey = Key.P;
    private const float PanelWidth = 720f;
    private const float PanelHeight = 640f;
    private const float TopBarFallbackHeight = 40f;

    private static UpgradeUIHandler? s_instance;
    private static CanvasLayer? s_canvasLayer;
    public static CanvasLayer? CanvasLayer => s_canvasLayer;

    /// <summary>面板视图（CardOps = 牌组子面板）。</summary>
    private enum UiView { Main, CardOps }

    private UiView _currentView = UiView.Main;

    // UI 组件
    private ColorRect? _background;
    private Panel? _mainPanel;
    private UpgradeTopBar? _topBar;
    private LineEdit? _searchBox;
    private ScrollContainer? _scrollContainer;
    private VBoxContainer? _scrollContent;
    private ScrollContainer? _cardOpsScroll;
    private VBoxContainer? _cardOpsContent;
    private Button? _deckEntryRow;
    private MegaLabel? _deckCountLabel;
    private MegaLabel? _emptyLabel;
    private MegaLabel? _cardOpsEmptyLabel;
    private ClassTabBar? _skillTabBar;
    private MegaLabel? _hintLabel;
    private readonly Dictionary<string, VBoxContainer> _skillClassBoxes = new();
    private readonly Dictionary<UpgradeItemDef, CollapsibleSection> _sectionOf = new();
    private readonly Dictionary<CollapsibleSection, string> _sectionCategory = new();
    private readonly Dictionary<UpgradeItemDef, UpgradeItemRow> _cardOpsRows = new();
    private Tween? _panelTween;
    private bool _isOpen;
    private bool _built;
    private int _localeWaitFrames;

    /// <summary>面板位置记忆（运行期间跨打开/关闭保持；NaN = 尚未拖拽，使用居中）。</summary>
    private static Vector2 s_panelPosition = new(float.NaN, float.NaN);

    // 拖拽
    private bool _isDragging;
    private Vector2 _dragOffset;

    // 操作项数据 + 控件映射
    private readonly List<UpgradeItemDef> _allItems = new();
    private readonly Dictionary<UpgradeItemDef, UpgradeItemRow> _itemControls = new();

    public static void CreateInstance()
    {
        if (s_instance != null) return;
        s_instance = new UpgradeUIHandler { Name = "InfiniteUpgradeUI" };
        s_canvasLayer = new CanvasLayer { Name = "InfiniteUpgradeCanvasLayer", Layer = 128 };
        s_canvasLayer.AddChild(s_instance);
        var tree = (SceneTree?)Engine.GetMainLoop();
        tree?.Root?.CallDeferred(Node.MethodName.AddChild, s_canvasLayer);
    }

    public static UpgradeUIHandler? Instance => s_instance;

    public override void _Ready()
    {
        PopulateItems();
        // 字体语言替换（zhs → 思源宋体等）依赖游戏本地化系统：LocManager.Initialize() 是在
        // ModManager 加载完模组**之后**才执行的，因此本节点首次 _Ready 时可能尚不可用。
        // 此时不建 UI（否则中文界面会拿无 CJK 字形的 Kreon 渲染成缺字方框），下一帧重试。
        if (!UpgradeTheme.LocaleReady && _localeWaitFrames < MaxLocaleWaitFrames)
        {
            _localeWaitFrames++;
            CallDeferred(nameof(BuildWhenLocaleReady));
            return;
        }
        FinishBuild();
    }

    /// <summary>等待本地化字体就绪的帧数上限（≈10 秒），超过后按当前可用字体建 UI。</summary>
    private const int MaxLocaleWaitFrames = 600;

    private void BuildWhenLocaleReady()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        if (!UpgradeTheme.LocaleReady && _localeWaitFrames < MaxLocaleWaitFrames)
        {
            _localeWaitFrames++;
            CallDeferred(nameof(BuildWhenLocaleReady));
            return;
        }
        FinishBuild();
    }

    private void FinishBuild()
    {
        if (_built) return;
        _built = true;
        BuildUI();
        SetUIVisible(false);
        Log.Info($"InfiniteUpgradeUI: ready (v3 mega-label theme, localeReady={UpgradeTheme.LocaleReady}).");
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

        // 快捷键。选牌进行中（面板隐藏但 _isOpen 保持）不拦截按键，
        // 让游戏选牌界面的 Esc/返回图标能正常取消选牌（取消链路）。
        if (@event is InputEventKey { Pressed: true, Keycode: ToggleHotkey, Echo: false } && !IsSelectionInProgress)
        {
            GetViewport().SetInputAsHandled();
            ToggleUI();
        }
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape, Echo: false } && _isOpen && Visible)
        {
            GetViewport().SetInputAsHandled();
            HideUI();
        }
        // 选牌进行中按 Esc：游戏自身 Esc 打开主菜单（不取消选牌），
        // 这里程序化取消选牌屏 → 退款并恢复面板
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape, Echo: false } && IsSelectionInProgress
            && CardOperationHelper.TryCancelActiveCardSelection())
        {
            GetViewport().SetInputAsHandled();
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
                    s_panelPosition = _mainPanel.Position; // 记忆拖拽后的位置
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

    /// <summary>鼠标是否在标题栏区域（顶栏高度内；排除关闭按钮/点数徽标区域）。</summary>
    private bool IsMouseInTitleBar()
    {
        if (_mainPanel == null) return false;
        var mousePos = GetGlobalMousePosition();
        var panelPos = _mainPanel.GlobalPosition;
        var barHeight = _topBar?.Size.Y ?? TopBarFallbackHeight;
        var inBar = mousePos.X >= panelPos.X && mousePos.X <= panelPos.X + PanelWidth
            && mousePos.Y >= panelPos.Y && mousePos.Y <= panelPos.Y + barHeight;
        if (!inBar) return false;
        // 关闭按钮在顶栏内：排除其区域，否则点击被拖拽逻辑吞掉（✕ 无反应）
        var closeRect = _topBar?.CloseButton?.GetGlobalRect();
        return closeRect == null || !closeRect.Value.HasPoint(mousePos);
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

        // 全屏遮罩
        _background = new ColorRect
        {
            Color = UpgradeTheme.Backdrop,
            MouseFilter = MouseFilterEnum.Stop,
        };
        _background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_background);

        // 主面板（冷灰蓝底 + 1px 描边 + 投影）
        _mainPanel = new Panel
        {
            CustomMinimumSize = new Vector2(PanelWidth, PanelHeight),
            MouseFilter = MouseFilterEnum.Stop,
        };
        _mainPanel.AddThemeStyleboxOverride("panel", UpgradeTheme.PanelStylebox());
        AddChild(_mainPanel);

        var outerVBox = new VBoxContainer
        {
            Position = Vector2.Zero,
            Size = new Vector2(PanelWidth, PanelHeight),
        };
        outerVBox.AddThemeConstantOverride("separation", 0);
        _mainPanel.AddChild(outerVBox);

        // ── 顶部栏（固定不滚动）──
        _topBar = new UpgradeTopBar(
            UpgradeLoc.Get(UpgradeLoc.UiTitle, "无限升级系统"), OnCloseClicked);
        outerVBox.AddChild(_topBar);

        // ── 内容区（统一内边距）──
        var contentMargin = new MarginContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        contentMargin.AddThemeConstantOverride("margin_left", 14);
        contentMargin.AddThemeConstantOverride("margin_right", 14);
        contentMargin.AddThemeConstantOverride("margin_top", 12);
        contentMargin.AddThemeConstantOverride("margin_bottom", 10);
        outerVBox.AddChild(contentMargin);

        var content = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 8);
        contentMargin.AddChild(content);

        // ── 搜索框 ──
        _searchBox = new LineEdit
        {
            PlaceholderText = UpgradeLoc.Get(UpgradeLoc.UiSearchPlaceholder, "搜索加点项目…"),
            ClearButtonEnabled = true,
            CustomMinimumSize = new Vector2(0, 30),
        };
        // LineEdit 的输入文字与占位文字是两套独立主题项，都要覆盖游戏字体，
        // 否则占位文字会落到 Godot 内置主题的小字号字体上。
        UpgradeTheme.ApplyGameFont(_searchBox, fontSize: 14);
        _searchBox.AddThemeFontOverride("font_placeholder", UpgradeTheme.FontRegular);
        _searchBox.AddThemeFontSizeOverride("font_placeholder_size", 14);
        _searchBox.AddThemeColorOverride("font_color", UpgradeTheme.TextMain);
        _searchBox.AddThemeColorOverride("font_placeholder_color", UpgradeTheme.TextSecondary);
        _searchBox.AddThemeColorOverride("caret_color", UpgradeTheme.Gold);
        _searchBox.AddThemeStyleboxOverride("normal", UpgradeTheme.InputStylebox(false));
        _searchBox.AddThemeStyleboxOverride("focus", UpgradeTheme.InputStylebox(true));
        _searchBox.TextChanged += OnSearchTextChanged;
        content.AddChild(_searchBox);

        // ── 滚动区（主视图）──
        _scrollContainer = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
        };
        _scrollContent = new VBoxContainer
        {
            // ScrollContainer 子节点必须显式撑宽，否则内容按最小宽度收缩，
            // 行内 ExpandFill 名称标签会被压到 0 宽（症状：只有数字/加号可见）
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _scrollContent.AddThemeConstantOverride("separation", 6);
        _scrollContainer.AddChild(_scrollContent);
        content.AddChild(_scrollContainer);

        // ── 滚动区（牌组子面板）──
        _cardOpsScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
        };
        _cardOpsContent = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _cardOpsContent.AddThemeConstantOverride("separation", 3);
        _cardOpsScroll.AddChild(_cardOpsContent);
        content.AddChild(_cardOpsScroll);
        _cardOpsScroll.Visible = false;

        // ── 页脚（快捷键提示 / 只读提示）──
        content.AddChild(UpgradeTheme.Divider());
        var footer = new HBoxContainer();
        _hintLabel = UpgradeTheme.Label(
            UpgradeLoc.Get(UpgradeLoc.UiHint, "[P] 打开/关闭    [Esc] 关闭"),
            11, UpgradeTheme.TextSecondary);
        _hintLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(_hintLabel);
        content.AddChild(footer);

        // 生成牌组子面板 + 主视图分区（默认全部收起）+ 空态标签
        BuildCardOpsView();
        BuildSections();
        _emptyLabel = BuildEmptyLabel();
        _scrollContent!.AddChild(_emptyLabel);
    }

    /// <summary>搜索空态标签（未找到相关项目）。</summary>
    private static MegaLabel BuildEmptyLabel()
    {
        var label = UpgradeTheme.Label(
            UpgradeLoc.Get(UpgradeLoc.UiEmpty, "未找到相关项目"), 13, UpgradeTheme.TextSecondary,
            align: HorizontalAlignment.Center);
        label.Visible = false;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    /// <summary>
    /// 牌组子面板：返回行 + 卡牌操作 + 牌组操作（操作优先，选中后调起游戏选牌界面）。
    /// 复用 UpgradeItemRow（Action 型渲染）。
    /// </summary>
    private void BuildCardOpsView()
    {
        var back = UpgradeTheme.TextButton(
            UpgradeLoc.Get(UpgradeLoc.UiBack, "← 返回主面板"), 14, UpgradeTheme.Gold,
            () => SwitchView(UiView.Main), minHeight: 30);
        UpgradeTheme.StyleButton(back, flat: true);
        var backLabel = UpgradeTheme.ButtonLabel(back);
        if (backLabel != null) backLabel.HorizontalAlignment = HorizontalAlignment.Left;
        back.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _cardOpsContent!.AddChild(back);

        _cardOpsContent.AddChild(UpgradeTheme.Divider());

        var zebra = false;
        foreach (var item in _allItems.Where(i => i.Category is "卡牌操作" or "牌组操作"))
        {
            var row = new UpgradeItemRow(item, zebra);
            zebra = !zebra;
            row.Purchased += OnPurchaseSucceeded;
            _itemControls[item] = row;
            _cardOpsRows[item] = row;
            _cardOpsContent.AddChild(row);
        }

        _cardOpsEmptyLabel = BuildEmptyLabel();
        _cardOpsContent.AddChild(_cardOpsEmptyLabel);
    }

    /// <summary>选牌进行中：面板隐藏但 _isOpen 保持（此时不响应 P/Esc，避免抢走游戏选牌界面的按键）。</summary>
    private bool IsSelectionInProgress => _isOpen && !Visible;

    /// <summary>主视图 ↔ 牌组子面板整页切换（只切换可见性，视图状态天然保持）。</summary>
    private void SwitchView(UiView view)
    {
        if (_currentView == view) return;
        _currentView = view;
        _scrollContainer!.Visible = view == UiView.Main;
        _cardOpsScroll!.Visible = view == UiView.CardOps;
        if (_searchBox != null) _searchBox.Text = "";
        ResetRowsFilter(view);
        RefreshAllRows();
    }

    /// <summary>只读模式（局内查看）：行隐藏加号、牌组入口禁用、顶栏显示只读徽标。</summary>
    private void SetReadOnlyMode(bool readOnly)
    {
        foreach (var row in _itemControls.Values)
            row.ReadOnly = readOnly;
        if (_deckEntryRow != null)
            _deckEntryRow.Disabled = readOnly;
        _topBar?.SetReadOnly(readOnly);
        if (_hintLabel != null)
            _hintLabel.Text = readOnly
                ? UpgradeLoc.Get(UpgradeLoc.UiHintReadOnly, "战斗中 · 只读模式    [Esc] 关闭")
                : UpgradeLoc.Get(UpgradeLoc.UiHint, "[P] 打开/关闭    [Esc] 关闭");
    }

    /// <summary>重置两视图全部行：可见 + 高亮清除 + 空态隐藏（打开面板时调用）。</summary>
    private void ResetAllRowsFilter()
    {
        foreach (var row in _itemControls.Values)
        {
            row.Visible = true;
            row.SetSearchHighlight(null);
        }
        if (_emptyLabel != null) _emptyLabel.Visible = false;
        if (_cardOpsEmptyLabel != null) _cardOpsEmptyLabel.Visible = false;
    }

    /// <summary>
    /// 进入视图时重置该视图所有行可见 —— 搜索框文本可能已是空串（TextChanged 不触发），
    /// 行的过滤状态必须显式复位，保证每次进入子面板都是完整列表。
    /// </summary>
    private void ResetRowsFilter(UiView view)
    {
        if (view == UiView.CardOps)
        {
            foreach (var row in _cardOpsRows.Values) row.Visible = true;
        }
        else
        {
            foreach (var (item, row) in _itemControls)
                if (!_cardOpsRows.ContainsKey(item)) row.Visible = true;
        }
    }

    /// <summary>
    /// 分区构建：基础属性 / 能力（Stat 条目）、技能（职业标签页）、牌组（入口行）、
    /// 测试（弱化样式）。全部默认收起。
    /// </summary>
    private void BuildSections()
    {
        var baseSection = AddSection("基础属性");
        AddStatRows(baseSection, "基础属性");

        var abilitySection = AddSection("能力");
        AddStatRows(abilitySection, "能力");

        // 技能：职业标签页 → 各职业条目容器（默认选中「通用」）
        var skillSection = AddSection("技能");
        _skillTabBar = new ClassTabBar();
        skillSection.Content.AddChild(_skillTabBar);
        foreach (var cls in ClassTabBar.Classes)
        {
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 3);
            skillSection.Content.AddChild(box);
            _skillClassBoxes[cls] = box;
            box.Visible = cls == ClassTabBar.Generic;
        }
        _skillTabBar.ClassSelected += cls =>
        {
            foreach (var (key, box) in _skillClassBoxes)
                box.Visible = key == cls;
        };
        var zebra = false;
        foreach (var item in _allItems.Where(i => i.Category == "技能"))
        {
            var row = CreateRow(item, skillSection, zebra);
            zebra = !zebra;
            _skillClassBoxes[item.SubCategory!].AddChild(row);
        }

        // 牌组：入口行（点击进入子面板）
        BuildDeckEntryRow();

        // 测试：弱化样式
        var testSection = AddSection("测试操作", weakStyle: true);
        foreach (var item in _allItems.Where(i => i.Category == "测试操作"))
            testSection.Content.AddChild(CreateRow(item, testSection));
    }

    private CollapsibleSection AddSection(string category, bool weakStyle = false)
    {
        var section = new CollapsibleSection(
            UpgradeLoc.SectionTitle(category), collapsed: true, weakStyle: weakStyle);
        _sectionCategory[section] = category;
        _scrollContent!.AddChild(section);
        return section;
    }

    private void AddStatRows(CollapsibleSection section, string category)
    {
        var zebra = false;
        foreach (var item in _allItems.Where(i => i.Category == category))
        {
            section.Content.AddChild(CreateRow(item, section, zebra));
            zebra = !zebra;
        }
    }

    private UpgradeItemRow CreateRow(UpgradeItemDef item, CollapsibleSection section, bool zebra = false)
    {
        var row = new UpgradeItemRow(item, zebra);
        row.Purchased += OnPurchaseSucceeded;
        _itemControls[item] = row;
        _sectionOf[item] = section;
        return row;
    }

    /// <summary>牌组入口行：图标 + 名称 + 卡牌数 + 右箭头（点击进入子面板）。</summary>
    private void BuildDeckEntryRow()
    {
        // 同 CollapsibleSection：无文本按钮必须显式给高度，否则文字溢出、点击无法命中
        _deckEntryRow = new Button
        {
            Flat = true,
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 34),
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        _deckEntryRow.AddThemeStyleboxOverride("normal", UpgradeTheme.RowStylebox(false, false));
        _deckEntryRow.AddThemeStyleboxOverride("hover", UpgradeTheme.RowStylebox(false, true));
        _deckEntryRow.AddThemeStyleboxOverride("pressed", UpgradeTheme.RowStylebox(false, true));
        _deckEntryRow.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _deckEntryRow.AddThemeStyleboxOverride("disabled",
            UpgradeTheme.RowStylebox(false, false));
        UpgradeTheme.ApplyGameFont(_deckEntryRow);

        var rowBox = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        rowBox.AddThemeConstantOverride("separation", 8);

        var chevron = new UiIcons.Chevron { IconColor = UpgradeTheme.Gold, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        rowBox.AddChild(chevron);

        var name = UpgradeTheme.Label(UpgradeLoc.SectionTitle("牌组"), 15, UpgradeTheme.Gold, bold: true);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rowBox.AddChild(name);

        _deckCountLabel = UpgradeTheme.Label("", 13, UpgradeTheme.TextSecondary,
            align: HorizontalAlignment.Right);
        rowBox.AddChild(_deckCountLabel);

        rowBox.AddChild(new UiIcons.ArrowRight
        {
            IconColor = UpgradeTheme.Gold,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        });

        _deckEntryRow.AddChild(rowBox);
        rowBox.SetAnchorsPreset(LayoutPreset.FullRect);
        _deckEntryRow.Pressed += () => SwitchView(UiView.CardOps);
        _scrollContent!.AddChild(_deckEntryRow);
    }

    // ═══════════════════════════════════════════════════════════════
    // 搜索
    // ═══════════════════════════════════════════════════════════════

    private void OnSearchTextChanged(string text)
    {
        var filter = text.Trim().ToLower();

        if (_currentView == UiView.CardOps)
        {
            UpdateFilteredList(_cardOpsRows, filter, _cardOpsEmptyLabel, _cardOpsScroll);
            return;
        }

        // 主视图：分区过滤 + 自动展开命中分区
        var matchesInSection = new HashSet<CollapsibleSection>();
        UpgradeItemRow? firstMatch = null;
        foreach (var item in _allItems)
        {
            if (!_itemControls.TryGetValue(item, out var row)) continue;
            var match = string.IsNullOrEmpty(filter) || item.SearchText.Contains(filter);
            row.Visible = match;
            row.SetSearchHighlight(match ? filter : null);
            if (match)
            {
                firstMatch ??= row;
                if (_sectionOf.TryGetValue(item, out var section))
                    matchesInSection.Add(section);
            }
        }
        if (!string.IsNullOrEmpty(filter))
        {
            foreach (var section in matchesInSection)
                if (section.Collapsed) section.SetCollapsed(false);
        }
        // 技能分区：搜索时显示含命中的职业容器（无搜索词时跟随选中标签）
        if (_skillClassBoxes.Count > 0)
        {
            foreach (var (cls, box) in _skillClassBoxes)
            {
                var anyVisible = box.GetChildren().OfType<UpgradeItemRow>().Any(r => r.Visible);
                box.Visible = string.IsNullOrEmpty(filter)
                    ? cls == (_skillTabBar?.SelectedClass ?? ClassTabBar.Generic)
                    : anyVisible;
            }
        }
        var totalVisible = _itemControls.Count(kv => !_cardOpsRows.ContainsKey(kv.Key) && kv.Value.Visible);
        _emptyLabel!.Visible = !string.IsNullOrEmpty(filter) && totalVisible == 0;
        ScrollToFirstMatch(firstMatch, _scrollContainer);
    }

    /// <summary>统一的行过滤/高亮/空态/滚动定位（子面板用）。</summary>
    private void UpdateFilteredList(
        Dictionary<UpgradeItemDef, UpgradeItemRow> rows, string filter,
        MegaLabel? emptyLabel, ScrollContainer? scroll)
    {
        UpgradeItemRow? firstMatch = null;
        var visible = 0;
        foreach (var (item, row) in rows)
        {
            var match = string.IsNullOrEmpty(filter) || item.SearchText.Contains(filter);
            row.Visible = match;
            row.SetSearchHighlight(match ? filter : null);
            if (match)
            {
                visible++;
                firstMatch ??= row;
            }
        }
        if (emptyLabel != null)
            emptyLabel.Visible = !string.IsNullOrEmpty(filter) && visible == 0;
        ScrollToFirstMatch(firstMatch, scroll);
    }

    /// <summary>滚动定位到首个命中行（延迟一帧，等分区展开动画开始后定位）。</summary>
    private void ScrollToFirstMatch(UpgradeItemRow? firstMatch, ScrollContainer? scroll)
    {
        if (firstMatch == null || scroll == null) return;
        CallDeferred(nameof(DeferredScrollTo), firstMatch, scroll);
    }

    private void DeferredScrollTo(UpgradeItemRow row, ScrollContainer scroll)
    {
        if (GodotObject.IsInstanceValid(row) && GodotObject.IsInstanceValid(scroll))
            scroll.EnsureControlVisible(row);
    }

    // ═══════════════════════════════════════════════════════════════
    // 操作项注册表
    // 显示名走本地化（UpgradeLoc.ResolveDisplayName），搜索语料含中英双语。
    // ═══════════════════════════════════════════════════════════════

    private void PopulateItems()
    {
        // === 基础属性 (8)：开局生效的数值属性 ===
        _allItems.Add(StatItem("基础属性", "初始力量+1", UpgradeLoc.ItemBaseStrength, 10,
            () => AbilityOperationHelper.GetBoost("strength"), Purchase("strength", 10)));
        _allItems.Add(StatItem("基础属性", "初始敏捷+1", UpgradeLoc.ItemBaseDexterity, 10,
            () => AbilityOperationHelper.GetBoost("dexterity"), Purchase("dexterity", 10)));
        _allItems.Add(StatItem("基础属性", "初始集中+1", UpgradeLoc.ItemBaseFocus, 15,
            () => AbilityOperationHelper.GetBoost("focus"), Purchase("focus", 15)));
        _allItems.Add(StatItem("基础属性", "初始覆甲+1", UpgradeLoc.ItemBasePlating, 8,
            () => AbilityOperationHelper.GetBoost("plating"), Purchase("plating", 8)));
        _allItems.Add(StatItem("基础属性", "初始荆棘+1", UpgradeLoc.ItemBaseThorns, 10,
            () => AbilityOperationHelper.GetBoost("thorns"), Purchase("thorns", 10)));
        _allItems.Add(StatItem("基础属性", "初始人工制品+1", UpgradeLoc.ItemBaseArtifact, 20,
            () => AbilityOperationHelper.GetBoost("artifact"), Purchase("artifact", 20)));
        _allItems.Add(StatItem("基础属性", "生命上限+1", UpgradeLoc.ItemHp, 5,
            () => AbilityOperationHelper.GetBoost("hp"), PurchaseImmediate("hp", 5)));
        _allItems.Add(StatItem("基础属性", "充能球栏位+1", UpgradeLoc.ItemOrbSlot, 10,
            () => AbilityOperationHelper.GetBoost("orbSlot"), PurchaseImmediate("orbSlot", 10)));

        // === 能力 (6)：行为类被动 ===
        _allItems.Add(StatItem("能力", "每回合能量+1", UpgradeLoc.ItemEnergyPerTurn, 30,
            () => AbilityOperationHelper.GetBoost("energy"), PurchaseImmediate("energy", 30)));
        _allItems.Add(StatItem("能力", "每回合辉星+1", UpgradeLoc.ItemStarsPerTurn, 15,
            () => AbilityOperationHelper.GetBoost("stars"), Purchase("stars", 15)));
        _allItems.Add(StatItem("能力", "每回合铸造+5", UpgradeLoc.ItemForgePerTurn, 15,
            () => AbilityOperationHelper.GetBoost("forge"), Purchase("forge", 15)));
        _allItems.Add(StatItem("能力", "格挡跨回合不消失", UpgradeLoc.ItemBlockKeep, 25,
            () => AbilityOperationHelper.GetBoost("blockKeep"), Purchase("blockKeep", 25), maxLevel: 1));
        _allItems.Add(StatItem("能力", "能量跨回合不消失", UpgradeLoc.ItemEnergyKeep, 25,
            () => AbilityOperationHelper.GetBoost("energyKeep"), Purchase("energyKeep", 25), maxLevel: 1));
        _allItems.Add(StatItem("能力", "每当你击败一名敌人时，获取20金币", UpgradeLoc.ItemGoldOnKill, 7,
            () => AbilityOperationHelper.GetBoost("gold_on_kill"), Purchase("gold_on_kill", 7), maxLevel: 1));

        // === 卡牌操作：牌组子面板 ===
        _allItems.Add(ActionItem("卡牌操作", "升级卡牌", UpgradeLoc.ItemCardUpgrade, CardOperationHelper.UpgradeCost,
            UpgradeLoc.PromptCardUpgrade, pk => UpgradeCardFlow(pk)));
        _allItems.Add(ActionItem("卡牌操作", "攻击+1", UpgradeLoc.ItemAttackPlus, 1,
            UpgradeLoc.PromptAttackPlus, pk => CardOperationHelper.ModifyDamage(1, pk)));
        _allItems.Add(ActionItem("卡牌操作", "格挡+1", UpgradeLoc.ItemBlockPlus, 1,
            UpgradeLoc.PromptBlockPlus, pk => CardOperationHelper.ModifyBlock(1, pk)));
        _allItems.Add(ActionItem("卡牌操作", "抽牌+1", UpgradeLoc.ItemDrawPlus, 7,
            UpgradeLoc.PromptDrawPlus, pk => CardOperationHelper.ModifyDrawCount(7, pk)));
        _allItems.Add(ActionItem("卡牌操作", "重放+1", UpgradeLoc.ItemReplayPlus, 7,
            UpgradeLoc.PromptReplayPlus, pk => CardOperationHelper.ModifyReplayCount(7, pk)));
        _allItems.Add(ActionItem("卡牌操作", "耗能-1", UpgradeLoc.ItemCostMinus, 12,
            UpgradeLoc.PromptCostMinus, pk => CardOperationHelper.ReduceEnergyCost(12, pk)));
        _allItems.Add(ActionItem("卡牌操作", "添加消耗", UpgradeLoc.ItemAddExhaust, 12,
            UpgradeLoc.PromptAddExhaust, pk => CardOperationHelper.ToggleKeyword(12, CardKeyword.Exhaust, true, pk)));
        _allItems.Add(ActionItem("卡牌操作", "移除消耗", UpgradeLoc.ItemRemoveExhaust, 15,
            UpgradeLoc.PromptRemoveExhaust, pk => CardOperationHelper.ToggleKeyword(15, CardKeyword.Exhaust, false, pk)));
        _allItems.Add(ActionItem("卡牌操作", "添加奇巧", UpgradeLoc.ItemAddSly, 7,
            UpgradeLoc.PromptAddSly, pk => CardOperationHelper.ToggleKeyword(7, CardKeyword.Sly, true, pk)));
        _allItems.Add(ActionItem("卡牌操作", "添加保留", UpgradeLoc.ItemAddRetain, 7,
            UpgradeLoc.PromptAddRetain, pk => CardOperationHelper.ToggleKeyword(7, CardKeyword.Retain, true, pk)));
        _allItems.Add(ActionItem("卡牌操作", "移除保留", UpgradeLoc.ItemRemoveRetain, 7,
            UpgradeLoc.PromptRemoveRetain, pk => CardOperationHelper.ToggleKeyword(7, CardKeyword.Retain, false, pk)));
        _allItems.Add(ActionItem("卡牌操作", "添加固有", UpgradeLoc.ItemAddInnate, 7,
            UpgradeLoc.PromptAddInnate, pk => CardOperationHelper.ToggleKeyword(7, CardKeyword.Innate, true, pk)));
        _allItems.Add(ActionItem("卡牌操作", "移除固有", UpgradeLoc.ItemRemoveInnate, 7,
            UpgradeLoc.PromptRemoveInnate, pk => CardOperationHelper.ToggleKeyword(7, CardKeyword.Innate, false, pk)));
        _allItems.Add(ActionItem("卡牌操作", "添加虚无", UpgradeLoc.ItemAddEthereal, 8,
            UpgradeLoc.PromptAddEthereal, pk => CardOperationHelper.ToggleKeyword(8, CardKeyword.Ethereal, true, pk)));
        _allItems.Add(ActionItem("卡牌操作", "移除虚无", UpgradeLoc.ItemRemoveEthereal, 8,
            UpgradeLoc.PromptRemoveEthereal, pk => CardOperationHelper.ToggleKeyword(8, CardKeyword.Ethereal, false, pk)));
        _allItems.Add(ActionItem("卡牌操作", "添加永恒", UpgradeLoc.ItemAddEternal, 10,
            UpgradeLoc.PromptAddEternal, pk => CardOperationHelper.ToggleKeyword(10, CardKeyword.Eternal, true, pk)));
        _allItems.Add(ActionItem("卡牌操作", "移除永恒", UpgradeLoc.ItemRemoveEternal, 15,
            UpgradeLoc.PromptRemoveEternal, pk => CardOperationHelper.ToggleKeyword(15, CardKeyword.Eternal, false, pk)));
        _allItems.Add(ActionItem("卡牌操作", "为一张卡牌新增附魔", UpgradeLoc.ItemEnchant, 12,
            UpgradeLoc.PromptEnchant, pk => CardOperationHelper.AddEnchantment(12, pk)));

        // === 技能：购买后 MAX 徽标 ===
        _allItems.Add(SkillItem(ClassTabBar.Generic, "每打出1张牌，都获得1格挡", UpgradeLoc.ItemSkillBlockOnPlay, "block_on_play", 10));
        // 计数显示用余数：0/4 → 1/4 → 2/4 → 3/4 → 0/4（触发并归零）
        _allItems.Add(SkillItem(ClassTabBar.Generic, "每当打出4张牌，获得1敏捷", UpgradeLoc.ItemSkillAgilityEvery4Plays, "agility_every_4_plays", 10,
            () => $"{SkillRegistry.CombatPlayCount % 4}/4"));
        _allItems.Add(SkillItem(ClassTabBar.Generic, "每当打出4张牌，获得1力量", UpgradeLoc.ItemSkillStrengthEvery4Plays, "strength_every_4_plays", 10,
            () => $"{SkillRegistry.CombatPlayCount % 4}/4"));
        _allItems.Add(SkillItem(ClassTabBar.Generic, "在你的回合，当你没有手牌时，抽1张牌", UpgradeLoc.ItemSkillDrawWhenNoHand, "draw_when_no_hand", 7));
        _allItems.Add(SkillItem(ClassTabBar.Generic, "你可以在休息处选择任意数量的选项", UpgradeLoc.ItemSkillRestAllOptions, "rest_all_options", 10));
        _allItems.Add(SkillItem(ClassTabBar.Generic, "每回合开始时，获取消耗牌堆数等量格挡", UpgradeLoc.ItemSkillBlockAtTurnStart, "block_at_turn_start", 10));
        _allItems.Add(SkillItem(ClassTabBar.Generic, "你的回合开始时，获取2力量", UpgradeLoc.ItemSkillStrAtTurnStart, "str_at_turn_start", 12));
        _allItems.Add(SkillItem(ClassTabBar.Generic, "回合开始时，对所有敌人造成x点伤害y次", UpgradeLoc.ItemSkillDmgXTimesAtTurnStart, "dmg_x_times_at_turn_start", 15,
            () =>
            {
                var c = CardOperationHelper.GetLocalPlayer()?.Creature;
                int str = c?.GetPower<StrengthPower>()?.Amount ?? 0;
                int dex = c?.GetPower<DexterityPower>()?.Amount ?? 0;
                int x = Math.Max(1, str + 1);
                int y = Math.Max(1, dex + 1);
                return $"{x}伤 {y}次";
            }));
        _allItems.Add(SkillItem(ClassTabBar.Generic, "每当你打出1张能力牌，你的下一张牌免费", UpgradeLoc.ItemSkillFreeNextCardOnPower, "free_next_card_on_power", 10));
        _allItems.Add(SkillItem(ClassTabBar.Ironclad, "每当有一张牌被消耗时，抽一张牌", UpgradeLoc.ItemSkillDrawOnExhaust, "draw_on_exhaust", 10));
        _allItems.Add(SkillItem(ClassTabBar.Ironclad, "每当你消耗1张牌时，给予所有敌人2层易伤", UpgradeLoc.ItemSkillVulnerableOnExhaust, "vulnerable_on_exhaust", 10));
        _allItems.Add(SkillItem(ClassTabBar.Ironclad, "回合开始时，获取所有敌人易伤总和点力量", UpgradeLoc.ItemSkillStrFromVulnerableAtTurnStart, "str_from_vulnerable_at_turn_start", 15));
        _allItems.Add(SkillItem(ClassTabBar.Ironclad, "每当失去生命值时，抽一张牌", UpgradeLoc.ItemSkillDrawOnHpLoss, "draw_on_hp_loss", 10));
        _allItems.Add(SkillItem(ClassTabBar.Ironclad, "回合结束时，你的格挡翻倍", UpgradeLoc.ItemSkillDoubleBlockAtTurnEnd, "double_block_at_turn_end", 15));
        _allItems.Add(SkillItem(ClassTabBar.Ironclad, "你的回合开始时，回复3点生命值", UpgradeLoc.ItemSkillHealAtTurnStart, "heal_at_turn_start", 8));
        _allItems.Add(SkillItem(ClassTabBar.Silent, "每当丢弃一张牌时，给予所有敌人1层虚弱", UpgradeLoc.ItemSkillWeakOnDiscard, "weak_on_discard", 10));
        _allItems.Add(SkillItem(ClassTabBar.Silent, "你每丢弃一张牌，获得1敏捷", UpgradeLoc.ItemSkillDexterityOnDiscard, "dexterity_on_discard", 15));
        _allItems.Add(SkillItem(ClassTabBar.Silent, "你的[小刀]会攻击所有敌人", UpgradeLoc.ItemSkillShivAllEnemies, "shiv_all_enemies", 10));
        _allItems.Add(SkillItem(ClassTabBar.Silent, "每当给予敌人中毒时，获得1格挡", UpgradeLoc.ItemSkillBlockOnPoison, "block_on_poison", 10));
        _allItems.Add(SkillItem(ClassTabBar.Silent, "你每打出一张牌都给予所有敌人一层中毒", UpgradeLoc.ItemSkillPoisonAllOnCardPlay, "poison_all_on_card_play", 15));
        _allItems.Add(SkillItem(ClassTabBar.Silent, "你的回合开始时，额外摸1张牌", UpgradeLoc.ItemSkillDrawAtTurnStart, "draw_at_turn_start", 8));
        _allItems.Add(SkillItem(ClassTabBar.Regent, "每打出1张牌时，铸造1", UpgradeLoc.ItemSkillForgeOnPlay, "forge_on_play", 10));
        _allItems.Add(SkillItem(ClassTabBar.Regent, "每打出1张技能牌时，获得2点活力", UpgradeLoc.ItemSkillVigorOnSkillPlay, "vigor_on_skill_play", 6));
        _allItems.Add(SkillItem(ClassTabBar.Regent, "每铸造一次，君王之剑永久获取1格挡", UpgradeLoc.ItemSkillSovereignBladeBlockOnForge, "sovereign_blade_block_on_forge", 12));
        _allItems.Add(SkillItem(ClassTabBar.Regent, "你的回合开始时，免费打出第一张牌", UpgradeLoc.ItemSkillFreeFirstCard, "free_first_card", 8));
        _allItems.Add(SkillItem(ClassTabBar.Regent, "每当你获得1点辉星，所有敌人本回合失去1点力量", UpgradeLoc.ItemSkillEnemyLoseStrOnStar, "enemy_lose_str_on_star", 15));
        _allItems.Add(SkillItem(ClassTabBar.Regent, "每回合开始时，在手牌中添加一张升级过的无色牌，其本回合可以免费打出", UpgradeLoc.ItemSkillAddColorlessCardAtTurnStart, "add_colorless_card_at_turn_start", 10));
        _allItems.Add(SkillItem(ClassTabBar.Necrobinder, "每打出1张牌时，召唤1", UpgradeLoc.ItemSkillSummonOnPlay, "summon_on_play", 10));
        _allItems.Add(SkillItem(ClassTabBar.Necrobinder, "每打出1张虚无牌，对所有敌人造成3点伤害", UpgradeLoc.ItemSkillDmgOnEtherealPlay, "dmg_on_ethereal_play", 7));
        _allItems.Add(SkillItem(ClassTabBar.Necrobinder, "奥斯提会额外攻击一次", UpgradeLoc.ItemSkillOstyExtraAttack, "osty_extra_attack", 15));
        _allItems.Add(SkillItem(ClassTabBar.Necrobinder, "你的回合开始时，额外召唤2", UpgradeLoc.ItemSkillSummonAtTurnStart, "summon_at_turn_start", 8));
        _allItems.Add(SkillItem(ClassTabBar.Necrobinder, "每当你给予敌人灾厄时，该敌人失去1点力量", UpgradeLoc.ItemSkillEnemyLoseStrOnDoom, "enemy_lose_str_on_doom", 10));
        _allItems.Add(SkillItem(ClassTabBar.Necrobinder, "回合结束时，打出消耗牌堆中的所有虚无牌", UpgradeLoc.ItemSkillPlayEtherealFromExhaust, "play_ethereal_from_exhaust", 15));
        _allItems.Add(SkillItem(ClassTabBar.Defect, "每打出1张能力牌，抽一张牌", UpgradeLoc.ItemSkillDrawOnPowerPlay, "draw_on_power_play", 10));
        _allItems.Add(SkillItem(ClassTabBar.Defect, "回合结束时，每有一个充能球，对所有敌人造成1点伤害", UpgradeLoc.ItemSkillOrbDmgAtTurnEnd, "orb_dmg_at_turn_end", 10));
        _allItems.Add(SkillItem(ClassTabBar.Defect, "回合结束时，消耗你手牌中的状态牌，每张获得2格挡", UpgradeLoc.ItemSkillExhaustStatusGainBlockAtTurnEnd, "exhaust_status_gain_block_at_turn_end", 15));
        _allItems.Add(SkillItem(ClassTabBar.Defect, "每当你抽到能力牌时，自动打出", UpgradeLoc.ItemSkillAutoPlayPowerOnDraw, "auto_play_power_on_draw", 20));
        _allItems.Add(SkillItem(ClassTabBar.Defect, "你的回合开始时，随机获取1个充能球", UpgradeLoc.ItemSkillOrbAtTurnStart, "orb_at_turn_start", 8));
        _allItems.Add(SkillItem(ClassTabBar.Defect, "在你的回合开始时，打出一张随机能力牌", UpgradeLoc.ItemSkillAutoPlayRandomPowerAtTurnStart, "auto_play_random_power_at_turn_start", 10));

        // === 牌组操作 ===
        _allItems.Add(ActionItem("牌组操作", "从牌组删除一张牌", UpgradeLoc.ItemDeckRemove, 20,
            UpgradeLoc.PromptDeckRemove, pk => CardOperationHelper.RemoveCardFromDeck(20, pk)));

        // === 测试操作 ===
        _allItems.Add(TestItem("点数+1", UpgradeLoc.ItemTestP1, 1));
        _allItems.Add(TestItem("点数+5", UpgradeLoc.ItemTestP5, 5));
        _allItems.Add(TestItem("点数+10", UpgradeLoc.ItemTestP10, 10));
    }

    /// <summary>数值型条目构建（含本地化显示名、等级取数、搜索语料）。</summary>
    private UpgradeItemDef StatItem(string category, string fallbackName, string locKey, int cost,
        Func<int> levelProvider, Func<Task<bool>> onClick, int maxLevel = 0) => new()
    {
        Category = category,
        DisplayName = UpgradeLoc.ResolveDisplayName(locKey, fallbackName),
        LocKey = locKey,
        Cost = cost,
        Kind = UpgradeItemKind.Stat,
        MaxLevel = maxLevel,
        LevelProvider = levelProvider,
        ValueText = () => levelProvider().ToString(),
        OnClick = onClick,
        SearchText = BuildSearchText(category, locKey, fallbackName),
    };

    /// <summary>动作型条目构建（卡牌操作，含选牌提示 key；onClick 接收 promptKey 并透传）。</summary>
    private UpgradeItemDef ActionItem(string category, string fallbackName, string locKey, int cost,
        string promptKey, Func<string, Task<bool>> onClick) => new()
    {
        Category = category,
        DisplayName = UpgradeLoc.ResolveDisplayName(locKey, fallbackName),
        LocKey = locKey,
        Cost = cost,
        Kind = UpgradeItemKind.Action,
        PromptKey = promptKey,
        OnClick = () => onClick(promptKey),
        SearchText = BuildSearchText(category, locKey, fallbackName),
    };

    /// <summary>技能条目构建（限等级 MaxLevel=1，按职业 SubCategory 落入标签页）。</summary>
    private UpgradeItemDef SkillItem(string cls, string fallbackName, string locKey, string skillId, int cost,
        Func<string>? valueText = null) => new()
    {
        Category = "技能",
        SubCategory = cls,
        DisplayName = UpgradeLoc.ResolveDisplayName(locKey, fallbackName),
        LocKey = locKey,
        Cost = cost,
        Kind = UpgradeItemKind.Stat,
        MaxLevel = 1,
        LevelProvider = () => SkillRegistry.GetLevel(skillId),
        ValueText = valueText ?? (() => SkillRegistry.Has(skillId)
            ? UpgradeLoc.Get(UpgradeLoc.UiOwned, "已拥有")
            : UpgradeLoc.Get(UpgradeLoc.UiNotOwned, "未拥有")),
        OnClick = async () =>
        {
            var ok = await UpgradePurchaseFlow.EnqueuePurchase(skillId, cost, isSkill: true, isImmediate: false);
            if (ok) RefreshPointsLabel();
            return ok;
        },
        SearchText = BuildSearchText("技能", locKey, fallbackName),
    };

    /// <summary>测试条目构建（加点走同步 action，跨端一致）。</summary>
    private UpgradeItemDef TestItem(string fallbackName, string locKey, int amount) => new()
    {
        Category = "测试操作",
        DisplayName = UpgradeLoc.ResolveDisplayName(locKey, fallbackName),
        LocKey = locKey,
        Cost = 0,
        Kind = UpgradeItemKind.Action,
        OnClick = async () =>
        {
            var ok = await UpgradePurchaseFlow.EnqueueAddPoints(amount);
            if (ok) RefreshPointsLabel();
            return ok;
        },
        SearchText = BuildSearchText("测试操作", locKey, fallbackName),
    };

    /// <summary>能力购买 OnClick（走同步 action，两端一致后刷新点数）。</summary>
    private Func<Task<bool>> Purchase(string key, int cost) => async () =>
    {
        var ok = await UpgradePurchaseFlow.EnqueuePurchase(key, cost, isSkill: false, isImmediate: false);
        if (ok) RefreshPointsLabel();
        return ok;
    };

    /// <summary>能力购买 + 立即生效（hp/energy/orbSlot）。</summary>
    private Func<Task<bool>> PurchaseImmediate(string key, int cost) => async () =>
    {
        var ok = await UpgradePurchaseFlow.EnqueuePurchase(key, cost, isSkill: false, isImmediate: true);
        if (ok) RefreshPointsLabel();
        return ok;
    };

    /// <summary>升级卡牌流程（选牌后入队 CardMod action，跨端一致）。</summary>
    private async Task<bool> UpgradeCardFlow(string promptKey)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return false;
        SetUIVisible(false);
        try
        {
            var card = await CardOperationHelper.SelectCardFromDeck(player, promptKey);
            if (card == null) { SetUIVisible(true); return false; }
            card = CardOperationHelper.EnsureMutableInDeck(player, card); // 写回 deck，确保 identity 可被 action 端匹配
            string identity = CardUpgradeTracker.GetCardIdentity(card, player.Deck.Cards);
            var ok = await UpgradePurchaseFlow.EnqueueCardMod(
                CardOperationHelper.UpgradeCost, identity, (int)CardUpgradeTracker.ModType.Upgrade, "", false);
            RefreshPointsLabel();
            if (ok) { CardOperationHelper.RefreshAllVisuals(player); HideUI(); } else SetUIVisible(true);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error($"Upgrade error: {ex.Message}");
            SetUIVisible(true);
            return false;
        }
    }

    /// <summary>搜索语料：分类 + 中英名称（小写）。</summary>
    private static string BuildSearchText(string category, string? locKey, string fallbackName)
    {
        var zhs = locKey != null ? UpgradeLoc.GetZhs(locKey) : null;
        var eng = locKey != null ? UpgradeLoc.GetEng(locKey) : null;
        return $"{UpgradeLoc.SectionTitle(category)} {category} {zhs ?? fallbackName} {eng ?? ""}".ToLower();
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
        if (RunManager.Instance?.DebugOnlyGetState() == null) return;

        // 战斗中允许打开，但为只读模式（只能查看，不能操作）
        var readOnly = CombatManager.Instance is { IsOverOrEnding: false };
        _isOpen = true;
        SetReadOnlyMode(readOnly);
        RefreshPointsLabel();
        // 打开时显式重置搜索状态（不依赖 TextChanged 事件链）
        ResetAllRowsFilter();
        if (_searchBox != null) _searchBox.Text = "";
        SetUIVisible(true);
        ResizeBackground();
        CenterMainPanel();
        PlayOpenAnimation();
    }

    /// <summary>面板开启动画：淡入 + 上移 16px。</summary>
    private void PlayOpenAnimation()
    {
        _panelTween?.Kill();
        if (_mainPanel == null) return;
        _mainPanel.Modulate = new Color(1, 1, 1, 0);
        _mainPanel.Position += new Vector2(0, 16);
        _panelTween = CreateTween();
        _panelTween.TweenProperty(_mainPanel, "modulate:a", 1f, 0.15f);
        _panelTween.Parallel().TweenProperty(_mainPanel, "position:y", _mainPanel.Position.Y - 16, 0.15f);
    }

    public void HideUI()
    {
        _isOpen = false;
        _panelTween?.Kill();
        if (_mainPanel == null || !Visible)
        {
            SetUIVisible(false);
            return;
        }
        _panelTween = CreateTween();
        _panelTween.TweenProperty(_mainPanel, "modulate:a", 0f, 0.12f);
        _panelTween.Parallel().TweenProperty(_mainPanel, "position:y", _mainPanel.Position.Y + 16, 0.12f);
        _panelTween.TweenCallback(Callable.From(() => SetUIVisible(false)));
    }

    public void SetUIVisible(bool visible)
    {
        Visible = visible;
        MouseFilter = visible ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        // 隐藏时将 CanvasLayer 降到最底层，避免干扰其他 UI 的输入事件
        if (s_canvasLayer != null)
            s_canvasLayer.Layer = visible ? 128 : -1;
    }

    public void RefreshPointsLabel()
    {
        _topBar?.SetPoints(UpgradePointManager.CurrentPoints);
        RefreshAllRows();
    }

    private void OnPurchaseSucceeded() => SpawnPurchaseVfx();

    /// <summary>购买成功反馈：+1 飘字（点数旁上浮淡出）+ 点数计数器跳动。</summary>
    private void SpawnPurchaseVfx()
    {
        if (_topBar == null) return;
        var pointsLabel = _topBar.PointsLabel;

        var floatLabel = UpgradeTheme.Label("+1", 16, UpgradeTheme.HoverGlow, bold: true);
        floatLabel.ZIndex = 100;
        floatLabel.Position = pointsLabel.GlobalPosition + new Vector2(-4, -20);
        AddChild(floatLabel);
        var ft = CreateTween();
        ft.TweenProperty(floatLabel, "position:y", floatLabel.Position.Y - 28, 0.6f);
        ft.Parallel().TweenProperty(floatLabel, "modulate:a", 0f, 0.6f).SetDelay(0.25f);
        ft.TweenCallback(Callable.From(floatLabel.QueueFree));

        var pulse = CreateTween();
        pulse.TweenProperty(pointsLabel, "scale", new Vector2(1.25f, 1.25f), 0.08f);
        pulse.TweenProperty(pointsLabel, "scale", Vector2.One, 0.08f);
    }

    /// <summary>按当前点数刷新所有条目行状态 + 分区摘要 + 牌组入口 + 职业标签计数。</summary>
    private void RefreshAllRows()
    {
        var points = UpgradePointManager.CurrentPoints;
        foreach (var row in _itemControls.Values)
            row.Refresh(points);
        RefreshDeckRow();
        UpdateSectionSummaries();
        UpdateClassTabCounts();
    }

    /// <summary>刷新牌组入口行的卡牌数。</summary>
    private void RefreshDeckRow()
    {
        if (_deckCountLabel == null) return;
        var player = CardOperationHelper.GetLocalPlayer();
        _deckCountLabel.Text = player?.Deck?.Cards != null
            ? UpgradeLoc.Format(UpgradeLoc.UiDeckCount, "{0} 张", player.Deck.Cards.Count)
            : "—";
    }

    /// <summary>分区头右侧摘要：条目数 / 已投入等级 / 技能已拥有数。</summary>
    private void UpdateSectionSummaries()
    {
        foreach (var (section, category) in _sectionCategory)
        {
            var items = _allItems.Where(i => i.Category == category).ToList();
            if (items.Count == 0) { section.SetSummary(""); continue; }

            if (category == "技能")
            {
                int owned = items.Count(i => (i.LevelProvider?.Invoke() ?? 0) > 0);
                section.SetSummary($"{owned}/{items.Count}");
            }
            else
            {
                int levels = items.Sum(i => i.LevelProvider?.Invoke() ?? 0);
                section.SetSummary(levels > 0
                    ? UpgradeLoc.Format(UpgradeLoc.UiSectionSpent, "{0} 项 · 已投入 {1}", items.Count, levels)
                    : UpgradeLoc.Format(UpgradeLoc.UiSectionCount, "{0} 项", items.Count));
            }
        }
    }

    /// <summary>职业标签页计数（已拥有 / 总数）。</summary>
    private void UpdateClassTabCounts()
    {
        if (_skillTabBar == null) return;
        var skills = _allItems.Where(i => i.Category == "技能").ToList();
        foreach (var cls in ClassTabBar.Classes)
        {
            var inClass = skills.Where(i => i.SubCategory == cls).ToList();
            if (inClass.Count == 0) continue;
            int owned = inClass.Count(i => (i.LevelProvider?.Invoke() ?? 0) > 0);
            _skillTabBar.SetCount(cls, owned, inClass.Count);
        }
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
        _mainPanel.Position = float.IsNaN(s_panelPosition.X)
            ? new Vector2((vpSize.X - PanelWidth) / 2, (vpSize.Y - PanelHeight) / 2)
            : s_panelPosition;
        ClampPanelToScreen(); // 防止记忆位置在分辨率变化后出屏
    }
}
