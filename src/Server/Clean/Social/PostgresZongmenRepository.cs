using System.Data;
using System.Text.Json;
using Domain.World;
using Domain.GameData;
using Protocol;

namespace Server.Clean.Social;

/// <summary>宗门权威行；已解散行保留挂靠点供离线成员安全退出旧驻地。</summary>
public sealed record ZongmenRecord(Guid Id, string Name, string Prefix, Guid LeaderId, string AnchorNodeId,
    bool Disbanded, int HallLevel, double MemberCount)
{
    public string NodeId => ZongmenRules.ResidenceNodeId(Id);
    public double Capacity => ZongmenRules.MemberCapacity(HallLevel);
}

/// <summary>资格撤销的确定时刻，用于在离线补算前截断旧驻地活动。</summary>
public sealed record ZongmenDeparture(DateTimeOffset At, string AnchorNodeId);

/// <summary>驻地准入在同一条查询中读取，避免推进时重复加载名册与大殿资料。</summary>
public sealed record ZongmenResidenceAccess(string AnchorNodeId, bool Disbanded, bool IsMember, ZongmenDeparture? Departure);

/// <summary>宗门持久化与命令事务；组织变更、令牌扣除和幂等回执同生同灭。</summary>
public sealed class PostgresZongmenRepository(NpgsqlDataSource dataSource, GameDataCatalog catalog)
{
    public async ValueTask<ZongmenRecord?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ReadAsync(connection, null, id, false, ct);
    }

    public async ValueTask<ZongmenRecord?> FindForPlayerAsync(Guid playerId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var id = await ScalarAsync(connection, null, "SELECT zongmen_id FROM social.zongmen_members WHERE player_id=$1", ct, playerId);
        return id is Guid value ? await ReadAsync(connection, null, value, false, ct) : null;
    }

    public async ValueTask<ZongmenResidenceAccess> ResidenceAccessAsync(Guid playerId, Guid id, DateTimeOffset after, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Command(connection, null, """
            SELECT z.anchor_node_id,z.disbanded_at IS NOT NULL,
                EXISTS(SELECT 1 FROM social.zongmen_members m WHERE m.player_id=$1 AND m.zongmen_id=$2),
                d.left_at,d.anchor_node_id
            FROM social.zongmen z LEFT JOIN LATERAL (
                SELECT left_at,anchor_node_id FROM social.zongmen_departures
                WHERE player_id=$1 AND zongmen_id=$2 AND left_at >= $3 ORDER BY left_at LIMIT 1
            ) d ON true WHERE z.zongmen_id=$2
            """, playerId, id, after);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("驻地缺少宗门权威行。");
        var departure = reader.IsDBNull(3) ? null : new ZongmenDeparture(reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4));
        return new(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), departure);
    }

    /// <summary>自身驻地与组织名录分开投影，他宗门的节点、挂靠点和私密成员不进入名录。</summary>
    public async ValueTask<ZongmenStateDto> StateAsync(Guid playerId, OnlinePresenceRegistry presence, string search, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var readTransaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var ownId = await ScalarAsync(connection, null, "SELECT zongmen_id FROM social.zongmen_members WHERE player_id=$1", ct, playerId);
        ZongmenDto? own = null;
        if (ownId is Guid id && await ReadAsync(connection, null, id, false, ct) is { } record)
        {
            var members = new List<ZongmenMemberDto>();
            await using (var command = Command(connection, null, """
                SELECT m.player_id,p.display_name,m.role FROM social.zongmen_members m
                JOIN identity.players p ON p.player_id=m.player_id
                WHERE m.zongmen_id=$1 ORDER BY m.role,m.joined_at,m.player_id
                """, id))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                {
                    var memberId = reader.GetGuid(0);
                    members.Add(new(memberId.ToString("D"), reader.GetString(1), reader.GetInt32(2), presence.IsOnline(memberId)));
                }
            var role = members.Single(member => member.PlayerId == playerId.ToString("D")).Role;
            var requests = ZongmenRules.CanManage(role)
                ? await RequestsAsync(connection, "r.zongmen_id=$1", id, ct) : [];
            own = new(id.ToString("D"), record.Name, record.Prefix, record.LeaderId.ToString("D"), role,
                record.AnchorNodeId, record.NodeId, record.HallLevel, record.Capacity, members, requests);
        }
        var directory = new List<ZongmenSummaryDto>();
        await using (var command = Command(connection, null, """
            SELECT z.zongmen_id,z.name,z.prefix,
                (SELECT count(*) FROM social.zongmen_members m WHERE m.zongmen_id=z.zongmen_id),
                b.level,b.target_level,b.started_at_unix_seconds,b.contributed_materials,
                z.anchor_node_id
            FROM social.zongmen z JOIN game.node_buildings b
                ON b.node_id='node.zongmen.' || replace(z.zongmen_id::text,'-','') AND b.building_id=$2
            WHERE z.disbanded_at IS NULL AND strpos(lower(z.name),lower($1)) > 0 ORDER BY z.name LIMIT 50
            """, search.Trim()[..Math.Min(search.Trim().Length, 64)], ZongmenRules.HallBuildingId))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var building = new NodeBuildingState(ZongmenRules.HallBuildingId, reader.GetInt32(4), reader.GetInt32(5), reader.GetDouble(6),
                    JsonSerializer.Deserialize<Dictionary<string, double>>(reader.GetString(7))!);
                var level = NodeBuildingRules.EffectiveLevel(building, catalog.Buildings[ZongmenRules.HallBuildingId], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d);
                directory.Add(new(reader.GetGuid(0).ToString("D"), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), ZongmenRules.MemberCapacity(level), reader.GetString(8)));
            }
        var invitations = await RequestsAsync(connection, "r.player_id=$1 AND r.kind='invitation'", playerId, ct);
        var applications = await RequestsAsync(connection, "r.player_id=$1 AND r.kind='application'", playerId, ct);
        await readTransaction.CommitAsync(ct);
        return new(own, directory, invitations, applications);
    }

    public async ValueTask<IReadOnlyList<Guid>> MemberIdsAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Command(connection, null, "SELECT player_id FROM social.zongmen_members WHERE zongmen_id=$1", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<Guid>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0));
        return result;
    }

    public async ValueTask<IReadOnlyDictionary<Guid, string>> PrefixesAsync(IEnumerable<Guid> playerIds, CancellationToken ct)
    {
        var ids = playerIds.Distinct().ToArray();
        var result = new Dictionary<Guid, string>();
        if (ids.Length == 0) return result;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Command(connection, null, """
            SELECT m.player_id,z.prefix FROM social.zongmen_members m JOIN social.zongmen z USING(zongmen_id)
            WHERE m.player_id=ANY($1) AND z.disbanded_at IS NULL
            """, (object)ids);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0), reader.GetString(1));
        return result;
    }

    public async ValueTask<string?> PrefixAsync(Guid playerId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ScalarAsync(connection, null, """
            SELECT z.prefix FROM social.zongmen z JOIN social.zongmen_members m USING(zongmen_id)
            WHERE m.player_id=$1 AND z.disbanded_at IS NULL
            """, ct, playerId) as string;
    }

    /// <summary>固定原请求进行冲突重试与未知提交确认，不能重算令牌或替换首次回执。</summary>
    public async ValueTask<EconomyTransactionResult> ExecuteAsync(Guid playerId, Guid requestId, string action,
        JsonElement payload, string currentNodeId, bool travelling, CommandReceipt receipt, CancellationToken ct)
    {
        Require(payload.ValueKind == JsonValueKind.Object, "宗门操作参数无效。");
        var now = DateTimeOffset.UtcNow;
        var request = new EconomyTransactionRequest(requestId, $"zongmen:{playerId:D}:{requestId:D}", action, playerId,
            action is "zongmen.create" or "zongmen.move" ? [new AssetDelta(playerId, ZongmenRules.TokenItemId, "", -1)] : [],
            now, JsonSerializer.SerializeToUtf8Bytes(new { action, payload }, ProtocolJson.Options), Receipts: [receipt]);
        var stripes = PlayerCommitGates.StripesFor([playerId]);
        await PlayerCommitGates.AcquireAllAsync(stripes, ct);
        EconomyCommitOutcomeUnknownException? unknown = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var confirmation = unknown is null ? null : new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var token = confirmation?.Token ?? ct;
                try
                {
                    await using var connection = await dataSource.OpenConnectionAsync(token);
                    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
                    var previous = await PostgresEconomyRepository.TryLoadCommittedAsync(connection, transaction, request.IdempotencyKey, token);
                    if (previous is not null) return PostgresEconomyRepository.ValidateReplay(request, previous);
                    await MutateAsync(connection, transaction, playerId, requestId, action, payload, currentNodeId, travelling, now, token);
                    var result = await PostgresEconomyRepository.CommitCoreAsync(connection, transaction, request, token);
                    await PostgresEconomyRepository.CommitTransactionAsync(transaction, request.TransactionId, token);
                    return result;
                }
                catch (PostgresException error) when (error.SqlState is "40001" or "40P01" && attempt < 5 && unknown is null)
                { await Task.Delay(25 * (attempt + 1), ct); }
                catch (PostgresException error) when (error.SqlState == "23505" && unknown is null)
                { throw new DomainException(DomainErrorCode.Duplicate, error.ConstraintName == "zongmen_name_normalized_key" ? "宗门名称已存在。" : "角色已加入宗门或请求已处理。"); }
                catch (EconomyInsufficientBalanceException) when (unknown is null)
                { throw new DomainException(DomainErrorCode.InvalidArgument, "宗门令牌不足，请先炼制。"); }
                catch (EconomyCommitOutcomeUnknownException error) when (unknown is null) { unknown = error; }
                catch (Exception error) when (unknown is not null)
                { unknown.Data["ConfirmationError"] = error; throw unknown; }
            }
        }
        finally { PlayerCommitGates.ReleaseAll(stripes); }
    }

    private async ValueTask MutateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid playerId,
        Guid requestId, string action, JsonElement payload, string currentNodeId, bool travelling, DateTimeOffset now, CancellationToken ct)
    {
        var target = Guid.TryParse(Text(payload, "targetPlayerId"), out var parsedTarget) ? parsedTarget : playerId;
        if (action == "zongmen.invite" && target == playerId && Text(payload, "targetName") is { Length: > 0 } targetName)
        {
            var found = await ScalarAsync(connection, transaction,
                "SELECT player_id FROM identity.players WHERE display_name_normalized=$1 AND status=1", ct,
                PostgresAccountRepository.NormalizeNameForLookup(targetName));
            Require(found is Guid, "角色不存在。");
            target = (Guid)found!;
        }
        // 所有涉及的角色先按稳定顺序取事务锁，再锁宗门行，避免并发批准把一个角色加入两个组织。
        foreach (var id in new[] { playerId, target }.Distinct().Order())
            await ScalarAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1, 25))", ct, id.ToString("D"));
        var own = await ScalarAsync(connection, transaction, "SELECT zongmen_id FROM social.zongmen_members WHERE player_id=$1", ct, playerId);
        if (action == "zongmen.create")
        {
            Require(own is null, "你已加入宗门。");
            var anchor = PublicAnchor(currentNodeId, travelling);
            Require(Text(payload, "anchorNodeId") == anchor, "驻留地点已变化，请重新确认宗门位置。");
            var name = ZongmenRules.NormalizeName(Text(payload, "name"));
            var prefix = ZongmenRules.NormalizePrefix(Text(payload, "prefix"));
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{playerId:D}:{requestId:D}"))[..16]);
            await RunAsync(connection, transaction, """
                INSERT INTO social.zongmen(zongmen_id,name,name_normalized,prefix,leader_id,anchor_node_id,created_at)
                VALUES($1,$2,$3,$4,$5,$6,$7)
                """, ct, id, name, name.ToUpperInvariant(), prefix, playerId, anchor, now);
            await RunAsync(connection, transaction, "INSERT INTO social.zongmen_members(player_id,zongmen_id,role,joined_at) VALUES($1,$2,0,$3)", ct, playerId, id, now);
            await RunAsync(connection, transaction, """
                INSERT INTO game.node_buildings(node_id,building_id,level,target_level,started_at_unix_seconds,contributed_materials,updated_at)
                VALUES($1,$2,1,0,0,'{}'::jsonb,$3)
                """, ct, ZongmenRules.ResidenceNodeId(id), ZongmenRules.HallBuildingId, now);
            return;
        }
        var selectedId = Guid.TryParse(Text(payload, "zongmenId"), out var selected) ? selected : own as Guid?;
        Require(selectedId.HasValue, "请先选择宗门。");
        var record = await ReadAsync(connection, transaction, selectedId!.Value, true, ct);
        Require(record is { Disbanded: false }, "宗门不存在或已解散。");
        var zongmen = record!;
        var actorRoleRaw = await ScalarAsync(connection, transaction,
            "SELECT role FROM social.zongmen_members WHERE player_id=$1 AND zongmen_id=$2", ct, playerId, zongmen.Id);
        var role = actorRoleRaw is int actorRole ? actorRole : 99;
        if (action == "zongmen.apply")
        {
            Require(own is null, "你已加入宗门。");
            await RunAsync(connection, transaction, """
                INSERT INTO social.zongmen_requests(zongmen_id,player_id,kind,created_at) VALUES($1,$2,'application',$3)
                ON CONFLICT(zongmen_id,player_id) DO UPDATE SET kind='application',created_at=EXCLUDED.created_at
                """, ct, zongmen.Id, playerId, now);
            return;
        }
        if (action == "zongmen.accept")
        {
            Require(own is null, "你已加入宗门。");
            Require(await ScalarAsync(connection, transaction, "SELECT 1 FROM social.zongmen_requests WHERE zongmen_id=$1 AND player_id=$2 AND kind='invitation'", ct, zongmen.Id, playerId) is not null, "邀请已失效。");
            await JoinAsync(connection, transaction, zongmen, playerId, now, ct);
            return;
        }
        if (action == "zongmen.decline")
        {
            await RunAsync(connection, transaction, "DELETE FROM social.zongmen_requests WHERE zongmen_id=$1 AND player_id=$2", ct, zongmen.Id, playerId);
            return;
        }
        Require(role < 7, "你不是该宗门成员。");
        if (action == "zongmen.leave")
        {
            Require(role != 0, "宗主须先移交或解散宗门。");
            await RemoveMemberAsync(connection, transaction, zongmen, playerId, now, ct);
            return;
        }
        if (action is "zongmen.move" or "zongmen.disband")
        {
            Require(role == 0, "仅宗主可执行此操作。");
            if (action == "zongmen.move")
            {
                var anchor = PublicAnchor(currentNodeId, travelling);
                Require(Text(payload, "anchorNodeId") == anchor && anchor != zongmen.AnchorNodeId, "请选择并重新确认新的宗门位置。");
                await RunAsync(connection, transaction, "UPDATE social.zongmen SET anchor_node_id=$2 WHERE zongmen_id=$1", ct, zongmen.Id, anchor);
            }
            else
            {
                await RunAsync(connection, transaction, """
                    INSERT INTO social.zongmen_departures(zongmen_id,player_id,left_at,anchor_node_id)
                    SELECT zongmen_id,player_id,$2,$3 FROM social.zongmen_members WHERE zongmen_id=$1
                    """, ct, zongmen.Id, now, zongmen.AnchorNodeId);
                await RunAsync(connection, transaction, "DELETE FROM social.zongmen_requests WHERE zongmen_id=$1", ct, zongmen.Id);
                await RunAsync(connection, transaction, "DELETE FROM social.zongmen_members WHERE zongmen_id=$1", ct, zongmen.Id);
                await RunAsync(connection, transaction, "UPDATE social.zongmen SET disbanded_at=$2 WHERE zongmen_id=$1", ct, zongmen.Id, now);
            }
            return;
        }
        Require(ZongmenRules.CanManage(role), "当前职位没有此管理权限。");
        if (action == "zongmen.invite")
        {
            Require(target != playerId, "不能邀请自己。");
            Require(await ScalarAsync(connection, transaction, "SELECT 1 FROM identity.players WHERE player_id=$1 AND status=1", ct, target) is not null, "角色不存在。");
            Require(await ScalarAsync(connection, transaction, "SELECT 1 FROM social.zongmen_members WHERE player_id=$1", ct, target) is null, "对方已加入宗门。");
            await RunAsync(connection, transaction, """
                INSERT INTO social.zongmen_requests(zongmen_id,player_id,kind,created_at) VALUES($1,$2,'invitation',$3)
                ON CONFLICT(zongmen_id,player_id) DO UPDATE SET kind='invitation',created_at=EXCLUDED.created_at
                """, ct, zongmen.Id, target, now);
            return;
        }
        if (action is "zongmen.approve" or "zongmen.reject")
        {
            Require(await ScalarAsync(connection, transaction, "SELECT 1 FROM social.zongmen_requests WHERE zongmen_id=$1 AND player_id=$2 AND kind='application'", ct, zongmen.Id, target) is not null, "申请已失效。");
            if (action == "zongmen.approve") await JoinAsync(connection, transaction, zongmen, target, now, ct);
            else await RunAsync(connection, transaction, "DELETE FROM social.zongmen_requests WHERE zongmen_id=$1 AND player_id=$2", ct, zongmen.Id, target);
            return;
        }
        var targetRaw = await ScalarAsync(connection, transaction, "SELECT role FROM social.zongmen_members WHERE player_id=$1 AND zongmen_id=$2", ct, target, zongmen.Id);
        Require(targetRaw is int, "目标不是本宗成员。");
        var targetRole = (int)targetRaw!;
        Require(ZongmenRules.CanManageMember(role, targetRole), "只能管理低于自身职位的成员。");
        if (action == "zongmen.kick") await RemoveMemberAsync(connection, transaction, zongmen, target, now, ct);
        else if (action == "zongmen.role")
        {
            var next = payload.TryGetProperty("role", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : -1;
            Require(next is >= 1 and <= 6 && next > role && (next >= 4 || role == 0), "不能授予该职位。");
            await RunAsync(connection, transaction, "UPDATE social.zongmen_members SET role=$2 WHERE player_id=$1", ct, target, next);
        }
        else if (action == "zongmen.transfer")
        {
            Require(role == 0, "仅宗主可移交宗门。");
            await RunAsync(connection, transaction, "UPDATE social.zongmen_members SET role=1 WHERE player_id=$1", ct, playerId);
            await RunAsync(connection, transaction, "UPDATE social.zongmen_members SET role=0 WHERE player_id=$1", ct, target);
            await RunAsync(connection, transaction, "UPDATE social.zongmen SET leader_id=$2 WHERE zongmen_id=$1", ct, zongmen.Id, target);
        }
        else throw new DomainException(DomainErrorCode.InvalidArgument, "宗门操作无效。");
    }

    private static async ValueTask JoinAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ZongmenRecord row, Guid playerId, DateTimeOffset now, CancellationToken ct)
    {
        Require(row.MemberCount < row.Capacity, "宗门人数已满，请先升级大殿。");
        Require(await ScalarAsync(connection, transaction, "SELECT 1 FROM social.zongmen_members WHERE player_id=$1", ct, playerId) is null, "角色已加入宗门。");
        await RunAsync(connection, transaction, "INSERT INTO social.zongmen_members(player_id,zongmen_id,role,joined_at) VALUES($1,$2,6,$3)", ct, playerId, row.Id, now);
        await RunAsync(connection, transaction, "DELETE FROM social.zongmen_requests WHERE player_id=$1", ct, playerId);
    }

    private static async ValueTask RemoveMemberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ZongmenRecord row, Guid playerId, DateTimeOffset now, CancellationToken ct)
    {
        await RunAsync(connection, transaction, "INSERT INTO social.zongmen_departures(zongmen_id,player_id,left_at,anchor_node_id) VALUES($1,$2,$3,$4)", ct, row.Id, playerId, now, row.AnchorNodeId);
        await RunAsync(connection, transaction, "DELETE FROM social.zongmen_members WHERE player_id=$1 AND zongmen_id=$2", ct, playerId, row.Id);
    }

    private string PublicAnchor(string nodeId, bool travelling)
    {
        Require(!travelling && catalog.WorldGraph.TryGetNode(nodeId, out var node) && !node.Hidden, "请先驻留普通公共地点。");
        return nodeId;
    }

    private async ValueTask<ZongmenRecord?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, bool locked, CancellationToken ct)
    {
        ZongmenRecord result;
        await using (var command = Command(connection, transaction, """
            SELECT zongmen_id,name,prefix,leader_id,anchor_node_id,disbanded_at IS NOT NULL
            FROM social.zongmen WHERE zongmen_id=$1
            """ + (locked ? " FOR UPDATE" : ""), id))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            result = new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4), reader.GetBoolean(5), 1, 0);
        }
        var level = 1;
        await using (var command = Command(connection, transaction, """
            SELECT level,target_level,started_at_unix_seconds,contributed_materials FROM game.node_buildings
            WHERE node_id=$1 AND building_id=$2
            """, result.NodeId, ZongmenRules.HallBuildingId))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
            {
                var materials = JsonSerializer.Deserialize<Dictionary<string, double>>(reader.GetString(3))!;
                var state = new NodeBuildingState(ZongmenRules.HallBuildingId, reader.GetInt32(0), reader.GetInt32(1), reader.GetDouble(2), materials);
                level = NodeBuildingRules.EffectiveLevel(state, catalog.Buildings[ZongmenRules.HallBuildingId], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d);
            }
        var count = await ScalarAsync(connection, transaction, "SELECT count(*) FROM social.zongmen_members WHERE zongmen_id=$1", ct, id);
        return result with { HallLevel = level, MemberCount = Convert.ToDouble(count) };
    }

    private static async ValueTask<List<ZongmenRequestDto>> RequestsAsync(NpgsqlConnection connection, string condition, Guid id, CancellationToken ct)
    {
        var result = new List<ZongmenRequestDto>();
        await using var command = Command(connection, null, """
            SELECT r.zongmen_id,z.name,r.player_id,p.display_name,r.kind
            FROM social.zongmen_requests r JOIN social.zongmen z USING(zongmen_id)
            JOIN identity.players p USING(player_id) WHERE z.disbanded_at IS NULL AND
            """ + " " + condition + " ORDER BY r.created_at LIMIT 100", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0).ToString("D"), reader.GetString(1), reader.GetGuid(2).ToString("D"), reader.GetString(3), reader.GetString(4)));
        return result;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }
    private static async ValueTask<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteScalarAsync(ct);
        return result is DBNull ? null : result;
    }
    private static async ValueTask RunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static string Text(JsonElement payload, string key) => payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static void Require(bool condition, string message)
    { if (!condition) throw new DomainException(DomainErrorCode.InvalidArgument, message); }
}
