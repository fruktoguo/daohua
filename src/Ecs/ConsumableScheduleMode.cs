namespace DaoHua.Ecs;

/// <summary>
/// 自动消耗品判定调度模式。缺省 <see cref="PeriodicTick"/> 保持现状生产行为；
/// <see cref="EventDriven"/> 为压测/原型开关：同一输入下判定时机不同会改变
/// 消耗品生效时刻，属于有意语义分叉而非位级兼容路径。
/// </summary>
public enum ConsumableScheduleMode
{
    /// <summary>现状：会话每 5 秒独立时钟轮询、单人每资源边界窗口判定（含战外相位）。</summary>
    PeriodicTick = 0,
    /// <summary>事件驱动：战斗相位每个自然边界结算后判定并级联收敛，冷却/持续到期精确唤醒，战外不判定。</summary>
    EventDriven = 1
}
