using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Ecs.Combat;
using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using YuoCore.Ecs;
using Xunit;

namespace Ecs.Tests;

/// <summary>相位本体化回归：恢复状态的组件视图、实体/内联派发与同刻命中反应顺序。</summary>
public sealed class CombatPhaseStateTests
{
    [Fact]
    public void CancellingCombatRemovesCapabilityViewsAndNewEncounterRebindsThem()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var player = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()),
            "战斗生命周期", now, catalog.Fingerprint);
        player.ReplaceCombatSlots([new PlayerAbilitySlot("ability.heavy-slash", 1, [])], []);
        using var runtime = new EcsGameRuntime(catalog, now);
        runtime.CreatePlayer(player);
        var actionId = Guid.NewGuid();
        runtime.EnqueueAction(player.PlayerId.Value, new ActionId("combat.hunt-qi-beast"), -1, actionId);
        var encounterId = runtime.CreateEncounter(player.PlayerId.Value, "encounter.twilight-pass", 77);
        var encounter = runtime.World.GetEntity(encounterId)!.GetComponent<CombatEncounterComponent>()!;
        var entity = runtime.World.GetEntity(encounter.PlayerEntityId)!;
        encounter.State.Player.RefreshStats(encounter.State.Player.Stats with
        {
            Effects = new CombatEffectProfile(HpDrainPoints: 20, MpDrainPoints: 20, DamageReflectPoints: 20)
        });
        CombatTriggerSurface.Sync(entity, encounter.State.Player);
        Assert.NotNull(entity.GetComponent<SkillStateComponent>());
        Assert.NotNull(entity.GetComponent<BuffComponent>());
        Assert.NotNull(entity.GetComponent<ThornsComponent>());

        Assert.True(runtime.RemoveAction(player.PlayerId.Value, actionId));
        Assert.Null(runtime.World.GetEntity(encounterId));
        Assert.Null(entity.GetComponent<CombatantComponent>());
        Assert.Null(entity.GetComponent<SkillStateComponent>());
        Assert.Null(entity.GetComponent<BuffComponent>());
        Assert.Null(entity.GetComponent<OnHitLeechComponent>());
        Assert.Null(entity.GetComponent<ManaLeechComponent>());
        Assert.Null(entity.GetComponent<ThornsComponent>());
        Assert.Null(entity.GetComponent<CombatTriggerInboxComponent>());

        runtime.EnqueueAction(player.PlayerId.Value, new ActionId("combat.hunt-qi-beast"), -1);
        var nextId = runtime.CreateEncounter(player.PlayerId.Value, "encounter.twilight-pass", 78);
        var next = runtime.World.GetEntity(nextId)!.GetComponent<CombatEncounterComponent>()!;
        Assert.NotSame(encounter.State.Player, next.State.Player);
        Assert.Same(next.State.Player, entity.GetComponent<CombatantComponent>()!.State);
        Assert.Same(next.State.Player.Skills, entity.GetComponent<SkillStateComponent>()!.Skills);
        Assert.Same(next.State.Player.Buffs, entity.GetComponent<BuffComponent>()!.Active);
        Assert.NotNull(entity.GetComponent<CombatTriggerInboxComponent>());
    }

    [Fact]
    public void RestoredSkillAndBuffViewsFollowAllSixEntityPhases()
    {
        using var abilityDocument = JsonDocument.Parse("""
            {"id":"ability.phase-support","category":"spell","targetType":"self",
             "castMilliseconds":250,"cooldownMilliseconds":60000,"manaCost":5,
             "buff":{"buffId":"buff.phase-support","durationSeconds":4.75,"attackSpeedPoints":100,"mpRegen":3}}
            """);
        var playerTemplate = new CombatantTemplate(
            CombatantStats.Create(100, 10, 0, 1) with { MaxManapoints = 100, MpRegen = 2 },
            [new SkillSlot(abilityDocument.RootElement.Clone(), 1)]);
        CombatantTemplate[] enemies = [new(CombatantStats.Create(100000, 1000, 0, 1))];
        var source = DeterministicCombatEngine.Start("phase-restore", 77, playerTemplate, enemies, 3);
        DeterministicCombatEngine.AdvanceTo(source, 0.3);
        Assert.Single(source.Player.Buffs);
        Assert.NotNull(source.Player.SkillCooldownUntil);

        CombatEncounterState Restore() => CombatEncounterState.Restore(source.EncounterId, source.Seed,
            playerTemplate, CombatEncounterState.Snapshot(source.Player), enemies,
            source.Enemies.Select(CombatEncounterState.Snapshot).ToArray(), source.ElapsedSeconds,
            source.Outcome, source.Sequence, source.PlayerRespawnSeconds, source.PlayerRespawnAtSeconds);
        var expected = Restore();
        var actual = Restore();
        using var runtime = new EcsGameRuntime(GameDataCatalog.LoadEmbedded());
        var host = AttachEncounter(runtime, actual);
        var playerEntity = runtime.World.GetEntity(host.PlayerEntityId)!;
        var skillView = playerEntity.GetComponent<SkillStateComponent>()!;
        var buffView = playerEntity.GetComponent<BuffComponent>()!;
        Assert.Same(actual.Player.Skills, skillView.Skills);
        Assert.Same(actual.Player.SkillCooldownUntil, skillView.CooldownUntil);
        Assert.Same(actual.Player.Buffs, buffView.Active);
        Assert.Single(buffView.Active);

        var expectedResult = DeterministicCombatEngine.AdvanceTo(expected, 5);
        var dispatch = new RecordingEntityDispatcher();
        var actualResult = DeterministicCombatEngine.AdvanceTo(actual, 5, dispatch, host);
        Assert.Equal(expectedResult.Events, actualResult.Events);
        Assert.Equal(expectedResult.PlayerDeathTimestamps, actualResult.PlayerDeathTimestamps);
        Assert.Equal(expectedResult.PlayerRespawnTimestamps, actualResult.PlayerRespawnTimestamps);
        Assert.NotEmpty(actualResult.PlayerDeathTimestamps!);
        Assert.NotEmpty(actualResult.PlayerRespawnTimestamps!);
        Assert.Equal(JsonSerializer.Serialize(CombatEncounterState.Snapshot(expected.Player)),
            JsonSerializer.Serialize(CombatEncounterState.Snapshot(actual.Player)));
        Assert.Equal(JsonSerializer.Serialize(CombatEncounterState.Snapshot(expected.Enemies[0])),
            JsonSerializer.Serialize(CombatEncounterState.Snapshot(actual.Enemies[0])));
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Empty(buffView.Active);
        Assert.Equal(actual.Player.PendingSkillIndex, skillView.PendingSkillIndex);
        Assert.Contains(typeof(ICombatBuffTickPhase), dispatch.Tags);
        Assert.Contains(typeof(ICombatPlayerRespawnPhase), dispatch.Tags);
        Assert.Contains(typeof(ICombatPlayerActPhase), dispatch.Tags);
        Assert.Contains(typeof(ICombatEnemyActPhase), dispatch.Tags);
        Assert.Contains(typeof(ICombatPlayerDownedPhase), dispatch.Tags);
        Assert.Contains(typeof(ICombatOutcomePhase), dispatch.Tags);
        Assert.Null(host.PhaseContext);

        // 下一遭遇换入权威状态后，原组件视图必须随之切换；刷新面板不能留下旧 Stats 副本。
        var replacement = Restore().Player;
        var combatant = playerEntity.GetComponent<CombatantComponent>()!;
        combatant.State = replacement;
        replacement.RefreshStats(replacement.Stats with { AttackPower = 123 });
        Assert.Same(replacement.Stats, combatant.Stats);
        Assert.Same(replacement.SkillCooldownUntil, skillView.CooldownUntil);
        Assert.Same(replacement.Buffs, buffView.Active);
        Assert.Single(buffView.Active);
    }

    [Fact]
    public void LethalReflectionSettlesBeforeLaterEnemyAtSameBoundary()
    {
        var player = CombatantStats.Create(20, 100, 0, 1);
        CombatantTemplate[] enemies =
        [
            new(CombatantStats.Create(100000, 1, 0, 1)
                with { Effects = new CombatEffectProfile(DamageReflectPoints: 100) }),
            new(CombatantStats.Create(100000, 1, 0, 1)
                with { Effects = new CombatEffectProfile(DamageReflectPoints: 100) })
        ];
        var expected = DeterministicCombatEngine.Start("same-time-reflect", 7, new CombatantTemplate(player), enemies);
        var actual = DeterministicCombatEngine.Start("same-time-reflect", 7, new CombatantTemplate(player), enemies);
        using var runtime = new EcsGameRuntime(GameDataCatalog.LoadEmbedded());
        var host = AttachEncounter(runtime, actual);
        var expectedResult = DeterministicCombatEngine.AdvanceTo(expected, 1);
        var actualResult = DeterministicCombatEngine.AdvanceTo(actual, 1, EcsCombatPhaseDispatcher.Instance, host);

        Assert.Equal(expectedResult.Events, actualResult.Events);
        Assert.Equal(2, actualResult.Events.Count);
        Assert.Equal("player", actualResult.Events[0].AttackerId);
        Assert.Equal("player", actualResult.Events[1].TargetId);
        Assert.Equal(0, actual.Player.Hitpoints);
        Assert.All(actual.Enemies, enemy => Assert.Equal(0, enemy.AttackCount));
        Assert.Equal(CombatOutcome.EnemyWon, actual.Outcome);
        Assert.Null(host.PhaseContext);
    }

    private static CombatEncounterComponent AttachEncounter(EcsGameRuntime runtime, CombatEncounterState state)
    {
        var player = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()),
            "相位装配", runtime.StartAt, runtime.GameData.Fingerprint);
        runtime.CreatePlayer(player);
        var entity = runtime.World.Entities.Values.Single(candidate =>
            candidate.GetComponent<PlayerIdentityComponent>()?.PlayerId == player.PlayerId.Value);
        // 与恢复入场相同的公开装配边界负责单位组件与实体触发器接线，测试不越过 Domain 可见性。
        return runtime.World.WorldMain!.GetComponent<EcsClockComponent>()!.AttachCombatBattle!(
            entity, state, runtime.StartAt);
    }

    private sealed class RecordingEntityDispatcher : ICombatPhaseDispatcher
    {
        public HashSet<Type> Tags { get; } = [];

        public void Dispatch<TTag>(CombatEncounterComponent? host, CombatPhaseContext context) where TTag : ISystemTag
        {
            Tags.Add(typeof(TTag));
            EcsCombatPhaseDispatcher.Instance.Dispatch<TTag>(host, context);
            Assert.Null(host!.PhaseContext);
        }
    }
}
