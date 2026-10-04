using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Simulation;

namespace Server.Clean;

/// <summary>模拟资产事务标识来源；生产未注入时继续使用随机 UUID。</summary>
public interface ISimulationTransactionIdentity
{
    /// <summary>由幂等键派生事务标识；实现必须对同一键稳定返回同一 GUID。</summary>
    /// <param name="idempotencyKey">业务幂等键（如 simulation:{playerId}:{version}:{toMs}）。</param>
    /// <returns>事务标识，同时作为经济流水行主键。</returns>
    Guid Create(string idempotencyKey);
}

/// <summary>仅显式基准宿主注册，令相同业务幂等键在独立库产生相同事务标识。</summary>
public sealed class DeterministicSimulationTransactionIdentity : ISimulationTransactionIdentity
{
    /// <summary>对同一幂等键哈希出稳定 GUID：取 SHA-256 前 16 字节，保证独立实验库产生相同事务标识。</summary>
    /// <param name="idempotencyKey">业务幂等键。</param>
    /// <returns>确定性事务标识。</returns>
    public Guid Create(string idempotencyKey)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)).AsSpan(0, 16));
}

/// <summary>模拟资产事务封装；保留领域原始增量、行序和结算时刻。</summary>
public static class SimulationEconomyTransaction
{
    /// <summary>
    /// 把一轮模拟的资产增量封装为经济事务请求：幂等键由玩家/状态版本/结算时刻拼出，
    /// 事务标识默认随机 UUID，注入 <paramref name="identity"/> 后改为确定性派生（基准 replay 对齐用）。
    /// </summary>
    /// <param name="playerId">收益归属玩家。</param>
    /// <param name="result">本轮模拟结果，提供资产增量、状态版本与结算时刻。</param>
    /// <param name="identity">确定性事务标识来源；null 时使用随机 UUID（生产口径）。</param>
    /// <returns>可直接提交 <see cref="IEconomyTransactionStore"/> 的事务请求。</returns>
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
