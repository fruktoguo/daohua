using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 快照增量推送门禁：
/// 1. touchedSections 只窄化编码范围，checksum 仍是最终裁决；
/// 2. 可丢弃帧入队失败不前移分段/资产基线；
/// 3. 慢消费者持续丢帧后以 4009 关闭且不再投递；
/// 4. 传音广播同一载荷只序列化一次、各会话独立序号；
/// 5. 运行时推进/命令结果携带本次触及分段差集；
/// 6. combat 派生候选由锁内属性解析实例引用变化精确判定（WireAttributesChanged）。
/// </summary>
public sealed class SnapshotDeltaPushTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.UnixEpoch;

    private static PlayerRuntimeSnapshot SnapshotOf(PlayerAggregate aggregate, IReadOnlySet<PlayerSectionKind>? touched = null,
        bool? wireAttributesChanged = null) => new(
        aggregate.PlayerId.Value,
        aggregate.AccountId.Value.ToString("D"),
        aggregate.DisplayName,
        Math.Max(1, aggregate.StateVersion),
        aggregate.LastSimulatedAt,
        aggregate.ExportState(),
        aggregate.Actions.Count,
        [],
        touched,
        wireAttributesChanged);

    private static Dictionary<string, string> ChecksumsOf(IReadOnlyList<PlayerSectionDto> sections)
    {
        var map = new Dictionary<string, string>(sections.Count, StringComparer.Ordinal);
        foreach (var section in sections) map[section.Section] = section.Checksum;
        return map;
    }

    /// <summary>
    /// 触及差集窄化编码：只触及 Buffs 的推送只对 buffs/progression/combat 候选段编码，
    /// checksum 裁决后仅真实变化的 buffs 进入增量；未触及分段沿用基线校验和逐位不重算。
    /// </summary>
    [Fact]
    public void BuildDelta_TouchedSectionsNarrowsEncode_ChecksumArbitrates()
    {
        var catalog = GameDataCatalog.BuiltIn;
        var aggregate = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "窄化", Baseline, "fp");
        var baselineDto = ProtocolSnapshotMapper.ToSnapshot(SnapshotOf(aggregate), "fp", catalog);
        var baselineChecksums = ChecksumsOf(baselineDto.Sections);

        // 模拟「本操作只触及 Buffs」：无属性效果的增益，content 变化只落在 buffs。
        aggregate.Buffs.Active.Add(new ActiveBuff("buff.focus", Baseline.AddHours(1).ToUnixTimeMilliseconds() / 1000d, 1, "凝神"));
        var delta = ProtocolSnapshotMapper.BuildDelta(
            SnapshotOf(aggregate, new HashSet<PlayerSectionKind> { PlayerSectionKind.Buffs }),
            "fp", catalog, new HashSet<PlayerSectionKind> { PlayerSectionKind.Buffs },
            baselineChecksums, baselineDto.Assets,
            out var nextChecksums, out _);

        var sent = Assert.Single(delta.ChangedSections);
        Assert.Equal(PlayerSectionKinds.Buffs, sent.Section);
        // 未触及分段逐位沿用基线校验和（未重新编码）；候选段重算后与基线一致者被裁决丢弃。
        Assert.Equal(baselineChecksums[PlayerSectionKinds.World], nextChecksums[PlayerSectionKinds.World]);
        Assert.Equal(baselineChecksums[PlayerSectionKinds.Loadout], nextChecksums[PlayerSectionKinds.Loadout]);
        Assert.Equal(baselineChecksums[PlayerSectionKinds.ActionQueue], nextChecksums[PlayerSectionKinds.ActionQueue]);
        Assert.Equal(baselineChecksums[PlayerSectionKinds.Duel], nextChecksums[PlayerSectionKinds.Duel]);
        Assert.Equal(baselineChecksums[PlayerSectionKinds.Settings], nextChecksums[PlayerSectionKinds.Settings]);
        Assert.Equal(8, nextChecksums.Count);
    }

    /// <summary>
    /// 未知触及集（touched=null）回退全段编码：输出与旧「全量 ToSnapshot + DiffSections/DiffAssets」逐位一致。
    /// </summary>
    [Fact]
    public void BuildDelta_NullTouched_MatchesLegacyFullEncodeOutput()
    {
        var catalog = GameDataCatalog.BuiltIn;
        var baselineAggregate = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "旧", Baseline, "fp");
        var baselineDto = ProtocolSnapshotMapper.ToSnapshot(SnapshotOf(baselineAggregate), "fp", catalog);
        var baselineChecksums = ChecksumsOf(baselineDto.Sections);

        var aggregate = PlayerAggregate.Create(baselineAggregate.PlayerId, baselineAggregate.AccountId, "新", Baseline, "fp");
        aggregate.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 3), Baseline, catalog.Actions);
        aggregate.Buffs.Active.Add(new ActiveBuff("buff.focus", Baseline.AddHours(1).ToUnixTimeMilliseconds() / 1000d, 2, "凝神"));
        var runtime = SnapshotOf(aggregate);

        var current = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", catalog);
        var legacySections = ProtocolSnapshotMapper.DiffSections(baselineChecksums, current.Sections);
        var legacyAssets = ProtocolSnapshotMapper.DiffAssets(baselineDto.Assets, current.Assets);

        var delta = ProtocolSnapshotMapper.BuildDelta(runtime, "fp", catalog, null, baselineChecksums, baselineDto.Assets,
            out var nextChecksums, out var nextAssets);

        Assert.Equal(
            legacySections.Select(section => (section.Section, section.Checksum)),
            delta.ChangedSections.Select(section => (section.Section, section.Checksum)));
        Assert.Equal(legacyAssets.Select(a => (a.AssetId, a.Variant, a.Quantity)),
            (delta.ChangedAssets ?? []).Select(a => (a.AssetId, a.Variant, a.Quantity)));
        Assert.Equal(current.Sections.ToDictionary(s => s.Section, s => s.Checksum, StringComparer.Ordinal), nextChecksums);
        Assert.Equal(current.Assets, nextAssets);
    }

    /// <summary>
    /// combat 属性面板跨段派生：属性面板判定为已变（或未携带判定信息回退候选）时，
    /// combat 属候选编码并被 checksum 裁决纳入增量——派生投影不依赖 dirty 标记本身。
    /// </summary>
    [Fact]
    public void BuildDelta_DerivedCombatCandidate_EncodedWhenAttributeInputTouched()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var aggregate = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "派生", Baseline, "fp");
        var baselineDto = ProtocolSnapshotMapper.ToSnapshot(SnapshotOf(aggregate), "fp", catalog);
        var baselineChecksums = ChecksumsOf(baselineDto.Sections);

        // 属性 Buff 只标 Buffs 脏：combat 属性面板随 HpRegen 输入变化但未标脏。
        aggregate.Buffs.Active.Add(new ActiveBuff("buff.regen", Baseline.AddHours(1).ToUnixTimeMilliseconds() / 1000d, 1, "回春", HpRegen: 5));
        var delta = ProtocolSnapshotMapper.BuildDelta(
            SnapshotOf(aggregate, new HashSet<PlayerSectionKind> { PlayerSectionKind.Buffs }, wireAttributesChanged: true),
            "fp", catalog, new HashSet<PlayerSectionKind> { PlayerSectionKind.Buffs },
            baselineChecksums, baselineDto.Assets,
            out _, out _);

        var sentSections = delta.ChangedSections.Select(s => s.Section).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(PlayerSectionKinds.Buffs, sentSections);
        Assert.Contains(PlayerSectionKinds.Combat, sentSections);
        Assert.DoesNotContain(PlayerSectionKinds.World, sentSections);
        Assert.DoesNotContain(PlayerSectionKinds.ActionQueue, sentSections);
    }

    /// <summary>
    /// 属性面板判定未变（锁内解析缓存命中同一实例）：combat 不再是候选，
    /// 无冷却槽时跳过编码直接沿用基线校验和，不进入增量。
    /// </summary>
    [Fact]
    public void BuildDelta_WireAttributesUnchanged_CombatNotCandidate()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var aggregate = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "未变", Baseline, "fp");
        var baselineDto = ProtocolSnapshotMapper.ToSnapshot(SnapshotOf(aggregate), "fp", catalog);
        var baselineChecksums = ChecksumsOf(baselineDto.Sections);

        // 无属性效果的 Buff 只触及 buffs 段；锁内解析判定面板未变 → combat 跳过候选编码。
        aggregate.Buffs.Active.Add(new ActiveBuff("buff.focus", Baseline.AddHours(1).ToUnixTimeMilliseconds() / 1000d, 1, "凝神"));
        var delta = ProtocolSnapshotMapper.BuildDelta(
            SnapshotOf(aggregate, new HashSet<PlayerSectionKind> { PlayerSectionKind.Buffs }, wireAttributesChanged: false),
            "fp", catalog, new HashSet<PlayerSectionKind> { PlayerSectionKind.Buffs },
            baselineChecksums, baselineDto.Assets,
            out var nextChecksums, out _);

        var sent = Assert.Single(delta.ChangedSections);
        Assert.Equal(PlayerSectionKinds.Buffs, sent.Section);
        Assert.Equal(baselineChecksums[PlayerSectionKinds.Combat], nextChecksums[PlayerSectionKinds.Combat]);
    }

    /// <summary>
    /// 未标脏未候选的分段不编码：分段内容变化未产生标脏时不进入增量——
    /// 该边界正是「一切状态变更必须标脏」的领域契约（持久化同样依赖），差集机制不重复兜底。
    /// </summary>
    [Fact]
    public void BuildDelta_UntouchedSection_ReliesOnDirtyMarkContract()
    {
        var catalog = GameDataCatalog.BuiltIn;
        var aggregate = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "契约", Baseline, "fp");
        var baselineDto = ProtocolSnapshotMapper.ToSnapshot(SnapshotOf(aggregate), "fp", catalog);
        var baselineChecksums = ChecksumsOf(baselineDto.Sections);

        // LearnedAbilities 属 loadout 分段内容：未标脏时该变化按契约不应被差集机制发现。
        var runtime = SnapshotOf(aggregate);
        var mutated = runtime with
        {
            State = runtime.State with { LearnedAbilities = [new AbilityId("ability.test")] }
        };
        var delta = ProtocolSnapshotMapper.BuildDelta(
            mutated, "fp", catalog, new HashSet<PlayerSectionKind> { PlayerSectionKind.Buffs },
            baselineChecksums, baselineDto.Assets,
            out var nextChecksums, out _);

        Assert.DoesNotContain(delta.ChangedSections, section => section.Section == PlayerSectionKinds.Loadout);
        Assert.Equal(baselineChecksums[PlayerSectionKinds.Loadout], nextChecksums[PlayerSectionKinds.Loadout]);
    }

    /// <summary>
    /// 可丢弃帧丢弃时基线不前移：满队列丢帧后下次推送仍以上次「已送达」内容为基线，
    /// 客户端不会永久缺失被丢帧携带的分段与资产变化。
    /// </summary>
    [Fact]
    public void WebSocketSession_DroppedFrame_DoesNotAdvanceBaseline()
    {
        var session = new WebSocketSession(Guid.NewGuid(), capacity: 1);
        var baselineSections = new Dictionary<string, string>(StringComparer.Ordinal) { ["progression"] = "v1" };
        var baselineAssets = new List<AssetBalanceDto> { new("spirit", null, 10, 1) };
        session.CommitPushBaseline(baselineSections, baselineAssets);

        // 占满队列后实时推送被丢弃：基线必须保持上次已送达内容。
        Assert.True(session.TrySendDroppable([1]));
        Assert.False(session.TrySendDroppable([2]));
        var (sections, assets) = session.ReadPushBaseline();
        Assert.Same(baselineSections, sections);
        Assert.Same(baselineAssets, assets);

        // 队列恢复可写后入队成功才允许提交新基线。
        Assert.True(session.Outbound.TryRead(out _));
        Assert.True(session.TrySendDroppable([3]));
        var nextSections = new Dictionary<string, string>(StringComparer.Ordinal) { ["progression"] = "v2" };
        var nextAssets = new List<AssetBalanceDto> { new("spirit", null, 12, 2) };
        session.CommitPushBaseline(nextSections, nextAssets);
        (sections, assets) = session.ReadPushBaseline();
        Assert.Same(nextSections, sections);
        Assert.Same(nextAssets, assets);
    }

    /// <summary>
    /// 慢消费者：连续丢帧达到阈值即以 4009 请求关闭；进入关闭流程后不再投递实时帧。
    /// 4009 未占用防多开码段，前端走默认自动重连拉取全量快照。
    /// </summary>
    [Fact]
    public void WebSocketSession_SlowConsumer_Closes4009AndStopsDelivery()
    {
        var session = new WebSocketSession(Guid.NewGuid(), capacity: 1);
        Assert.True(session.TrySendDroppable([1])); // 占满
        for (var i = 0; i < WebSocketSession.SlowConsumerMaxDrops - 1; i++)
        {
            Assert.False(session.TrySendDroppable([2]));
            Assert.Null(session.CloseHint);
        }
        Assert.False(session.TrySendDroppable([2])); // 第 32 次连续丢弃触发关闭
        var hint = session.CloseHint;
        Assert.NotNull(hint);
        Assert.Equal(4009, hint!.Value.CloseCode);
        Assert.Equal("slow consumer", hint.Value.Reason);
        // 已进入关闭流程：实时帧不再投递。
        Assert.False(session.TrySendDroppable([4]));
    }

    /// <summary>
    /// 传音广播：同一载荷字节全部收件人共享（只序列化一次），
    /// 各会话的信封 requestId/sequence 仍独立维护。
    /// </summary>
    [Fact]
    public void BroadcastChat_SharesSinglePayloadEncoding_PerSessionEnvelope()
    {
        var sessions = new SessionRegistry();
        var first = new WebSocketSession(Guid.NewGuid(), 16);
        var second = new WebSocketSession(Guid.NewGuid(), 16);
        sessions.TryAdd(first);
        sessions.TryAdd(second);
        var message = new ChatMessageDto("m1", ChatChannels.World, "", first.PlayerId.ToString("D"), "发送者", "大家好", 12345L);
        var outcome = new ChatSendOutcome(ChatSendStatus.Ok, message, [first.PlayerId, second.PlayerId, Guid.NewGuid()]);

        WebSocketEndpoint.BroadcastChat(sessions, outcome);

        Assert.True(first.Outbound.TryRead(out var packet1));
        Assert.True(second.Outbound.TryRead(out var packet2));
        var env1 = WireEnvelopeCodec.Decode(packet1);
        var env2 = WireEnvelopeCodec.Decode(packet2);
        Assert.Equal(WireMessageType.ChatMessage, env1.MessageType);
        Assert.Equal(WireMessageType.ChatMessage, env2.MessageType);
        // 载荷字节逐位一致（共享编码），信封头字段各会话独立。
        Assert.True(env1.Payload.Span.SequenceEqual(env2.Payload.Span));
        Assert.NotEqual(env1.RequestId, env2.RequestId);
        Assert.Equal(1, env1.Sequence);
        Assert.Equal(1, env2.Sequence);
        // 第二帧继续递增各自会话序号。
        WebSocketEndpoint.BroadcastChat(sessions, outcome);
        Assert.True(first.Outbound.TryRead(out _));
        Assert.True(second.Outbound.TryRead(out var packet3));
        Assert.Equal(2, WireEnvelopeCodec.Decode(packet3).Sequence);
    }

    /// <summary>
    /// 运行时触及差集管线：action.enqueue 结果快照携带 ActionQueue；
    /// simulation.advance 推送快照携带本次推进实际标脏的分段（含 ActionQueue/Progression）。
    /// </summary>
    [Fact]
    public async Task RuntimePushedSnapshots_CarryTouchedSections()
    {
        var playerId = Guid.NewGuid();
        var repository = new FakeRepository(playerId);
        await using var runtime = EcsRuntimeFactory.Create(
            repository,
            new EmptyAssets(),
            new NoopEconomy(),
            GameDataCatalog.BuiltIn);
        var pushed = new List<PlayerRuntimeSnapshot>();
        runtime.SnapshotUpdated += snapshot =>
        {
            lock (pushed) pushed.Add(snapshot);
            return ValueTask.CompletedTask;
        };

        await runtime.LoadAsync(playerId);
        var requestId = Guid.NewGuid().ToString("D");
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new ActionIntentRequest(requestId, "cultivation.draw-qi", 3), ProtocolJson.Options);
        var result = await runtime.ExecuteAsync(playerId, "action.enqueue", payload, 0, Guid.Parse(requestId));
        Assert.True(result.Success);
        var commandTouched = Assert.IsAssignableFrom<IReadOnlySet<PlayerSectionKind>>(result.Snapshot.TouchedSections);
        Assert.Contains(PlayerSectionKind.ActionQueue, commandTouched);

        await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow.AddSeconds(30));
        PlayerRuntimeSnapshot? advancePush;
        lock (pushed) advancePush = pushed.LastOrDefault(s => s.PlayerId == playerId);
        Assert.NotNull(advancePush);
        var touched = Assert.IsAssignableFrom<IReadOnlySet<PlayerSectionKind>>(advancePush!.TouchedSections);
        Assert.Contains(PlayerSectionKind.ActionQueue, touched);
        Assert.Contains(PlayerSectionKind.Progression, touched);
        // 修炼推进不触及的分段不得出现在差集（Duel 账本/世界/设置随收益入账与否由引擎标脏决定；
        // 引气入体含六维经验收益，入账六维时 Loadout 装配分段随经验写库标脏）。
        Assert.DoesNotContain(PlayerSectionKind.World, touched);
        Assert.Contains(PlayerSectionKind.Loadout, touched);
        Assert.DoesNotContain(PlayerSectionKind.Settings, touched);
    }

    /// <summary>
    /// 版本未推进的命令（market.*/item.* 钱包类与空拍推进同路径）不再附加快照推送：
    /// 同版本推送会被客户端版本门禁丢弃，却前移推送基线、把命令回执的资产差集烧空——
    /// 钱包差额只能由必达的命令回执 delta 携带。版本推进的命令仍照常推送。
    /// </summary>
    [Fact]
    public async Task CommandPush_SameStateVersion_SnapshotSuppressed()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId),
            new EmptyAssets(),
            new NoopEconomy(),
            GameDataCatalog.BuiltIn);
        var pushed = new List<PlayerRuntimeSnapshot>();
        runtime.SnapshotUpdated += snapshot =>
        {
            lock (pushed) pushed.Add(snapshot);
            return ValueTask.CompletedTask;
        };

        var initial = await runtime.LoadAsync(playerId);
        // 精确使用已模拟时刻才是无变化命令；真实时间前进现在必须持久化游标并推进版本。
        var advance = await runtime.ExecuteAsync(playerId, "simulation.advance",
            JsonSerializer.SerializeToElement(new { targetTime = initial.LastSimulatedAt }), 0, Guid.NewGuid());
        Assert.True(advance.Success);
        Assert.Equal(initial.StateVersion, advance.Snapshot.StateVersion);
        Assert.Empty(pushed);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(
            new ActionIntentRequest(requestId.ToString("D"), "cultivation.draw-qi", 3), ProtocolJson.Options);
        var enqueued = await runtime.ExecuteAsync(playerId, "action.enqueue", payload, 0, requestId);
        Assert.True(enqueued.Success);
        Assert.Single(pushed);
    }

    /// <summary>
    /// 命令回执增量形态：触及差集精确且推送基线已建立时，回执只携带触及段与派生候选。
    /// 客户端以「基线分段 + changedSections 覆盖」合并后，分段校验和与全量快照逐位一致；
    /// 回执入队成功还会把会话推送基线前移到回执编码口径。
    /// </summary>
    [Fact]
    public async Task CommandReceipt_EnqueueCommand_DeltaForm_MergesToFullState()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId),
            new EmptyAssets(),
            new NoopEconomy(),
            GameDataCatalog.BuiltIn);
        var session = new WebSocketSession(playerId, 16);
        var initial = await runtime.LoadAsync(playerId);
        var initialDto = ProtocolSnapshotMapper.ToSnapshot(initial, "fp", GameDataCatalog.BuiltIn);
        session.CommitPushBaseline(ChecksumsOf(initialDto.Sections), initialDto.Assets);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(
            new ActionIntentRequest(requestId.ToString("D"), "cultivation.draw-qi", 3), ProtocolJson.Options);
        var result = await runtime.ExecuteAsync(playerId, "action.enqueue", payload, 0, requestId);
        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot.TouchedSections);

        var packet = WebSocketEndpoint.BuildCommandResultPacket(
            session, requestId.ToString("D"), requestId, result, GameDataCatalog.BuiltIn, out var enqueued);
        // 队列有余量：回执已在会话锁内入队并提交基线。
        Assert.True(enqueued);
        var envelope = WireEnvelopeCodec.Decode(packet);
        Assert.Equal(WireMessageType.CommandResult, envelope.MessageType);
        var receipt = JsonSerializer.Deserialize<CommandResultDto>(envelope.Payload.Span, ProtocolJson.Options)!;
        Assert.Equal(CommandResultSnapshotForms.Delta, receipt.SnapshotForm);
        var delta = receipt.Snapshot!.Value.Deserialize<PlayerSnapshotDeltaDto>(ProtocolJson.Options)!;
        Assert.Equal(result.Snapshot.StateVersion, delta.StateVersion);
        Assert.Contains(delta.ChangedSections, section => section.Section == PlayerSectionKinds.ActionQueue);
        // 未触及分段不进入增量载荷。
        Assert.DoesNotContain(delta.ChangedSections, section => section.Section == PlayerSectionKinds.Loadout);
        Assert.DoesNotContain(delta.ChangedSections, section => section.Section == PlayerSectionKinds.Settings);
        Assert.DoesNotContain(delta.ChangedSections, section => section.Section == PlayerSectionKinds.Duel);

        // 客户端合并语义：基线校验和被 changedSections 覆盖后与全量快照逐位一致。
        var fullDto = ProtocolSnapshotMapper.ToSnapshot(result.Snapshot, "fp", GameDataCatalog.BuiltIn);
        var merged = ChecksumsOf(initialDto.Sections);
        foreach (var section in delta.ChangedSections) merged[section.Section] = section.Checksum;
        Assert.Equal(ChecksumsOf(fullDto.Sections), merged);
        // 回执已入队：推送基线前移到回执编码口径，后续增量按此裁决。
        var (nextBaseline, _) = session.ReadPushBaseline();
        Assert.Equal(merged, nextBaseline);
    }

    /// <summary>
    /// 触及集不可判定（命令失败、快照不带差集）时回执回退全量形态：
    /// 即使推送基线已建立也不得窄化，客户端可无条件重建状态。
    /// </summary>
    [Fact]
    public async Task CommandReceipt_FailedCommand_FallsBackToSnapshotForm()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId),
            new EmptyAssets(),
            new NoopEconomy(),
            GameDataCatalog.BuiltIn);
        var session = new WebSocketSession(playerId, 16);
        var initial = await runtime.LoadAsync(playerId);
        var initialDto = ProtocolSnapshotMapper.ToSnapshot(initial, "fp", GameDataCatalog.BuiltIn);
        session.CommitPushBaseline(ChecksumsOf(initialDto.Sections), initialDto.Assets);

        var result = await runtime.ExecuteAsync(playerId, "action.remove",
            JsonSerializer.SerializeToElement(new { actionInstanceId = Guid.NewGuid().ToString("D") }), 0, Guid.NewGuid());
        Assert.False(result.Success);
        Assert.Null(result.Snapshot.TouchedSections);

        var packet = WebSocketEndpoint.BuildCommandResultPacket(
            session, "req-remove", Guid.NewGuid(), result, GameDataCatalog.BuiltIn, out var enqueued);
        // 全量兜底回执同样在会话锁内取号-编码-入队：队列有余量即已入队，调用方无需补发。
        Assert.True(enqueued);
        var receipt = JsonSerializer.Deserialize<CommandResultDto>(WireEnvelopeCodec.Decode(packet).Payload.Span, ProtocolJson.Options)!;
        Assert.Equal(CommandResultSnapshotForms.Snapshot, receipt.SnapshotForm);
        var full = receipt.Snapshot!.Value.Deserialize<PlayerSnapshotDto>(ProtocolJson.Options)!;
        Assert.Equal(8, full.Sections.Count);
    }

    /// <summary>
    /// 幂等重放：同一 requestId 二次执行直接返回持久化/内存回执，快照不带本次触及差集
    /// （原命令生成时点的客户端基线无法假设），回执必须标记全量形态。
    /// </summary>
    [Fact]
    public async Task CommandReceipt_IdempotentReplay_ReturnsSnapshotForm()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId),
            new EmptyAssets(),
            new NoopEconomy(),
            GameDataCatalog.BuiltIn);
        var session = new WebSocketSession(playerId, 16);
        var initial = await runtime.LoadAsync(playerId);
        var initialDto = ProtocolSnapshotMapper.ToSnapshot(initial, "fp", GameDataCatalog.BuiltIn);
        session.CommitPushBaseline(ChecksumsOf(initialDto.Sections), initialDto.Assets);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(
            new ActionIntentRequest(requestId.ToString("D"), "cultivation.draw-qi", 3), ProtocolJson.Options);
        var first = await runtime.ExecuteAsync(playerId, "action.enqueue", payload, 0, requestId);
        Assert.True(first.Success);
        var replay = await runtime.ExecuteAsync(playerId, "action.enqueue", payload, 0, requestId);
        Assert.True(replay.Success);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Code);
        Assert.Null(replay.Snapshot.TouchedSections);

        var packet = WebSocketEndpoint.BuildCommandResultPacket(
            session, requestId.ToString("D"), requestId, replay, GameDataCatalog.BuiltIn, out var enqueued);
        Assert.True(enqueued);
        var receipt = JsonSerializer.Deserialize<CommandResultDto>(WireEnvelopeCodec.Decode(packet).Payload.Span, ProtocolJson.Options)!;
        Assert.Equal(CommandResultSnapshotForms.Snapshot, receipt.SnapshotForm);
    }

    /// <summary>
    /// 零触及命令（market.*/空拍推进等只动钱包或完全无写入）差集为空≠未知：
    /// 回执仍走增量形态，changedSections 只可能含派生候选段（progression 时钟 / combat 属性 / world 建筑）。
    /// </summary>
    [Fact]
    public async Task CommandReceipt_NoTouchedSections_DeltaCarriesDerivedCandidatesOnly()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId),
            new EmptyAssets(),
            new NoopEconomy(),
            GameDataCatalog.BuiltIn);
        var session = new WebSocketSession(playerId, 16);
        var initial = await runtime.LoadAsync(playerId);
        var initialDto = ProtocolSnapshotMapper.ToSnapshot(initial, "fp", GameDataCatalog.BuiltIn);
        session.CommitPushBaseline(ChecksumsOf(initialDto.Sections), initialDto.Assets);

        var result = await runtime.ExecuteAsync(playerId, "simulation.advance",
            JsonSerializer.SerializeToElement(new { targetTime = DateTimeOffset.UtcNow }), 0, Guid.NewGuid());
        Assert.True(result.Success);
        var touched = Assert.IsAssignableFrom<IReadOnlySet<PlayerSectionKind>>(result.Snapshot.TouchedSections);
        Assert.Empty(touched);

        var packet = WebSocketEndpoint.BuildCommandResultPacket(
            session, "req-advance", Guid.NewGuid(), result, GameDataCatalog.BuiltIn, out var enqueued);
        Assert.True(enqueued);
        var receipt = JsonSerializer.Deserialize<CommandResultDto>(WireEnvelopeCodec.Decode(packet).Payload.Span, ProtocolJson.Options)!;
        Assert.Equal(CommandResultSnapshotForms.Delta, receipt.SnapshotForm);
        var delta = receipt.Snapshot!.Value.Deserialize<PlayerSnapshotDeltaDto>(ProtocolJson.Options)!;
        // 空差集下只有派生候选可进入增量：显式触及的分段一律不得出现。
        foreach (var section in delta.ChangedSections)
            Assert.Contains(section.Section, new[] { PlayerSectionKinds.Progression, PlayerSectionKinds.Combat, PlayerSectionKinds.World });
    }

    /// <summary>
    /// 锁内属性面板判定（WireAttributesChanged）跟随属性输入修订号：
    /// 加载期初始推进已建立面板基线实例，输入未变的命令命中解析缓存 → false；
    /// loadout.replace 重建装备列表递增输入版本、修订号变化 → 重新解析 → true；
    /// 该次解析回填缓存后，输入再次未变的命令 → false。
    /// </summary>
    [Fact]
    public async Task RuntimeWireAttributesChanged_TracksAttributeInputRevision()
    {
        var playerId = Guid.NewGuid();
        var repository = new FakeRepository(playerId);
        await using var runtime = EcsRuntimeFactory.Create(
            repository,
            new EmptyAssets(),
            new NoopEconomy(),
            GameDataCatalog.LoadEmbedded());

        await runtime.LoadAsync(playerId);
        // 每次入队使用独立 requestId：同请求 ID 会走幂等重放路径（快照不带本操作判定信息）。
        JsonElement EnqueuePayload(out Guid enqueueRequestId)
        {
            enqueueRequestId = Guid.NewGuid();
            return System.Text.Json.JsonSerializer.SerializeToElement(
                new ActionIntentRequest(enqueueRequestId.ToString("D"), "cultivation.draw-qi", 3), ProtocolJson.Options);
        }
        var first = await runtime.ExecuteAsync(playerId, "action.enqueue", EnqueuePayload(out var firstRequestId), 0, firstRequestId);
        Assert.True(first.Success);
        Assert.False(first.Snapshot.WireAttributesChanged);

        // 空装配替换同样重建装备版本列表：属性输入修订号严格增大，面板必须重新解析。
        var loadoutPayload = System.Text.Json.JsonSerializer.SerializeToElement(
            new { equipment = Array.Empty<object>(), abilitySlots = Array.Empty<object>() }, ProtocolJson.Options);
        var replaced = await runtime.ExecuteAsync(playerId, "loadout.replace", loadoutPayload, 0, Guid.NewGuid());
        Assert.True(replaced.Success);
        Assert.True(replaced.Snapshot.WireAttributesChanged);

        var third = await runtime.ExecuteAsync(playerId, "action.enqueue", EnqueuePayload(out var thirdRequestId), 0, thirdRequestId);
        Assert.True(third.Success);
        Assert.False(third.Snapshot.WireAttributesChanged);
    }

    /// <summary>
    /// world 静态投影缓存：同一配表实例两次编码产出逐位一致的分段校验和，
    /// 且默认节点投影字段（mapId/locationName/availableActionIds/生态清单）与配表内容一致。
    /// </summary>
    [Fact]
    public void WorldSection_StaticProjectionCache_ProducesStableContent()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var aggregate = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "世界", Baseline, "fp");
        var runtime = SnapshotOf(aggregate);
        var first = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", catalog);
        var second = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", catalog);
        var world1 = Assert.Single(first.Sections, s => s.Section == PlayerSectionKinds.World);
        var world2 = Assert.Single(second.Sections, s => s.Section == PlayerSectionKinds.World);
        Assert.Equal(world1.Checksum, world2.Checksum);
        Assert.Equal(world1.Data.GetRawText(), world2.Data.GetRawText());

        var root = world1.Data;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("mapId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("locationName").GetString()));
        Assert.Equal(aggregate.ExportState().World.NodeId, root.GetProperty("nodeId").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("availableActionIds").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("monsters").ValueKind);
        Assert.True(root.TryGetProperty("minBudget", out _));
        Assert.True(root.TryGetProperty("bossInterval", out _));
    }

    /// <summary>
    /// 并发多生产者有序投递：必达帧与可丢弃帧交错时，通道物理序必须严格等于序号递增序——
    /// 取号-编码-入队在会话锁内原子完成，杜绝「先取号后写入」的倒序窗口（客户端按序号丢帧）。
    /// </summary>
    [Fact]
    public async Task WebSocketSession_ConcurrentProducers_ChannelOrderMatchesSequence()
    {
        var session = new WebSocketSession(Guid.NewGuid(), capacity: 4096);
        const int producers = 8;
        const int framesPerProducer = 64;
        await Parallel.ForEachAsync(Enumerable.Range(0, producers), async (index, _) =>
        {
            for (var i = 0; i < framesPerProducer; i++)
            {
                if ((i + index) % 3 == 0)
                    session.TrySendJsonDroppable(WireMessageType.SnapshotDelta, Guid.NewGuid(), new { n = i });
                else
                    await session.SendJsonAsync(WireMessageType.CommandResult, Guid.NewGuid(), new { n = i }, CancellationToken.None);
            }
        });
        var sequences = new List<long>();
        while (session.Outbound.TryRead(out var packet)) sequences.Add(WireEnvelopeCodec.Decode(packet).Sequence);
        Assert.Equal(producers * framesPerProducer, sequences.Count);
        for (var i = 1; i < sequences.Count; i++)
            Assert.True(sequences[i] > sequences[i - 1], $"帧序号倒序：{sequences[i - 1]} -> {sequences[i]}");
    }

    /// <summary>
    /// 必达帧满队列时不丢弃而是阻塞等待背压（社交成功应答/命令回执/错误帧同语义）：
    /// 队列排空后按序送达且序号与通道物理序一致。
    /// </summary>
    [Fact]
    public async Task WebSocketSession_SendJsonAsync_FullQueue_BlocksThenDelivers()
    {
        var session = new WebSocketSession(Guid.NewGuid(), capacity: 1);
        Assert.True(session.TrySend([0]));
        var sendTask = session.SendJsonAsync(WireMessageType.SocialState, Guid.NewGuid(), new { ok = true }, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(sendTask.IsCompleted);
        Assert.True(session.Outbound.TryRead(out _));
        await sendTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Outbound.TryRead(out var packet));
        Assert.Equal(1, WireEnvelopeCodec.Decode(packet).Sequence);
    }

    /// <summary>
    /// 道号属身份标量而非分段：增量推送随帧透传当前 DisplayName，
    /// 改名无需等待全量快照即可下发到客户端。
    /// </summary>
    [Fact]
    public void BuildDelta_CarriesDisplayName()
    {
        var catalog = GameDataCatalog.BuiltIn;
        var aggregate = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "新道号", Baseline, "fp");
        var baselineDto = ProtocolSnapshotMapper.ToSnapshot(SnapshotOf(aggregate), "fp", catalog);
        var delta = ProtocolSnapshotMapper.BuildDelta(
            SnapshotOf(aggregate), "fp", catalog, null,
            ChecksumsOf(baselineDto.Sections), baselineDto.Assets,
            out _, out _);
        Assert.Equal("新道号", delta.DisplayName);
    }

    /// <summary>
    /// 队首 Pending→Running 零转移翻转（暂停队列推进）也算真实变更：
    /// 版本推进且附带推送——不计入会让客户端队列显示停在「排队中」直到下一次真实转移。
    /// </summary>
    [Fact]
    public async Task AdvancePush_PausedQueueHeadActivation_StillPushes()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId),
            new EmptyAssets(),
            new NoopEconomy(),
            GameDataCatalog.BuiltIn);
        var pushed = new List<PlayerRuntimeSnapshot>();
        runtime.SnapshotUpdated += snapshot =>
        {
            lock (pushed) pushed.Add(snapshot);
            return ValueTask.CompletedTask;
        };

        var initial = await runtime.LoadAsync(playerId);
        // 暂停队列后普通入队：非优先入队不翻转队首，行动停在 Pending。
        var pause = await runtime.ExecuteAsync(playerId, "queue.configure",
            JsonSerializer.SerializeToElement(new { paused = true }), 0, Guid.NewGuid());
        Assert.True(pause.Success);
        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(
            new ActionIntentRequest(requestId.ToString("D"), "cultivation.draw-qi", 1), ProtocolJson.Options);
        var enqueued = await runtime.ExecuteAsync(playerId, "action.enqueue", payload, 0, requestId);
        Assert.True(enqueued.Success);
        Assert.Equal(ActionQueueStatus.Pending, enqueued.Snapshot.State.Actions[0].Status);
        pushed.Clear();

        await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow.AddSeconds(30));
        var push = pushed.SingleOrDefault(s => s.PlayerId == playerId);
        Assert.NotNull(push);
        Assert.True(push!.StateVersion > initial.StateVersion);
        // 队首已翻转为 Running：ActionQueue 在触及差集内。
        Assert.Contains(PlayerSectionKind.ActionQueue,
            Assert.IsAssignableFrom<IReadOnlySet<PlayerSectionKind>>(push.TouchedSections));
    }

    /// <summary>
    /// 市场结算入账（对手方成交/退款/领取）：在线玩家缓存钱包失效后下一次推进重读余额、
    /// 推进版本并推送——资产差量由推送 delta 携带；版本推进不伪造分段脏（资产不属分段轴）。
    /// </summary>
    [Fact]
    public async Task AdvancePush_MarketWalletCredited_PushesNewVersion()
    {
        var playerId = Guid.NewGuid();
        var assets = new MutableAssets();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), assets, new NoopEconomy(), GameDataCatalog.BuiltIn);
        var pushed = new List<PlayerRuntimeSnapshot>();
        runtime.SnapshotUpdated += snapshot =>
        {
            lock (pushed) pushed.Add(snapshot);
            return ValueTask.CompletedTask;
        };

        var initial = await runtime.LoadAsync(playerId);
        // 市场结算把灵石计入余额：对手方无任何命令，仅缓存钱包被标记失效。
        assets.Rows = [new AssetBalanceRecord(playerId, "item.spirit-stone", "", 100, 1)];
        runtime.NotifyMarketWalletCredited(playerId);

        await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow.AddSeconds(30));
        var push = pushed.SingleOrDefault(s => s.PlayerId == playerId);
        Assert.NotNull(push);
        Assert.True(push!.StateVersion > initial.StateVersion);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlySet<PlayerSectionKind>>(push.TouchedSections));
    }

    /// <summary>单玩家假仓库。</summary>
    private sealed class FakeRepository(Guid playerId) : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle = new(
            new PlayerIdentity(playerId, Guid.NewGuid(), "触及", DateTimeOffset.UtcNow),
            new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new Dictionary<string, StoredSection>(), []);

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerStateBundle?>(_bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EmptyAssets : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    /// <summary>可变余额假仓库：市场入账测试按帧切换返回集。</summary>
    private sealed class MutableAssets : IAssetBalanceRepository
    {
        public IReadOnlyList<AssetBalanceRecord> Rows = [];
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Rows);
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }
}
