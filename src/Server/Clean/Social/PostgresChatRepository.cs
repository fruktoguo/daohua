namespace Server.Clean.Social;

/// <summary>
/// 传音消息的 PostgreSQL 持久化实现。
/// 保留策略：写入与裁剪在同一事务内完成，任何 (channel, scope_key) 作用域只保留最新 100 条，
/// 不存在历史归档与分页接口。
/// 裁剪按作用域写计数摊销：每写满 <see cref="TrimInterval"/> 条才执行一次 DELETE，
/// 超限量被约束在 (0, TrimInterval) 条内，随下一次周期裁剪回到上限——100 条不变量最终成立。
/// </summary>
public sealed class PostgresChatRepository(NpgsqlDataSource dataSource) : IChatRepository
{
    /// <summary>同一作用域每写入多少条做一次裁剪（把每条一次的 DELETE 摊薄为 1/16）。</summary>
    private const int TrimInterval = 16;
    /// <summary>写计数表容量阈值：超过即清空重计，避免长跑下作用域键无限膨胀（重计只推迟裁剪，不破坏上限不变量）。</summary>
    private const int TrimCounterPruneThreshold = 4096;

    /// <summary>(channel, scopeKey) → 自上次裁剪周期起累计的写入数。</summary>
    private readonly ConcurrentDictionary<(string Channel, string ScopeKey), int> _appendsSinceTrim = new();

    /// <summary>写入一条消息；命中本周期裁剪点时在同事务内裁掉该作用域超出保留条数的旧行。</summary>
    public async ValueTask AppendAsync(ChatMessageRecord message, int retention, CancellationToken cancellationToken = default)
    {
        var trimDue = ShouldTrim(message.Channel, message.ScopeKey);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO social.chat_messages(message_id, channel, scope_key, sender_id, sender_name, body, party_ref, sent_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = message.MessageId });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.Channel });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.ScopeKey });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.SenderId });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.SenderName });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.Body });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)message.PartyLink?.PartyId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.SentAt });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (trimDue)
        {
            await using var trim = new NpgsqlCommand("""
                DELETE FROM social.chat_messages
                WHERE channel = $1 AND scope_key = $2
                  AND message_id NOT IN (
                      SELECT message_id FROM social.chat_messages
                      WHERE channel = $1 AND scope_key = $2
                      ORDER BY sent_at DESC, message_id DESC
                      LIMIT $3
                  )
                """, connection, transaction);
            trim.Parameters.Add(new NpgsqlParameter { Value = message.Channel });
            trim.Parameters.Add(new NpgsqlParameter { Value = message.ScopeKey });
            trim.Parameters.Add(new NpgsqlParameter { Value = retention });
            await trim.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>按作用域写计数判定本次是否触发裁剪：每第 <see cref="TrimInterval"/> 条写触发一次。</summary>
    private bool ShouldTrim(string channel, string scopeKey)
    {
        if (_appendsSinceTrim.Count >= TrimCounterPruneThreshold) _appendsSinceTrim.Clear();
        var writes = _appendsSinceTrim.AddOrUpdate((channel, scopeKey), 1, static (_, count) => count + 1);
        return writes % TrimInterval == 0;
    }

    /// <summary>读取某作用域最近若干条消息，按时间正序返回。</summary>
    public async ValueTask<IReadOnlyList<ChatMessageRecord>> LoadRecentAsync(string channel, string scopeKey, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // party_ref 随队伍解散 ON DELETE SET NULL 自动变回普通文本；存活队伍联出档案与人数供卡片渲染。
        await using var command = new NpgsqlCommand("""
            SELECT message_id, channel, scope_key, sender_id, sender_name, body, sent_at,
                   pt.party_id, pt.name, pt.activity_kind, pt.map_id, pt.min_valuation,
                   (SELECT count(*) FROM social.party_members pm WHERE pm.party_id = pt.party_id) AS member_count
            FROM (
                SELECT message_id, channel, scope_key, sender_id, sender_name, body, party_ref, sent_at
                FROM social.chat_messages
                WHERE channel = $1 AND scope_key = $2
                ORDER BY sent_at DESC, message_id DESC
                LIMIT $3
            ) recent
            LEFT JOIN social.parties pt ON pt.party_id = recent.party_ref
            ORDER BY sent_at, message_id
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = channel });
        command.Parameters.Add(new NpgsqlParameter { Value = scopeKey });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        var result = new List<ChatMessageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var link = reader.IsDBNull(7)
                ? null
                : new PartyLinkRecord(
                    reader.GetGuid(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
                    reader.GetDouble(11), Convert.ToInt32(reader.GetInt64(12)));
            result.Add(new ChatMessageRecord(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
                reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), link));
        }
        return result;
    }
}
