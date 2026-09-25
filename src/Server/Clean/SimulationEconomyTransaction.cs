using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Simulation;

namespace Server.Clean;

/// <summary>模拟资产事务标识来源；生产未注入时继续使用随机 UUID。</summary>
public interface ISimulationTransactionIdentity
{
    Guid Create(string idempotencyKey);
}

/// <summary>仅显式基准宿主注册，令相同业务幂等键在独立库产生相同事务标识。</summary>
public sealed class DeterministicSimulationTransactionIdentity : ISimulationTransactionIdentity
{
    public Guid Create(string idempotencyKey)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)).AsSpan(0, 16));
}

/// <summary>OOP/ECS 共用模拟资产事务封装；保留领域原始增量、行序和结算时刻。</summary>
public static class SimulationEconomyTransaction
{
    public static EconomyTransactionRequest Create(Guid playerId, SimulationResult result,
        ISimulationTransactionIdentity? identity = null)
    {
        var key = $"simulation:{playerId:D}:{result.StateVersion}:{result.To.ToUnixTimeMilliseconds()}";
        return new EconomyTransactionRequest(
            identity?.Create(key) ?? Guid.NewGuid(), key, "simulation.reward", playerId,
            result.AssetDeltas.Select(delta => new AssetDelta(delta.PlayerId.Value,
                delta.Asset.ItemId.Value, delta.Asset.Variant, delta.QuantityDelta)).ToArray(),
            result.To, JsonSerializer.SerializeToUtf8Bytes(new { result.StateVersion, result.To }));
    }
}
