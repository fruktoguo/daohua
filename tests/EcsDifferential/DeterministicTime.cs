using YuoCore.Ecs;
using YuoCore.Ecs.Helpers;

namespace DaoHua.EcsDifferential;

/// <summary>确定性时钟：固定起点，每次 Update 步进 16ms，与真实时间无关。</summary>
public sealed class FixedClock : TimeInfo
{
    public const long BaseMs = 1_700_000_000_000L;

    public FixedClock()
    {
        FrameTime = BaseMs;
    }

    public override void Update()
    {
        FrameTime += 16;
    }
}

/// <summary>
/// 确定性实体 id 生成器：纯递增计数，与两变体内部实现无关。
/// 从 2_000_000 起跳，避开 WorldMain(0) 与默认场景(10000)。
/// </summary>
public sealed class SequentialIdGenerate : IDGenerate
{
    private long _next = 2_000_000L;

    public SequentialIdGenerate() : base(new FixedClock())
    {
    }

    public override long GenerateInstanceId() => _next++;
}
