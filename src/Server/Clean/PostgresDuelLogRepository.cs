namespace Server.Clean;

/// <summary>PVP 斗法日志中的单个道具损益条目。</summary>
/// <param name="ItemId">物品定义 ID。</param>
/// <param name="Quantity">本场获得或失去的数量。</param>
public sealed record DuelItemDeltaEntry(string ItemId, double Quantity);

/// <summary>PVP 斗法完整收益日志条目（白名单第 4 类）。</summary>
/// <param name="DuelId">斗法唯一标识（用于 <c>ux_duel_logs_duel_id</c> 幂等防重）。</param>
/// <param name="InitiatorPlayerId">发起方玩家 ID。</param>
/// <param name="InitiatorName">发起方战时道号。</param>
/// <param name="InitiatorPower">发起方撮合战力。</param>
/// <param name="TargetPlayerId">应战方玩家 ID。</param>
/// <param name="TargetName">应战方战时道号。</param>
/// <param name="TargetPower">应战方撮合战力。</param>
/// <param name="NodeId">斗法发生的世界节点 ID。</param>
/// <param name="WinnerPlayerId">胜方玩家 ID；平局时为 null。</param>
/// <param name="Outcome">胜负结论：<c>initiator_win</c> / <c>target_win</c> / <c>draw</c>。</param>
/// <param name="Forfeited">是否由主动认输触发结束。</param>
/// <param name="InitiatorGains">发起方本场战利品收益明细。</param>
/// <param name="InitiatorLosses">发起方本场被掠夺损失明细。</param>
/// <param name="TargetGains">应战方本场战利品收益明细。</param>
/// <param name="TargetLosses">应战方本场被掠夺损失明细。</param>
/// <param name="DurationSeconds">斗法脚本持续时长（秒）。</param>
/// <param name="OccurredAt">结算落库时刻（UTC）。</param>
public sealed record DuelLogEntry(
    string DuelId,
    Guid InitiatorPlayerId,
    string InitiatorName,
    double InitiatorPower,
    Guid TargetPlayerId,
    string TargetName,
    double TargetPower,
    string NodeId,
    Guid? WinnerPlayerId,
    string Outcome,
    bool Forfeited,
    IReadOnlyList<DuelItemDeltaEntry> InitiatorGains,
    IReadOnlyList<DuelItemDeltaEntry> InitiatorLosses,
    IReadOnlyList<DuelItemDeltaEntry> TargetGains,
    IReadOnlyList<DuelItemDeltaEntry> TargetLosses,
    double DurationSeconds,
    DateTimeOffset OccurredAt);

/// <summary>PVP 斗法完整收益日志仓储契约（白名单第 4 类）。</summary>
public interface IDuelLogRepository
{
    /// <summary>幂等写入一条斗法结算日志；同一 <c>duel_id</c> 重放静默跳过。</summary>
    /// <param name="entry">斗法日志条目。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RecordAsync(DuelLogEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>基于 PostgreSQL <c>game.duel_logs</c> 的 PVP 斗法收益日志仓储实现。</summary>
public sealed class PostgresDuelLogRepository(NpgsqlDataSource dataSource) : IDuelLogRepository
{
    /// <inheritdoc />
    public async ValueTask RecordAsync(DuelLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var outcome = entry.Outcome is "initiator_win" or "target_win" or "draw" ? entry.Outcome : "draw";
        var initiatorGainsJson = JsonSerializer.Serialize(entry.InitiatorGains);
        var initiatorLossesJson = JsonSerializer.Serialize(entry.InitiatorLosses);
        var targetGainsJson = JsonSerializer.Serialize(entry.TargetGains);
        var targetLossesJson = JsonSerializer.Serialize(entry.TargetLosses);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            INSERT INTO game.duel_logs (
                duel_log_id, duel_id,
                initiator_player_id, initiator_name, initiator_power,
                target_player_id, target_name, target_power,
                node_id, winner_player_id, outcome, forfeited,
                initiator_gains, initiator_losses, target_gains, target_losses,
                duration_seconds, occurred_at)
            VALUES (
                $1, $2,
                $3, $4, $5,
                $6, $7, $8,
                $9, $10, $11, $12,
                $13::jsonb, $14::jsonb, $15::jsonb, $16::jsonb,
                $17, $18)
            ON CONFLICT (duel_id) DO NOTHING
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = Truncate(entry.DuelId, 96) });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.InitiatorPlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = Truncate(entry.InitiatorName, 64) });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = Sanitize(entry.InitiatorPower) });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.TargetPlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = Truncate(entry.TargetName, 64) });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = Sanitize(entry.TargetPower) });
        command.Parameters.Add(new NpgsqlParameter { Value = Truncate(entry.NodeId, 128) });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.WinnerPlayerId is { } winner ? winner : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = outcome });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.Forfeited });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = initiatorGainsJson });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = initiatorLossesJson });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = targetGainsJson });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = targetLossesJson });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = Math.Max(0d, Sanitize(entry.DurationSeconds)) });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.OccurredAt });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= maxLength ? value : value[..maxLength]);

    private static double Sanitize(double value) => double.IsFinite(value) ? value : 0d;
}
