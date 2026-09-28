namespace DaoHua.Ecs;

/// <summary>
/// 消融实验开关（TASK-20260927-07）：仅基准归因使用，通过环境变量逐项关闭优化版宿主优化。
/// 所有开关默认启用，生产与常规开发不受任何影响；两版变体共用此类型，基线构建不读取。
/// </summary>
public static class EcsAblation
{
    private static bool ReadSwitch(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is null || (bool.TryParse(value, out var enabled) && enabled);
    }

    /// <summary>行动管线内合并连续自然边界；关闭后每边界回到完整 ECS Tick。</summary>
    public static readonly bool BulkAdvance = ReadSwitch("DAOHUA_ECS_OPT_BULK");

    /// <summary>系统 tick 前将扫描集收敛为当前玩家与遭遇实体；关闭后系统遍历全体实体。</summary>
    public static readonly bool ActiveEntities = ReadSwitch("DAOHUA_ECS_OPT_ACTIVE");

    /// <summary>上下文与管线构造期缓存组件引用；关闭后每次访问重新走组件存储查询。</summary>
    public static readonly bool ComponentCache = ReadSwitch("DAOHUA_ECS_OPT_CACHE");

    /// <summary>玩家加载并行执行；关闭后在优化版恢复基线全局加载门（加载+首次模拟串行）。</summary>
    public static readonly bool ParallelLoad = ReadSwitch("DAOHUA_ECS_OPT_PARALLELLOAD");
}
