namespace Server.Clean.Social;

/// <summary>
/// 传音消息的 PostgreSQL 持久化实现。
/// 保留策略：写入与裁剪在同一事务内完成，任何 (channel, scope_key) 作用域只保留最新 100 条，
/// 不存在历史归档与分页接口。
/// </summary>
public sealed class PostgresChatRepository(NpgsqlDataSource dataSource) : IChatRepository
{
    /// <summary>写入一条消息并同事务裁剪该作用域超出保留条数的旧行。</summary>
    public async ValueTask AppendAsync(ChatMessageRecord message, int retention, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO social.chat_messages(message_id, channel, scope_key, sender_id, sender_name, body, sent_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = message.MessageId });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.Channel });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.ScopeKey });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.SenderId });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.SenderName });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.Body });
            insert.Parameters.Add(new NpgsqlParameter { Value = message.SentAt });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var trim = new NpgsqlCommand("""
            DELETE FROM social.chat_messages
            WHERE channel = $1 AND scope_key = $2
              AND message_id NOT IN (
                  SELECT message_id FROM social.chat_messages
                  WHERE channel = $1 AND scope_key = $2
                  ORDER BY sent_at DESC, message_id DESC
                  LIMIT $3
              )
            """, connection, transaction))
        {
            trim.Parameters.Add(new NpgsqlParameter { Value = message.Channel });
            trim.Parameters.Add(new NpgsqlParameter { Value = message.ScopeKey });
            trim.Parameters.Add(new NpgsqlParameter { Value = retention });
            await trim.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>读取某作用域最近若干条消息，按时间正序返回。</summary>
    public async ValueTask<IReadOnlyList<ChatMessageRecord>> LoadRecentAsync(string channel, string scopeKey, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT message_id, channel, scope_key, sender_id, sender_name, body, sent_at
            FROM (
                SELECT message_id, channel, scope_key, sender_id, sender_name, body, sent_at
                FROM social.chat_messages
                WHERE channel = $1 AND scope_key = $2
                ORDER BY sent_at DESC, message_id DESC
                LIMIT $3
            ) recent
            ORDER BY sent_at, message_id
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = channel });
        command.Parameters.Add(new NpgsqlParameter { Value = scopeKey });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        var result = new List<ChatMessageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ChatMessageRecord(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
                reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6)));
        return result;
    }
}
