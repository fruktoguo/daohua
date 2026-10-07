using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Npgsql;
using Server.Clean;
using Server.Clean.Social;

// ============================================================================
// 组队讨伐种子工具（一次性）：为尐妖创建两名 18 级满配队友并插进现有队伍。
// 用法: dotnet run --project tools/seed-party -- [--apply] [--leader <playerId>]
//   默认 dry-run 只打印将要写入的内容；--apply 才落库。
// 约束：新号注册后从未装载，直写存档安全；队伍行按领域裁决等价插入。
// ============================================================================

const string ConnectionString =
    "Host=127.0.0.1;Port=5432;Database=daohua;Username=daohua;Password=daohua-local-dev;Timeout=10";
const int Pbkdf2Iterations = 600_000; // 与 PostgresAccountRepository 同口径
const string TargetNodeId = "node.cloud-gate"; // 队长当前所在节点（山门试炼遭遇池）
var partyId = Guid.Parse("ebe886a8-09c5-4c20-9afa-d20aa9401543"); // 尐妖现有队伍「猛攻」

var apply = args.Contains("--apply");
var leaderArg = args.SkipWhile(a => a != "--leader").Skip(1).FirstOrDefault();
var leaderId = leaderArg is not null ? Guid.Parse(leaderArg) : Guid.Parse("13c49fce-2cc8-40d6-8710-00551e2d5298");

await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
var catalog = GameDataCatalog.LoadEmbedded();
var social = new PostgresSocialRepository(dataSource);
var now = DateTimeOffset.UtcNow;

Console.WriteLine($"[seed] 配表指纹 {catalog.Fingerprint}；队长 {leaderId}；目标节点 {TargetNodeId}；模式={(apply ? "APPLY" : "DRY-RUN")}");

// ---------------------------------------------------------------------------
// 0. 读队长存档打印参照练度
// ---------------------------------------------------------------------------
await using (var connection = await dataSource.OpenConnectionAsync())
{
    var head = await new NpgsqlCommand(
        "SELECT state_version, schema_version, last_simulated_at FROM game.player_state_heads WHERE player_id=$1",
        connection) { Parameters = { new NpgsqlParameter { Value = leaderId } } }
        .ExecuteReaderAsync() is { } r && await r.ReadAsync()
        ? (r.GetInt64(0), r.GetInt32(1), r.GetFieldValue<DateTimeOffset>(2)) : default;
    Console.WriteLine($"[leader] head={head}");
    await connection.CloseAsync();
}

// ---------------------------------------------------------------------------
// 1. 造号：注册账号+玩家行 → 构造聚合 → 灌经验到 RealmLevel 18 → 满配 → 写档
// ---------------------------------------------------------------------------
var teammates = new (string Account, string Password, string Name, string Role,
    string WeaponId, string TrinketId, string[] Abilities)[]
{
    ("yue_qianjun", "Seed#2026", "岳千钧", "物理",
        "equipment.pogang-sword", "equipment.chiwen-ring",
        ["ability.heavy-slash", "ability.chain-strike", "ability.star-piercer", "ability.heavy-clouds"]),
    ("bai_qingheng", "Seed#2026", "白青蘅", "法术+回血",
        "equipment.chaoyuan-staff", "equipment.juling-pendant",
        ["ability.palm-lightning", "ability.blazing-blast", "ability.qingping-melody", "ability.lightning-attraction"]),
};

var seeded = new List<(Guid PlayerId, string Name)>();
foreach (var mate in teammates)
{
    await using var connection = await dataSource.OpenConnectionAsync();

    // 幂等入口：按道号查重——已存在则复用账号/玩家行只重写存档，不重复注册。
    Guid accountId, playerId;
    await using (var lookup = new NpgsqlCommand(
        "SELECT player_id, account_id FROM identity.players WHERE display_name=$1", connection))
    {
        lookup.Parameters.Add(new NpgsqlParameter { Value = mate.Name });
        await using var reader = await lookup.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            playerId = reader.GetGuid(0);
            accountId = reader.GetGuid(1);
            Console.WriteLine($"[seed] {mate.Name} 已存在（{playerId}）——复用身份行，仅补读模型与队伍行。");
            seeded.Add((playerId, mate.Name));
            await connection.CloseAsync();
            if (apply)
            {
                await SyncActionQueueReadModelAsync(playerId);
                var join = await social.TryJoinPartyAsync(partyId, playerId, now, CancellationToken.None);
                if (join == PartyJoinResult.Joined)
                    await social.TrySetPartyMemberReadyAsync(playerId, true, CancellationToken.None);
            }
            continue;
        }
    }

    accountId = Guid.NewGuid();
    playerId = Guid.NewGuid();
    var inviteCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));

    // 1a. 身份行（PBKDF2-SHA512 与注册仓储同口径）。
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(mate.Password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 64);
    await using (var command = new NpgsqlCommand("""
        INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, status, created_at, updated_at)
        VALUES ($1, $2, $3, $4, $5, 1, $6, $6)
        """, connection))
    {
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = mate.Account.ToUpperInvariant() });
        command.Parameters.Add(new NpgsqlParameter { Value = hash });
        command.Parameters.Add(new NpgsqlParameter { Value = salt });
        command.Parameters.Add(new NpgsqlParameter { Value = Pbkdf2Iterations });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        if (apply) await command.ExecuteNonQueryAsync();
    }
    await using (var command = new NpgsqlCommand("""
        INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at, status, invite_code)
        VALUES ($1, $2, $3, $4, $5, $5, 1, $6)
        """, connection))
    {
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = mate.Name });
        command.Parameters.Add(new NpgsqlParameter { Value = mate.Name.ToUpperInvariant() });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = inviteCode });
        if (apply) await command.ExecuteNonQueryAsync();
    }

    // 1b. 领域聚合：练度 18 级 + 满配装备技能 + 目标节点 + 资源满。
    var aggregate = PlayerAggregate.Create(
        new PlayerId(playerId), new AccountId(accountId), mate.Name, now, catalog.Fingerprint);
    PlayerAttributeResolver.Apply(aggregate, catalog);
    var realm = catalog.RealmProgression;
    while (aggregate.Progression.RealmLevel < 18)
    {
        var cap = realm?.ExpCapFor(aggregate.Progression.RealmLevel) ?? double.PositiveInfinity;
        var grant = double.IsFinite(cap) ? cap : 1_000_000d;
        aggregate.Progression.CreditExperience(grant, grant, cap);
        if (realm is not null) aggregate.Breakthrough(realm);
        else break;
    }
    aggregate.ReplaceCombatSlots(
        mate.Abilities.Select(id => new PlayerAbilitySlot(id, 18)).ToArray(),
        new[]
        {
            new PlayerEquipmentSlot(mate.WeaponId, 20),
            new PlayerEquipmentSlot("equipment.ziwu-crown", 20),
            new PlayerEquipmentSlot("equipment.pichen-shirt", 20),
            new PlayerEquipmentSlot("equipment.liuyun-skirt", 20),
            new PlayerEquipmentSlot("equipment.bichen-shoes", 20),
            new PlayerEquipmentSlot(mate.TrinketId, 20),
        });
    aggregate.World.NodeId = TargetNodeId;
    PlayerAttributeResolver.Apply(aggregate, catalog);
    aggregate.Resources.Hitpoints = aggregate.Resources.MaxHitpoints;
    aggregate.Resources.Manapoints = aggregate.Resources.MaxManapoints;
    // 挂一个本节点可用的单人讨伐行动（开打后被会话伪行动插队首）。
    aggregate.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1), now, catalog.Actions);

    Console.WriteLine($"[seed] {mate.Name}（{mate.Role}）playerId={playerId} realm={aggregate.Progression.RealmLevel} hp={aggregate.Resources.MaxHitpoints:0} atkSlots={mate.Abilities.Length}");

    // 1c. 落库：分段 + 头 + 投影（presence=offline 等待首次登录）。
    var writes = SectionCodec.EncodeAggregateFull(aggregate.ExportState(), now);
    foreach (var write in writes) write.Validate();
    if (apply)
    {
        await using var tx = await connection.BeginTransactionAsync();
        foreach (var write in writes)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO game.player_state_sections
                    (player_id, section_kind, section_version, schema_version, codec, payload, raw_bytes, stored_bytes, checksum, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                ON CONFLICT (player_id, section_kind) DO UPDATE SET
                    section_version=EXCLUDED.section_version, schema_version=EXCLUDED.schema_version,
                    codec=EXCLUDED.codec, payload=EXCLUDED.payload, raw_bytes=EXCLUDED.raw_bytes,
                    stored_bytes=EXCLUDED.stored_bytes, checksum=EXCLUDED.checksum, updated_at=EXCLUDED.updated_at
                """, connection, tx);
            command.Parameters.Add(new NpgsqlParameter { Value = write.PlayerId });
            command.Parameters.Add(new NpgsqlParameter { Value = write.SectionKind });
            command.Parameters.Add(new NpgsqlParameter { Value = write.StateVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = write.SchemaVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = write.Codec });
            command.Parameters.Add(new NpgsqlParameter { Value = write.Payload.ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = write.RawBytes });
            command.Parameters.Add(new NpgsqlParameter { Value = write.StoredBytes });
            command.Parameters.Add(new NpgsqlParameter { Value = write.Checksum });
            command.Parameters.Add(new NpgsqlParameter { Value = write.UpdatedAt });
            await command.ExecuteNonQueryAsync();
        }
        var headChecksum = SectionCodec.ComputeHeadChecksum(writes.Select(write =>
            new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion,
                write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt)));
        await using (var command = new NpgsqlCommand("""
            INSERT INTO game.player_state_heads(player_id, state_version, schema_version, last_simulated_at, game_data_fingerprint, checksum, updated_at)
            VALUES ($1, 1, $2, $3, $4, $5, $6)
            ON CONFLICT (player_id) DO UPDATE SET
                state_version=EXCLUDED.state_version, schema_version=EXCLUDED.schema_version,
                last_simulated_at=EXCLUDED.last_simulated_at, game_data_fingerprint=EXCLUDED.game_data_fingerprint,
                checksum=EXCLUDED.checksum, updated_at=EXCLUDED.updated_at
            """, connection, tx))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            command.Parameters.Add(new NpgsqlParameter { Value = SectionCodec.CurrentSchemaVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            command.Parameters.Add(new NpgsqlParameter { Value = catalog.Fingerprint });
            command.Parameters.Add(new NpgsqlParameter { Value = headChecksum });
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO game.player_projection(player_id, realm_rank, current_node_id, presence, last_active_at, projection_version, updated_at)
            VALUES ($1, $2, $3, 'offline', $4, 1, $4)
            ON CONFLICT (player_id) DO UPDATE SET
                realm_rank=EXCLUDED.realm_rank, current_node_id=EXCLUDED.current_node_id,
                presence=EXCLUDED.presence, updated_at=EXCLUDED.updated_at
            """, connection, tx))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            command.Parameters.Add(new NpgsqlParameter { Value = 18d });
            command.Parameters.Add(new NpgsqlParameter { Value = TargetNodeId });
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            await command.ExecuteNonQueryAsync();
        }
        // 行动队列关系型读模型同步（与 ApplyActionQueueAsync 同构：解码 section → 槽位行重建）。
        await SyncActionQueueReadModelAsync(playerId, connection, tx,
            writes.First(w => w.SectionKind == PlayerSectionKinds.ActionQueue));
        await tx.CommitAsync();
    }
    await connection.CloseAsync();
    seeded.Add((playerId, mate.Name));
}

// ---------------------------------------------------------------------------
// 2. 组队：尐妖现有队伍 + 两名新成员 + 槽位收窄到 3 + 目标节点对齐队长位置
// ---------------------------------------------------------------------------
if (apply)
{
    await social.TryUpdatePartyProfileAsync(partyId,
        new PartyProfileRecord("猛攻", "any", TargetNodeId, 0, false), now, CancellationToken.None);
    await social.TryUpdatePartyOpenSlotsAsync(partyId, 3, now, CancellationToken.None);
}

Console.WriteLine();
Console.WriteLine(apply
    ? "完成。岳千钧/白青蘅已入队并就绪；让尐妖点一次「准备」即触发开打仲裁（三人在青云山门同节点）。"
    : "DRY-RUN 未落库。确认无误后追加 --apply 执行。");

// ---------------------------------------------------------------------------
// 行动队列读模型重建：与 PostgresSectionRepository.ApplyActionQueueAsync 同构。
// 两种用法：写档事务内传 (connection, tx, write)；存量号补写时自动开事务并先读 section。
// ---------------------------------------------------------------------------
async Task SyncActionQueueReadModelAsync(
    Guid playerId, NpgsqlConnection? connection = null, NpgsqlTransaction? tx = null,
    SectionWrite? write = null)
{
    var ownConnection = connection is null;
    var ownTransaction = tx is null;
    connection ??= await dataSource.OpenConnectionAsync();
    tx ??= await connection.BeginTransactionAsync();
    try
    {
        if (write is null)
        {
            // 存量号：从 game.player_state_sections 读 action_queue 行解码重建。
            await using var read = new NpgsqlCommand("""
                SELECT section_version, schema_version, codec, payload, raw_bytes, checksum, updated_at
                FROM game.player_state_sections WHERE player_id=$1 AND section_kind='action_queue'
                """, connection, tx);
            read.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await read.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return;
            var stored = new StoredSection(playerId, PlayerSectionKinds.ActionQueue,
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetFieldValue<byte[]>(3), reader.GetInt32(4), reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6));
            await reader.DisposeAsync();
            var queued = SectionCodec.Decode<List<QueuedActionState>>(stored);
            await WriteQueueRowsAsync(connection, tx, playerId, stored.StateVersion, queued);
            return;
        }
        var actions = SectionCodec.Decode<List<QueuedActionState>>(
            new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion,
                write.SchemaVersion, write.Codec, write.Payload.ToArray(),
                write.RawBytes, write.Checksum, write.UpdatedAt));
        await WriteQueueRowsAsync(connection, tx, playerId, write.StateVersion, actions);
    }
    finally
    {
        if (ownTransaction) await tx!.CommitAsync();
        else if (ownConnection) await connection.CloseAsync();
    }

    static async Task WriteQueueRowsAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid playerId, long actionVersion, List<QueuedActionState> actions)
    {
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM game.player_action_queue WHERE player_id = $1", connection, tx))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await delete.ExecuteNonQueryAsync();
        }
        for (var slot = 0; slot < actions.Count; slot++)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO game.player_action_queue
                    (player_id, slot, action_id, action_payload, action_version, status, next_due_at, idempotency_key, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, NULL, $7, $8)
                """, connection, tx);
            insert.Parameters.Add(new NpgsqlParameter { Value = playerId });
            insert.Parameters.Add(new NpgsqlParameter { Value = (short)slot });
            insert.Parameters.Add(new NpgsqlParameter { Value = actions[slot].Intent.ActionId.Value });
            insert.Parameters.Add(new NpgsqlParameter
                { Value = JsonSerializer.SerializeToUtf8Bytes(actions[slot].Intent, PersistenceJson.Options) });
            insert.Parameters.Add(new NpgsqlParameter { Value = actionVersion });
            insert.Parameters.Add(new NpgsqlParameter { Value = actions[slot].Status.ToString().ToLowerInvariant() });
            insert.Parameters.Add(new NpgsqlParameter { Value = $"action:{actions[slot].Id:D}" });
            insert.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow });
            await insert.ExecuteNonQueryAsync();
        }
    }
}
