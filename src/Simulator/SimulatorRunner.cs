using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Protocol.Simulator;

namespace DaoHua.Simulator;

/// <summary>
/// 战斗预演运行器：与实战同源的确定性讨伐推演。
/// 单人经 <see cref="SimulationEngine.AdvanceTo(PlayerAggregate, DateTimeOffset, GameDataCatalog, AssetWallet?, DuelAdvanceOptions?, ConsumableScheduleMode)"/>
/// 复用真实行动/战斗管线；多人经 <see cref="ActionParticipationSessionEngine"/> 复用组队会话结算。
/// 每个请求都从输入副本独立开始，绝不回写输入；外部环境按快照冻结，无 PVP 撮合。
/// </summary>
public static class SimulatorRunner
{
    /// <summary>推演时长下限（秒）。</summary>
    public const double MinDurationSeconds = 1;
    /// <summary>推演时长上限（秒，24 小时）。</summary>
    public const double MaxDurationSeconds = 86400;

    /// <summary>显式自定义新角色：以真实默认聚合导出档案，不冒充任何玩家导入。</summary>
    public static SimulatorProfileDto CreateDefaultProfile(GameDataCatalog? catalog = null)
    {
        catalog ??= GameDataCatalog.LoadEmbedded();
        var state = PlayerAggregate.Create(
            new PlayerId(new Guid("51530000-0000-0000-0000-000000000001")),
            new AccountId(new Guid("51530000-0000-0000-0000-000000000001")),
            "自定义角色",
            DateTimeOffset.UnixEpoch,
            catalog.Fingerprint).ExportState();
        return SimulatorProfileMapper.Capture(state, [], catalog) with { SourcePlayerId = null };
    }

    /// <summary>对档案 + 目标节点建筑投影执行真实属性解析，返回只读预览面板。</summary>
    public static SimulatorResolveResultDto ResolveProfiles(SimulatorResolveRequestDto request, GameDataCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gameData = catalog ?? GameDataCatalog.LoadEmbedded();
        ValidateCommon(request.FormatVersion, request.Environment, request.Members, request.NodeId, gameData);
        var environmentNode = RequireEnvironmentNode(request.Environment, request.NodeId);
        var resolved = new SimulatorResolvedProfileDto[request.Members.Count];
        for (var i = 0; i < request.Members.Count; i++)
        {
            var state = SimulatorProfileMapper.RestoreToState(
                request.Members[i], i, request.NodeId, environmentNode, gameData, request.Environment.CapturedAtUnixSeconds);
            var attributes = PlayerAttributeResolver.Resolve(state, gameData);
            resolved[i] = new SimulatorResolvedProfileDto(
                i,
                state.PlayerId.Value.ToString("D"),
                state.DisplayName,
                JsonSerializer.SerializeToElement(attributes, SimulatorJson.Options),
                JsonSerializer.SerializeToElement(attributes.ToCombatant(state.Progression.CombatExperience), SimulatorJson.Options));
        }
        return new SimulatorResolveResultDto(SimulatorContractVersions.Result, request.NodeId, resolved);
    }

    /// <summary>执行一次目标地点讨伐推演：1~5 人、难度 0~5、时长 1 秒~24 小时、显式种子。</summary>
    public static SimulatorRunResultDto Run(SimulatorRunRequestDto request, GameDataCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gameData = catalog ?? GameDataCatalog.LoadEmbedded();
        ValidateCommon(request.FormatVersion, request.Environment, request.Members, request.NodeId, gameData);
        if (request.Difficulty < 0 || request.Difficulty > 5)
            throw new DomainException(DomainErrorCode.InvalidArgument, "讨伐难度必须位于 0~5。");
        if (!double.IsFinite(request.DurationSeconds)
            || request.DurationSeconds < MinDurationSeconds || request.DurationSeconds > MaxDurationSeconds)
            throw new DomainException(DomainErrorCode.InvalidArgument, "推演时长必须位于 1~86400 秒。");
        if (!long.TryParse(request.Seed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seed))
            throw new DomainException(DomainErrorCode.InvalidArgument, "推演种子必须是 64 位整数十进制字符串。");
        var environmentNode = RequireEnvironmentNode(request.Environment, request.NodeId);
        var action = ResolveCombatAction(request.NodeId, gameData);
        var encounter = ResolveEncounter(request.NodeId, seed, gameData);
        var startAtUnixSeconds = request.Environment.CapturedAtUnixSeconds;
        var startAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(startAtUnixSeconds * 1000d));
        // 目标时刻按 tick 精确换算：DateTime.AddSeconds 的 AddUnits 会先取整部/分部再截断分部，
        // 大秒数（如 20.8264462）会因分部浮点误差少计 1 tick，与 ECS 逐窗口边界口径产生尾差。
        var targetAt = startAt.AddTicks((long)(request.DurationSeconds * TimeSpan.TicksPerSecond + 0.5));
        var members = request.Members.Count == 1
            ? [RunSolo(request.Members[0], request, environmentNode, action, encounter, seed, gameData, startAt, targetAt)]
            : RunParty(request, environmentNode, action, encounter, seed, gameData, startAt, targetAt);
        return new SimulatorRunResultDto(
            SimulatorContractVersions.Result,
            request.NodeId,
            request.Difficulty,
            request.Seed,
            request.DurationSeconds,
            request.DurationSeconds,
            members);
    }

    /// <summary>显式自定义新角色（JSON 入口）。</summary>
    public static string CreateDefaultProfileJson()
        => JsonSerializer.Serialize(CreateDefaultProfile(), SimulatorJson.Options);

    /// <summary>属性解析预览（JSON 入口）。</summary>
    public static string ResolveProfilesJson(string requestJson)
    {
        var request = JsonSerializer.Deserialize<SimulatorResolveRequestDto>(requestJson, SimulatorJson.Options)
            ?? throw new DomainException(DomainErrorCode.InvalidArgument, "属性预览请求不是合法的 JSON 载荷。");
        return JsonSerializer.Serialize(ResolveProfiles(request), SimulatorJson.Options);
    }

    /// <summary>讨伐推演（JSON 入口）。</summary>
    public static string RunJson(string requestJson)
    {
        var request = JsonSerializer.Deserialize<SimulatorRunRequestDto>(requestJson, SimulatorJson.Options)
            ?? throw new DomainException(DomainErrorCode.InvalidArgument, "推演请求不是合法的 JSON 载荷。");
        return JsonSerializer.Serialize(Run(request), SimulatorJson.Options);
    }

    /// <summary>嵌入式配表指纹；浏览器与服务端据此校验同一编译内容版本。</summary>
    public static string CatalogFingerprint(GameDataCatalog? catalog = null)
        => (catalog ?? GameDataCatalog.LoadEmbedded()).Fingerprint;

    /// <summary>嵌入式配表指纹（JSON 入口）。</summary>
    public static string CatalogFingerprintJson()
        => JsonSerializer.Serialize(new { fingerprint = CatalogFingerprint() }, SimulatorJson.Options);

    private static SimulatorMemberResultDto RunSolo(
        SimulatorProfileDto profile,
        SimulatorRunRequestDto request,
        SimulatorEnvironmentNodeDto environmentNode,
        ActionDefinition action,
        CombatEncounterDefinition encounter,
        long seed,
        GameDataCatalog gameData,
        DateTimeOffset startAt,
        DateTimeOffset targetAt)
    {
        var state = SimulatorProfileMapper.RestoreToState(
            profile, 0, request.NodeId, environmentNode, gameData, request.Environment.CapturedAtUnixSeconds);
        var player = PlayerAggregate.Restore(state);
        var wallet = SimulatorProfileMapper.BuildWallet(player.PlayerId, profile);
        var queued = player.Enqueue(
            new ActionIntent(action.Id, -1, Difficulty: request.Difficulty), startAt, gameData.Actions);
        // 显式种子钉死首波搜寻：遭遇选自目标节点生态池（与组队开战同一选择语义），波次与战斗抽样共用该种子。
        ((IPlayerActionState)queued).Expedition = SimulationEngine.CreateCombatSearchState(
            player, queued, encounter, CombatExpeditionPhase.InitialSearch, gameData, seed);
        var result = SimulationEngine.AdvanceTo(player, targetAt, gameData, wallet);
        var finalState = player.ExportState();
        var report = player.Duel.ReportFor(queued.Id);
        var expedition = queued.Expedition;
        return new SimulatorMemberResultDto(
            0,
            player.PlayerId.Value.ToString("D"),
            finalState.DisplayName,
            finalState.Combat.Hitpoints,
            finalState.Combat.MaxHitpoints,
            finalState.Combat.Manapoints,
            finalState.Combat.MaxManapoints,
            finalState.Combat.RegenCarrySeconds,
            SimulatorProfileMapper.ToDto(finalState.Combat.Skills),
            finalState.Combat.Buffs.Buffs.Select(SimulatorProfileMapper.ToDto).ToArray(),
            queued.CompletedRepeats,
            expedition?.WaveNumber ?? 1,
            expedition?.Phase.ToString() ?? CombatExpeditionPhase.InitialSearch.ToString(),
            report?.Experience ?? 0,
            report?.Foundation ?? 0,
            report?.CombatExperience ?? 0,
            finalState.Progression.LifetimeExperience - profile.LifetimeExperience,
            report is null
                ? new SimulatorRootExpDto(0, 0, 0, 0, 0, 0)
                : new SimulatorRootExpDto(report.RootExp.Stamina, report.RootExp.Meridians,
                    report.RootExp.Attack, report.RootExp.Bone, report.RootExp.Agility, report.RootExp.Consciousness),
            report?.Cycles ?? 0,
            report?.Encounters ?? 0,
            report?.NormalKills ?? 0,
            report?.EliteKills ?? 0,
            report?.BossKills ?? 0,
            report?.Deaths ?? 0,
            (report?.Items ?? []).Select(item => new SimulatorReportItemDto(item.ItemId, "", item.Quantity)).ToArray(),
            (report?.ItemsLost ?? []).Select(item => new SimulatorReportItemDto(item.ItemId, "", item.Quantity)).ToArray(),
            result.AssetDeltas.Select(SimulatorProfileMapper.ToDto).ToArray(),
            RebuildRemainingAssets(profile.Assets, result.AssetDeltas),
            (finalState.Settings.AutoUseSlots ?? [])
                .Where(slot => slot.ActionType == AutoUseBar.Combat)
                .Select(SimulatorProfileMapper.ToDto).ToArray(),
            report is null || report.CombatStats.Entries.Count == 0
                ? []
                : report.CombatStats.Entries.Select(unit => new SimulatorCombatUnitStatDto(
                    unit.UnitKey, (int)unit.Kind, unit.DisplayName,
                    unit.DamageDealtPhysical, unit.DamageDealtSpell,
                    unit.DamageTakenPhysical, unit.DamageTakenSpell,
                    unit.DamageMitigatedPhysical, unit.DamageMitigatedSpell,
                    unit.HealingRegen, unit.HealingOther, unit.DamageDealtTrue, unit.DamageTakenTrue)).ToArray(),
            ToEnemyDtos(expedition),
            result.Events.Select(SimulatorProfileMapper.ToDto).ToArray(),
            result.DroppedEventCount);
    }

    private static SimulatorMemberResultDto[] RunParty(
        SimulatorRunRequestDto request,
        SimulatorEnvironmentNodeDto environmentNode,
        ActionDefinition action,
        CombatEncounterDefinition encounter,
        long seed,
        GameDataCatalog gameData,
        DateTimeOffset startAt,
        DateTimeOffset targetAt)
    {
        var memberStates = new PlayerAggregateState[request.Members.Count];
        var snapshots = new ActionParticipantSnapshot[request.Members.Count];
        for (var i = 0; i < request.Members.Count; i++)
        {
            memberStates[i] = SimulatorProfileMapper.RestoreToState(
                request.Members[i], i, request.NodeId, environmentNode, gameData, request.Environment.CapturedAtUnixSeconds);
            snapshots[i] = ActionParticipantSnapshot.FromState(memberStates[i], gameData);
        }
        var sessionId = $"action:{DeriveSessionGuid(seed, request.NodeId, request.Difficulty):N}";
        var spec = new ActionParticipationSpec(
            sessionId,
            encounter.Id,
            request.NodeId,
            seed,
            snapshots.Average(member => member.SearchSpeedPoints),
            request.Environment.CapturedAtUnixSeconds,
            snapshots)
        {
            ActionId = action.Id,
            PvpEnabled = false,
            MonsterWeakenRatio = SimulatorProfileMapper.ToDomain(environmentNode.Effects).MonsterWeakenRatio,
            Difficulty = request.Difficulty
        };
        if (spec.Members.Any(member => !member.IsValid))
        {
            var invalidIndexes = string.Join(',', spec.Members
                .Select((member, index) => (member, index))
                .Where(entry => !entry.member.IsValid)
                .Select(entry => entry.index));
            throw new DomainException(DomainErrorCode.InvalidArgument,
                $"预演会话成员冻结快照无效：成员下标 {invalidIndexes}。");
        }
        if (!spec.IsValid)
            throw new DomainException(DomainErrorCode.InvalidArgument,
                $"预演会话规格不合法（行动={spec.ActionId.IsValid}，遭遇={spec.EncounterId}，寻敌速度={spec.SearchSpeedPoints}）。");
        var workingWallet = memberStates
            .Select((state, index) => SimulatorProfileMapper.BuildWorkingWallet(state.PlayerId, request.Members[index]))
            .SelectMany(balances => balances).ToArray();
        var engine = ActionParticipationSessionEngine.Create(spec, gameData, workingWallet);
        var settlements = engine.AdvanceTo(targetAt);
        var assetDeltas = engine.TakeAssetDeltas();
        var results = new SimulatorMemberResultDto[request.Members.Count];
        for (var i = 0; i < request.Members.Count; i++)
        {
            var memberPlayer = PlayerAggregate.Restore(memberStates[i]);
            var queued = memberPlayer.StartActionParticipation(spec, startAt, gameData.Actions)
                ?? throw new DomainException(DomainErrorCode.InvariantViolation, "预演会话成员绑定失败。");
            var playerId = memberPlayer.PlayerId;
            var memberDeltas = new List<AssetDelta>();
            var memberEvents = new List<SimulationEvent>();
            var droppedEvents = 0L;
            foreach (var settlement in settlements.Where(entry => entry.PlayerId == playerId).OrderBy(entry => entry.Sequence))
            {
                var applied = memberPlayer.ApplyActionParticipationSettlement(settlement, gameData);
                memberDeltas.AddRange(applied.AssetDeltas);
                memberEvents.AddRange(applied.Events);
                droppedEvents += applied.DroppedEventCount;
            }
            memberDeltas.AddRange(assetDeltas.Where(delta => delta.PlayerId == playerId));
            var finalState = memberPlayer.ExportState();
            var report = memberPlayer.Duel.ReportFor(queued.Id);
            var expedition = finalState.Actions[0].Expedition;
            var core = finalState.Loadout.CoreAttributes;
            var baseCore = request.Members[i].CoreAttributes;
            var remaining = RebuildRemainingAssets(request.Members[i].Assets, memberDeltas);
            results[i] = new SimulatorMemberResultDto(
                i,
                playerId.Value.ToString("D"),
                finalState.DisplayName,
                finalState.Combat.Hitpoints,
                finalState.Combat.MaxHitpoints,
                finalState.Combat.Manapoints,
                finalState.Combat.MaxManapoints,
                finalState.Combat.RegenCarrySeconds,
                SimulatorProfileMapper.ToDto(finalState.Combat.Skills),
                finalState.Combat.Buffs.Buffs.Select(SimulatorProfileMapper.ToDto).ToArray(),
                queued.CompletedRepeats,
                expedition?.WaveNumber ?? 1,
                expedition?.Phase.ToString() ?? CombatExpeditionPhase.InitialSearch.ToString(),
                finalState.Progression.Experience - request.Members[i].Experience,
                finalState.Progression.Foundation - request.Members[i].Foundation,
                finalState.Progression.CombatExperience - request.Members[i].CombatExperience,
                finalState.Progression.LifetimeExperience - request.Members[i].LifetimeExperience,
                new SimulatorRootExpDto(
                    core.StaminaExp - baseCore.StaminaExp, core.MeridiansExp - baseCore.MeridiansExp,
                    core.AttackExp - baseCore.AttackExp, core.BoneExp - baseCore.BoneExp,
                    core.AgilityExp - baseCore.AgilityExp, core.ConsciousnessExp - baseCore.ConsciousnessExp),
                report?.Cycles ?? 0,
                report?.Encounters ?? 0,
                report?.NormalKills ?? 0,
                report?.EliteKills ?? 0,
                report?.BossKills ?? 0,
                report?.Deaths ?? 0,
                (report?.Items ?? []).Select(item => new SimulatorReportItemDto(item.ItemId, "", item.Quantity)).ToArray(),
                (report?.ItemsLost ?? []).Select(item => new SimulatorReportItemDto(item.ItemId, "", item.Quantity)).ToArray(),
                memberDeltas.Select(SimulatorProfileMapper.ToDto).ToArray(),
                remaining,
                (finalState.Settings.AutoUseSlots ?? [])
                    .Where(slot => slot.ActionType == AutoUseBar.Combat)
                    .Select(SimulatorProfileMapper.ToDto).ToArray(),
                report is null || report.CombatStats.Entries.Count == 0
                    ? []
                    : report.CombatStats.Entries.Select(unit => new SimulatorCombatUnitStatDto(
                        unit.UnitKey, (int)unit.Kind, unit.DisplayName,
                        unit.DamageDealtPhysical, unit.DamageDealtSpell,
                        unit.DamageTakenPhysical, unit.DamageTakenSpell,
                        unit.DamageMitigatedPhysical, unit.DamageMitigatedSpell,
                        unit.HealingRegen, unit.HealingOther, unit.DamageDealtTrue, unit.DamageTakenTrue)).ToArray(),
                ToEnemyDtos(expedition),
                memberEvents.Select(SimulatorProfileMapper.ToDto).ToArray(),
                droppedEvents);
        }
        return results;
    }

    private static SimulatorAssetDto[] RebuildRemainingAssets(
        IReadOnlyList<SimulatorAssetDto> initial, IReadOnlyList<AssetDelta> deltas)
    {
        var remaining = new Dictionary<(string ItemId, string Variant), double>();
        foreach (var asset in initial)
            remaining[(asset.ItemId, asset.Variant ?? string.Empty)] =
                remaining.GetValueOrDefault((asset.ItemId, asset.Variant ?? string.Empty)) + asset.Quantity;
        foreach (var delta in deltas)
        {
            var key = (delta.Asset.ItemId.Value, delta.Asset.Variant);
            remaining[key] = remaining.GetValueOrDefault(key) + delta.QuantityDelta;
        }
        return remaining.Where(pair => pair.Value > 0)
            .Select(pair => new SimulatorAssetDto(pair.Key.ItemId, pair.Key.Variant, pair.Value)).ToArray();
    }

    private static void ValidateCommon(
        int formatVersion,
        SimulatorEnvironmentDto environment,
        IReadOnlyList<SimulatorProfileDto> members,
        string nodeId,
        GameDataCatalog gameData)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(members);
        if (formatVersion != SimulatorContractVersions.Request || environment.FormatVersion != SimulatorContractVersions.Environment)
            throw new DomainException(DomainErrorCode.InvalidArgument, "预演契约版本不匹配。");
        if (!string.Equals(environment.GameDataFingerprint, gameData.Fingerprint, StringComparison.Ordinal))
            throw new DomainException(DomainErrorCode.InvalidArgument, "环境快照配表指纹与当前配表不一致。");
        if (members.Count < 1 || members.Count > 5)
            throw new DomainException(DomainErrorCode.InvalidArgument, "推演成员数必须位于 1~5。");
        var memberIds = new Guid[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (member is null || member.FormatVersion != SimulatorContractVersions.Profile)
                throw new DomainException(DomainErrorCode.InvalidArgument, "角色档案契约版本不匹配。");
            if (!string.Equals(member.GameDataFingerprint, gameData.Fingerprint, StringComparison.Ordinal))
                throw new DomainException(DomainErrorCode.InvalidArgument, "角色档案配表指纹与当前配表不一致。");
            memberIds[i] = SimulatorInputValidator.NormalizeMemberId(member, i);
            if (Array.IndexOf(memberIds, memberIds[i], 0, i) >= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "推演成员包含重复角色。");
            SimulatorInputValidator.ValidateMember(member, i, gameData);
        }
        SimulatorInputValidator.ValidateRespawnTargets(members, memberIds);
        SimulatorInputValidator.ValidateEnvironment(environment, gameData);
        if (!gameData.WorldGraph.TryGetNode(nodeId, out var node) || node.EncounterIds.Count == 0)
            throw new DomainException(DomainErrorCode.NotFound, "目标节点不存在或没有讨伐遭遇池。");
    }

    private static SimulatorEnvironmentNodeDto RequireEnvironmentNode(
        SimulatorEnvironmentDto environment, string nodeId)
    {
        var node = environment.Nodes.FirstOrDefault(entry => string.Equals(entry.NodeId, nodeId, StringComparison.Ordinal));
        return node ?? throw new DomainException(DomainErrorCode.InvalidArgument, "环境快照缺少目标节点条目，拒绝推演。");
    }

    /// <summary>与组队开战同一口径的行动选择：战斗遭遇类行动中按节点白名单过滤后取 ID 字典序首个。</summary>
    private static ActionDefinition ResolveCombatAction(string nodeId, GameDataCatalog gameData)
        => gameData.Actions.All
            .Where(definition => definition.Kind == ActionKind.Combat
                && definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                && (definition.AllowedNodeIds is null || definition.AllowedNodeIds.Contains(nodeId))
                && (definition.ExcludedNodeIds is null || !definition.ExcludedNodeIds.Contains(nodeId)))
            .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new DomainException(DomainErrorCode.NotFound, "目标节点没有可用讨伐行动。");

    /// <summary>与组队开战同一口径的遭遇钉死：种子对节点生态池取模选择。</summary>
    private static CombatEncounterDefinition ResolveEncounter(string nodeId, long seed, GameDataCatalog gameData)
    {
        var node = gameData.WorldGraph.TryGetNode(nodeId, out var found) ? found : null;
        var pool = node?.EncounterIds ?? [];
        var encounterId = pool[(int)((ulong)seed % (ulong)pool.Count)];
        return gameData.CombatEncounters.TryGetValue(encounterId, out var encounter)
            ? encounter
            : throw new DomainException(DomainErrorCode.NotFound, "目标节点遭遇池引用了缺失的遭遇定义。");
    }

    private static SimulatorExpeditionEnemyDto[] ToEnemyDtos(CombatExpeditionState? expedition)
        => expedition?.Enemies is { Count: > 0 } enemies
            ? enemies.Select(enemy => new SimulatorExpeditionEnemyDto(
                enemy.EnemyId, enemy.State.Hitpoints, enemy.State.Manapoints)).ToArray()
            : [];

    private static Guid DeriveSessionGuid(long seed, string nodeId, int difficulty)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes($"simulator:{seed}:{nodeId}:{difficulty}"));
        return new Guid(bytes[..16]);
    }
}
