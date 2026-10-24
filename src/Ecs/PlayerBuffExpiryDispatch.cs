using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>资源自然边界与离线封顶共用的同步 Buff 到期裁决。</summary>
public interface IExpirePlayerBuffs : ISystemTag;

/// <summary>常驻到期信箱；只携带同步请求时刻与变更结果，不进入存档。</summary>
public sealed class PlayerBuffExpiryInboxComponent : YuoComponent
{
    /// <summary>本次到期裁决的时刻（Unix 秒）。</summary>
    public double AtUnixSeconds { get; internal set; }
    /// <summary>本次裁决是否真实删除了 Buff；由 Tag System 写回，调用方读取后复位。</summary>
    public bool Changed { get; internal set; }
}
