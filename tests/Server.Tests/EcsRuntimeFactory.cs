using DaoHua.Ecs;
using Domain.Common;
using Domain.GameData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Server.Clean;
using Server;

namespace Server.Tests;

/// <summary>
/// 测试宿主统一装配优化版 ECS 运行时（生产宿主同款内核），供原 OOP 运行时门禁用例复用。
/// </summary>
internal static class EcsRuntimeFactory
{
    public static EcsPlayerRuntimeAdapter Create(IPlayerStateRepository sections,
        IAssetBalanceRepository assets, IEconomyTransactionStore economy, GameDataCatalog catalog,
        IPlayerSimulationClock? simulationClock = null, DateTimeOffset? startAt = null)
        => new(new EcsGameRuntime(catalog, startAt ?? DateTimeOffset.UnixEpoch), sections, assets, economy,
            catalog, NullLogger<EcsPlayerRuntimeAdapter>.Instance, simulationClock);
}
