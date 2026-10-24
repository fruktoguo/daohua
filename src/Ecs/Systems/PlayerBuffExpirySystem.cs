using DaoHua.Ecs.Components;
using Domain.Player;
using YuoCore.Ecs;

namespace DaoHua.Ecs.Systems;

/// <summary>在边界开始时删除到期增益；保留剩余 Buff 顺序，只有真实删除才使属性缓存失效与分段标脏。</summary>
public sealed class PlayerBuffExpirySystem
    : YuoSystem<PlayerBuffExpiryInboxComponent, PlayerBuffComponent, PlayerIdentityComponent>, IExpirePlayerBuffs
{
    /// <summary>Tag 派发入口：删除到期增益并写回变更结果；有真实删除才标脏 Buffs 分段。</summary>
    public override void Run(PlayerBuffExpiryInboxComponent inbox, PlayerBuffComponent buffs, PlayerIdentityComponent identity)
    {
        inbox.Changed = buffs.Active.RemoveAll(buff => buff.ExpiresAtUnixSeconds <= inbox.AtUnixSeconds) > 0;
        if (inbox.Changed) identity.MarkDirty(PlayerSectionKind.Buffs);
    }
}
