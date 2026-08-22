// ==============================================================================
// 文件名称: StateSerializer.cs
// 主要职责: 提供玩家显式 SnapshotEnvelope 结构与紧凑 UTF-8 JSON 字节数组之间的序列化与反序列化。
// 核心逻辑 / 定义内容:
//   1. 全局统一 Web 规范 JSON 序列化选项配置 (JsonSerializerOptions)。
//   2. 显式不可变 SnapshotEnvelope (含 SnapshotSchemaVersion, GameDataFingerprint, PlayerStateVersion, SequenceId, LastSimulatedAt, Payload) 序列化。
//   3. 严格校验快照 Schema 版本与游戏数据指纹，非法/旧版本/指纹不一致/损坏载荷直接抛出 InvalidDataException 并保留错误证据。
// ==============================================================================

using System.Text.Json;
using Domain.GameData;
using Domain.Models;

namespace Server.Infrastructure;

/// <summary>
/// 玩家状态快照数据序列化器 (StateSerializer)。
/// <para>
/// 职责：
/// 1. 将玩家状态 <see cref="PlayerState"/> 包装为不可变 <see cref="SnapshotEnvelope"/> 并序列化为紧凑 UTF-8 JSON 字节载荷。
/// 2. 从快照载荷反序列化时严格校验 Schema 版本与 GameData 指纹，拒绝旧快照/未知结构/指纹不一致，不提供静默 fallback 或旧迁移。
/// </para>
/// </summary>
public static class StateSerializer
{
    /// <summary>
    /// 全局统一 Web 风格 JSON 序列化选项（非缩进、紧凑排版）。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// 将玩家状态包装为显式快照信封 (SnapshotEnvelope) 并序列化为 UTF-8 JSON 字节数组。
    /// </summary>
    /// <param name="state">玩家领域状态模型。</param>
    /// <param name="gameDataFingerprint">可选的游戏静态数据指纹，默认读取当前单例指纹。</param>
    /// <returns>UTF-8 编码的快照信封 JSON 字节数组。</returns>
    public static byte[] Serialize(PlayerState state, string? gameDataFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var envelope = new SnapshotEnvelope(
            SnapshotSchemaVersion: SnapshotEnvelope.CurrentSchemaVersion,
            GameDataFingerprint: gameDataFingerprint ?? GameDataCatalog.Current.Fingerprint,
            PlayerStateVersion: state.Version,
            SequenceId: state.SequenceId,
            LastSimulatedAt: state.LastSimulatedAt,
            Payload: state);
        return SerializeEnvelope(envelope);
    }

    /// <summary>
    /// 将显式快照信封序列化为 UTF-8 JSON 字节数组。
    /// </summary>
    public static byte[] SerializeEnvelope(SnapshotEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    /// <summary>
    /// 从快照 JSON 字节载荷解析并校验快照信封 (SnapshotEnvelope)。
    /// </summary>
    /// <param name="payload">快照二进制载荷。</param>
    /// <param name="expectedFingerprint">期望匹配的游戏数据指纹，为 null 时比对当前静态数据指纹。</param>
    /// <returns>校验通过的快照信封。</returns>
    /// <exception cref="InvalidDataException">快照为空、Schema 版本不兼容、指纹不匹配或载荷损坏时抛出。</exception>
    public static SnapshotEnvelope DeserializeEnvelope(ReadOnlyMemory<byte> payload, string? expectedFingerprint = null)
    {
        if (payload.IsEmpty)
        {
            throw new InvalidDataException("快照载荷为空。");
        }

        SnapshotEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(payload.Span, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"快照载荷 JSON 解析失败: {ex.Message}", ex);
        }

        if (envelope is null)
        {
            throw new InvalidDataException("快照载荷反序列化结果为空。");
        }

        if (envelope.SnapshotSchemaVersion != SnapshotEnvelope.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"快照 Schema 版本不兼容: 快照版本 {envelope.SnapshotSchemaVersion}，系统当前支持版本 {SnapshotEnvelope.CurrentSchemaVersion}。");
        }

        var targetFingerprint = expectedFingerprint ?? GameDataCatalog.Current.Fingerprint;
        if (!string.Equals(envelope.GameDataFingerprint, targetFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"游戏数据指纹不一致: 快照指纹 '{envelope.GameDataFingerprint}'，当前系统指纹 '{targetFingerprint}'。");
        }

        if (envelope.Payload is null)
        {
            throw new InvalidDataException("快照信封中的玩家状态载荷 (Payload) 为空。");
        }

        if (envelope.PlayerStateVersion <= 0)
        {
            throw new InvalidDataException($"快照信封版本号非法: {envelope.PlayerStateVersion}，必须为正整数。");
        }

        if (envelope.SequenceId <= 0)
        {
            throw new InvalidDataException($"快照信封序号非法: {envelope.SequenceId}，必须为正整数。");
        }

        var state = envelope.Payload;
        if (state.PlayerId == Guid.Empty)
        {
            throw new InvalidDataException("快照载荷中的 PlayerId 为空。");
        }

        if (string.IsNullOrWhiteSpace(state.Name))
        {
            throw new InvalidDataException("快照载荷中的玩家名称为空。");
        }

        if (envelope.PlayerStateVersion != state.Version)
        {
            throw new InvalidDataException(
                $"快照信封版本号 ({envelope.PlayerStateVersion}) 与载荷内部版本号 ({state.Version}) 不一致。");
        }

        if (envelope.SequenceId != state.SequenceId)
        {
            throw new InvalidDataException(
                $"快照信封序号 ({envelope.SequenceId}) 与载荷内部序号 ({state.SequenceId}) 不一致。");
        }

        if (envelope.LastSimulatedAt != state.LastSimulatedAt)
        {
            throw new InvalidDataException(
                $"快照信封结算时间 ({envelope.LastSimulatedAt:O}) 与载荷内部结算时间 ({state.LastSimulatedAt:O}) 不一致。");
        }

        return envelope;
    }

    /// <summary>
    /// 从快照 JSON 字节载荷反序列化并校验，提取玩家领域状态模型。
    /// </summary>
    /// <param name="payload">快照二进制载荷。</param>
    /// <param name="expectedFingerprint">期望匹配的游戏数据指纹，为 null 时比对当前静态数据指纹。</param>
    /// <returns>反序列化后的玩家状态模型。</returns>
    /// <exception cref="InvalidDataException">快照非法、版本或指纹不一致时抛出。</exception>
    public static PlayerState Deserialize(ReadOnlyMemory<byte> payload, string? expectedFingerprint = null)
    {
        return DeserializeEnvelope(payload, expectedFingerprint).Payload;
    }
}

