using System;
using System.Threading.Tasks;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// UI 中单个加点操作项的数据模型。
/// </summary>
public record UpgradeItemData(
    string Category,       // 分类名："卡牌操作" / "能力操作" / "牌组操作" / "测试操作"
    string DisplayName,    // 显示名称："升级卡牌" / "攻击+1" 等
    int Cost,              // 点数消耗，0 表示免费
    Func<Task> OnClick     // 点击处理函数（async）
)
{
    /// <summary>按钮标签文本。</summary>
    public string ButtonLabel => Cost > 0
        ? $"{DisplayName} [{Cost}点]"
        : DisplayName;

    /// <summary>用于搜索匹配（分类 + 名称）。</summary>
    public string SearchText => $"{Category} {DisplayName}".ToLower();
}
