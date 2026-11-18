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

    /// <summary>写入消息，同时从同一会话复制原始引用；命中裁剪点时在同一事务裁剪。</summary>
    public async ValueTask<ChatMessageRecord?> AppendAsync(ChatMessageRecord message, int retention, Guid? quoteMessageId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO social.chat_messages(message_id, channel, scope_key, sender_id, sender_name, body,
                                             party_ref, sent_at, sender_realm, quote_message_id, quote_sender_name, quote_body)
            SELECT $1, $2, $3, $4, $5, $6, $7, $8, $9, original.message_id, original.sender_name, original.body
            FROM (VALUES (1)) AS seed(n)
            LEFT JOIN social.chat_messages original
                ON original.message_id = $10 AND original.channel = $2 AND original.scope_key = $3
            WHERE $10 IS NULL OR original.message_id IS NOT NULL
            RETURNING quote_message_id, quote_sender_name, quote_body
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(message.MessageId);
            insert.Parameters.AddWithValue(message.Channel);
            insert.Parameters.AddWithValue(message.ScopeKey);
            insert.Parameters.AddWithValue(message.SenderId);
            insert.Parameters.AddWithValue(message.SenderName);
            insert.Parameters.AddWithValue(message.Body);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)message.PartyLink?.PartyId ?? DBNull.Value });
            insert.Parameters.AddWithValue(message.SentAt);
            insert.Parameters.AddWithValue(message.SenderRealmLevel);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)quoteMessageId ?? DBNull.Value });
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            message = message with { Quote = ReadQuote(reader, 0) };
        }
        var trimDue = ShouldTrim(message.Channel, message.ScopeKey);
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
        return message;
    }

    /// <summary>按作用域写计数判定本次是否触发裁剪：每第 <see cref="TrimInterval"/> 条写触发一次。</summary>
    private bool ShouldTrim(string channel, string scopeKey)
    {
        if (_appendsSinceTrim.Count >= TrimCounterPruneThreshold) _appendsSinceTrim.Clear();
        var writes = _appendsSinceTrim.AddOrUpdate((channel, scopeKey), 1, static (_, count) => count + 1);
        return writes % TrimInterval == 0;
    }

    /// <summary>锁定原消息，串行判定点赞状态和推进版本；与裁剪、历史快照保持一致。</summary>
    public async ValueTask<ChatLikeState?> SetLikeAsync(string channel, string scopeKey, Guid messageId, Guid playerId, bool liked, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await using var guard = new NpgsqlCommand("""
            SELECT like_version FROM social.chat_messages
            WHERE message_id = $1 AND channel = $2 AND scope_key = $3
            FOR NO KEY UPDATE
            """, connection, transaction);
        guard.Parameters.AddWithValue(messageId);
        guard.Parameters.AddWithValue(channel);
        guard.Parameters.AddWithValue(scopeKey);
        if (await guard.ExecuteScalarAsync(cancellationToken) is not long version) return null;

        // Read Committed 下锁后的语句能看到上一持锁事务提交的点赞；重复请求不增加版本。
        await using var edit = new NpgsqlCommand(liked
            ? "INSERT INTO social.chat_message_likes(message_id, player_id) VALUES ($1, $2) ON CONFLICT DO NOTHING"
            : "DELETE FROM social.chat_message_likes WHERE message_id = $1 AND player_id = $2", connection, transaction);
        edit.Parameters.AddWithValue(messageId);
        edit.Parameters.AddWithValue(playerId);
        var changed = await edit.ExecuteNonQueryAsync(cancellationToken) != 0;
        if (changed)
        {
            await using var advance = new NpgsqlCommand("UPDATE social.chat_messages SET like_version = like_version + 1 WHERE message_id = $1", connection, transaction);
            advance.Parameters.AddWithValue(messageId);
            await advance.ExecuteNonQueryAsync(cancellationToken);
            version++;
        }

        await using var count = new NpgsqlCommand("SELECT count(*)::double precision FROM social.chat_message_likes WHERE message_id = $1", connection, transaction);
        count.Parameters.AddWithValue(messageId);
        var likeCount = (double)(await count.ExecuteScalarAsync(cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return new ChatLikeState(likeCount, version, changed);
    }

    /// <summary>读取某作用域最近若干条消息，按时间正序返回。</summary>
    public async ValueTask<IReadOnlyList<ChatMessageRecord>> LoadRecentAsync(string channel, string scopeKey, Guid viewerId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // party_ref 随队伍解散 ON DELETE SET NULL 自动变回普通文本；存活队伍联出档案与人数供卡片渲染。
        await using var command = new NpgsqlCommand("""
            SELECT message_id, channel, scope_key, sender_id, sender_name, body, sent_at, sender_realm,
                   pt.party_id, pt.name, pt.activity_kind, pt.node_id, pt.min_valuation,
                   (SELECT count(*) FROM social.party_members pm WHERE pm.party_id = pt.party_id) AS member_count,
                   quote_message_id, quote_sender_name, quote_body, like_version,
                   (SELECT count(*)::double precision FROM social.chat_message_likes l WHERE l.message_id = recent.message_id),
                   EXISTS(SELECT 1 FROM social.chat_message_likes l WHERE l.message_id = recent.message_id AND l.player_id = $4)
            FROM (
                SELECT message_id, channel, scope_key, sender_id, sender_name, body, party_ref, sent_at, sender_realm,
                       quote_message_id, quote_sender_name, quote_body, like_version
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
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(viewerId);
        var result = new List<ChatMessageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var link = reader.IsDBNull(8)
                ? null
                : new PartyLinkRecord(
                    reader.GetGuid(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                    reader.GetDouble(12), Convert.ToInt32(reader.GetInt64(13)));
            var realmLevel = reader.IsDBNull(7) ? 1 : reader.GetInt32(7);
            result.Add(new ChatMessageRecord(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
                reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), link, realmLevel,
                ReadQuote(reader, 14), reader.GetDouble(18), reader.GetBoolean(19), reader.GetInt64(17)));
        }
        return result;
    }

    private static ChatQuoteRecord? ReadQuote(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : new ChatQuoteRecord(reader.GetGuid(ordinal), reader.GetString(ordinal + 1), reader.GetString(ordinal + 2));
}
