using System.Text.Json.Serialization;

namespace Domain.Player;

/// <summary>
/// 强化单等级胜负记录：Attempts 为总尝试次数，Successes 为成功次数。
/// </summary>
/// <param name="Attempts">总尝试次数（≥0）</param>
/// <param name="Successes">成功次数（≥0 且 ≤ Attempts）</param>
public sealed record EnhancementLevelStat(
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("successes")] int Successes)
{
    /// <summary>实际成功率（0.0 ~ 1.0）；无尝试时返回 0.0。</summary>
    [JsonIgnore]
    public double SuccessRate => Attempts > 0 ? (double)Successes / Attempts : 0.0;
}

/// <summary>
/// 单个行动或批次的装备分级强化暂存记录（服务端暂存池单元）。
/// </summary>
public sealed class ActionEnhancementRecord
{
    /// <summary>所属强化行动实例 ID</summary>
    [JsonPropertyName("actionId")]
    public Guid ActionId { get; set; }

    /// <summary>强化目标装备 ID（如 equipment.qingyun-sword）</summary>
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>分级统计明细：键为目标等级 1..20</summary>
    [JsonPropertyName("levels")]
    public Dictionary<int, EnhancementLevelStat> Levels { get; set; } = new();

    /// <summary>行动是否已完成（出队或达到目标等级）；false 表示仍在队列执行中</summary>
    [JsonPropertyName("isCompleted")]
    public bool IsCompleted { get; set; }

    /// <summary>最后更新时刻（Unix 秒）</summary>
    [JsonPropertyName("updatedAtUnixSeconds")]
    public double UpdatedAtUnixSeconds { get; set; }

    /// <summary>深拷贝当前记录副本</summary>
    public ActionEnhancementRecord Clone()
    {
        var clone = new ActionEnhancementRecord
        {
            ActionId = ActionId,
            ItemId = ItemId,
            IsCompleted = IsCompleted,
            UpdatedAtUnixSeconds = UpdatedAtUnixSeconds,
            Levels = new Dictionary<int, EnhancementLevelStat>(Levels.Count)
        };
        foreach (var (k, v) in Levels)
        {
            clone.Levels[k] = new EnhancementLevelStat(v.Attempts, v.Successes);
        }
        return clone;
    }
}
