using DaoHua.Ecs.Systems;

namespace Domain.Tests;

/// <summary>
/// 组队讨伐（总纲 §21.13）引擎级门禁：
/// 多盟友出手/倒地/复活/全灭判定、各成员视角推演逐位一致、快照往返不漂移。
/// 共享剧本确定性是组队共战的根基——任何一名成员的独立模拟必须产出同一场战斗。
/// </summary>
public sealed class CombatPartyExpeditionTests
{
    /// <summary>
    /// 多盟友按成员序出手：三个不同攻速的成员全部进入出手时间线，
    /// 敌方目标按全队威胁轮盘抽取（不再是单人恒打唯一目标）。
    /// </summary>
    [Fact]
    public void AllAlliesActOnSharedTimeline()
    {
        var allies = new[]
        {
            new CombatantTemplate(CombatantStats.Create(5_000, 50, 10, 1.0)),
            new CombatantTemplate(CombatantStats.Create(5_000, 50, 10, 0.8)),
            new CombatantTemplate(CombatantStats.Create(5_000, 50, 10, 1.2)),
        };
        var enemy = CombatantStats.Create(100_000, 100, 0, 10);
        var encounter = CombatEncounterState.CreateAllied("party.act", 31,
            allies, selfIndex: 1, [new CombatantTemplate(enemy)]);
        CombatActSystem.DecideInitialActions(encounter);

        var result = DeterministicCombatEngine.AdvanceTo(encounter, 5);
        Assert.Equal(3, encounter.PlayerAllies.Count);
        Assert.Equal("player", encounter.PlayerAllies[1].Id);
        Assert.Equal("ally:0", encounter.PlayerAllies[0].Id);
        Assert.Equal("ally:2", encounter.PlayerAllies[2].Id);
        // 三成员各有独立出手时刻表：出手计数都推进、出手事件都记录在案。
        Assert.All(encounter.PlayerAllies, ally => Assert.True(ally.AttackCount > 0));
        var attackerSet = result.Events.Select(e => e.AttackerId).ToHashSet();
        Assert.Contains("player", attackerSet);
        Assert.Contains("ally:0", attackerSet);
        Assert.Contains("ally:2", attackerSet);
    }

    /// <summary>
    /// 单名盟友倒地不影响全队：倒地成员退出目标轮盘，其余成员照常战斗；
    /// 倒地读条独立推进到各自复活点起身。
    /// </summary>
    [Fact]
    public void SingleAllyDownedDoesNotStopBattle()
    {
        // 盟友 1 气血薄且威胁增幅最高（101:1 吸火）会被秒杀，盟友 0 高血量站住：倒地窗口只覆盖盟友 1。
        var tanky = CombatantStats.Create(1_000_000, 100, 10, 5);
        var fragile = CombatantStats.Create(10, 100, 10, 5) with { ThreatBonusPoints = 10_000 };
        var allies = new[] { new CombatantTemplate(tanky), new CombatantTemplate(fragile) };
        var killer = CombatantStats.Create(100_000, 50, 0, 1);
        var encounter = CombatEncounterState.CreateAllied("party.downed", 41,
            allies, selfIndex: 0, [new CombatantTemplate(killer)], playerRespawnSeconds: 20);
        CombatActSystem.DecideInitialActions(encounter);

        var result = DeterministicCombatEngine.AdvanceTo(encounter, 10);
        // 盟友 1 倒地独立读条；盟友 0（本机）与战斗继续——不是全灭不暂停。
        Assert.False(encounter.PlayerAllies[1].IsAlive);
        Assert.True(double.IsFinite(encounter.PlayerAllies[1].RespawnAtSeconds));
        Assert.Equal(CombatOutcome.Running, encounter.Outcome);
        Assert.True(encounter.PlayerAllies[0].IsAlive);
        // 阵亡标记按成员下标记录（战报侧归因），本机位不计入 AllyDeathMarks。
        Assert.Contains(result.AllyDeathMarks ?? [], mark => mark.MemberIndex == 1);
    }

    /// <summary>
    /// 全灭分流与单人口径一致：原地复活遭遇全员倒地保持 Running 等最早复活点；
    /// 团灭即败遭遇（respawn=0）直接 EnemyWon。
    /// </summary>
    [Fact]
    public void FullWipeFollowsEncounterRuleNotParty()
    {
        var allies = new[]
        {
            new CombatantTemplate(CombatantStats.Create(10, 100, 10, 5)),
            new CombatantTemplate(CombatantStats.Create(10, 100, 10, 5)),
        };
        var killer = CombatantStats.Create(100_000, 50, 0, 0.5);

        // 原地复活：全员倒地 → Running，等待最早复活点。
        var respawnEncounter = CombatEncounterState.CreateAllied("party.wipe.respawn", 43,
            allies, selfIndex: 0, [new CombatantTemplate(killer)], playerRespawnSeconds: 30);
        CombatActSystem.DecideInitialActions(respawnEncounter);
        DeterministicCombatEngine.AdvanceTo(respawnEncounter, 10);
        Assert.True(respawnEncounter.IsAllAlliesDowned);
        Assert.Equal(CombatOutcome.Running, respawnEncounter.Outcome);
        // 两盟友同刻阵亡同刻复活：推进越过复活点产生起身标记（hp 过薄起身后会再被打倒，
        // 存活与否不作断言——复活语义以标记为准）。
        var revived = DeterministicCombatEngine.AdvanceTo(respawnEncounter, 60);
        Assert.NotEmpty(revived.AllyRespawnMarks ?? []);
        Assert.Contains(revived.AllyRespawnMarks!, mark => mark.MemberIndex == 1);
    }

    /// <summary>
    /// 团灭即败：respawn=0 时全员倒地立即 EnemyWon（与单人同口径，不因组队引入特殊规则）。
    /// </summary>
    [Fact]
    public void FullWipeWithoutRespawnIsTerminal()
    {
        var allies = new[]
        {
            new CombatantTemplate(CombatantStats.Create(10, 100, 10, 5)),
            new CombatantTemplate(CombatantStats.Create(10, 100, 10, 5)),
        };
        var killer = CombatantStats.Create(100_000, 50, 0, 0.5);
        var encounter = CombatEncounterState.CreateAllied("party.wipe.defeat", 47,
            allies, selfIndex: 1, [new CombatantTemplate(killer)], playerRespawnSeconds: 0);
        CombatActSystem.DecideInitialActions(encounter);
        var result = DeterministicCombatEngine.AdvanceTo(encounter, 10);
        Assert.Equal(CombatOutcome.EnemyWon, encounter.Outcome);
        Assert.Equal(CombatOutcome.EnemyWon, result.Outcome);
    }

    /// <summary>
    /// 确定性铁律：同一份盟友阵容+种子，selfIndex 0/1/2 三个视角各自推演——
    /// 力学字段（时刻/伤害/治疗/命中/暴击/技能/目标成员序）逐位一致；
    /// 唯一差异是单位 ID 标签（各视角下本机位恒为 "player"）。
    /// </summary>
    [Fact]
    public void SharedSpecReplaysIdenticallyFromEveryMemberView()
    {
        var allies = new[]
        {
            new CombatantTemplate(CombatantStats.Create(3_000, 80, 15, 1.0)),
            new CombatantTemplate(CombatantStats.Create(2_500, 90, 20, 0.9)),
            new CombatantTemplate(CombatantStats.Create(3_500, 70, 12, 1.1)),
        };
        var enemies = new[]
        {
            new CombatantTemplate(CombatantStats.Create(4_000, 30, 8, 0.7)),
            new CombatantTemplate(CombatantStats.Create(6_000, 45, 10, 1.3)),
        };

        var replays = new List<(CombatEncounterState Encounter, List<(double At, int Attacker, int Target, double Damage, double Healing, bool Hit, bool Crit, string? Ability)> Frames)>();
        for (var selfIndex = 0; selfIndex < 3; selfIndex++)
        {
            var encounter = CombatEncounterState.CreateAllied("party.replay", 59, allies, selfIndex, enemies);
            CombatActSystem.DecideInitialActions(encounter);
            var frames = new List<(double, int, int, double, double, bool, bool, string?)>();
            // 分片推进覆盖中途结算：逐 0.3s 推进攒事件。
            for (var t = 0.3; t <= 30; t += 0.3)
            {
                var step = DeterministicCombatEngine.AdvanceTo(encounter, t);
                foreach (var combatEvent in step.Events)
                    frames.Add((combatEvent.AtSeconds,
                        MemberOrdinal(encounter, combatEvent.AttackerId),
                        MemberOrdinal(encounter, combatEvent.TargetId),
                        combatEvent.Damage, combatEvent.Healing, combatEvent.Hit, combatEvent.Critical,
                        combatEvent.AbilityId));
            }
            replays.Add((encounter, frames));
        }

        // 三视角帧序列逐位一致：力学真值与时序完全相同。
        Assert.Equal(replays[0].Frames.Count, replays[1].Frames.Count);
        Assert.Equal(replays[0].Frames.Count, replays[2].Frames.Count);
        for (var i = 0; i < replays[0].Frames.Count; i++)
        {
            Assert.Equal(replays[0].Frames[i], replays[1].Frames[i]);
            Assert.Equal(replays[0].Frames[i], replays[2].Frames[i]);
        }
        // 终局各状态量一致：气血/灵力/出手计数/威胁逐成员相等。
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(replays[0].Encounter.PlayerAllies[i].Hitpoints, replays[1].Encounter.PlayerAllies[i].Hitpoints, 9);
            Assert.Equal(replays[0].Encounter.PlayerAllies[i].Manapoints, replays[2].Encounter.PlayerAllies[i].Manapoints, 9);
            Assert.Equal(replays[0].Encounter.PlayerAllies[i].AttackCount, replays[1].Encounter.PlayerAllies[i].AttackCount, 9);
            Assert.Equal(replays[0].Encounter.Outcome, replays[2].Encounter.Outcome);
        }
    }

    /// <summary>
    /// 快照往返：战斗中途导出盟友快照经 RestoreAllied 恢复后继续推进，
    /// 与一次连续推进的事件序列逐位一致（崩溃恢复不破确定性）。
    /// </summary>
    [Fact]
    public void SnapshotRestoreRoundTripsAlliedBattle()
    {
        var allies = new[]
        {
            new CombatantTemplate(CombatantStats.Create(8_000, 200, 25, 1.0)),
            new CombatantTemplate(CombatantStats.Create(8_000, 200, 25, 0.8)),
        };
        var enemies = new[]
        {
            new CombatantTemplate(CombatantStats.Create(20_000, 60, 5, 0.9)),
            new CombatantTemplate(CombatantStats.Create(20_000, 60, 5, 1.1)),
        };
        const long seed = 67;
        const int selfIndex = 1;

        // 连续推进参照系。
        var continuous = CombatEncounterState.CreateAllied("party.persist", seed, allies, selfIndex, enemies);
        CombatActSystem.DecideInitialActions(continuous);
        DeterministicCombatEngine.AdvanceTo(continuous, 7);
        var continuousTail = DeterministicCombatEngine.AdvanceTo(continuous, 20);

        // 同一时点快照→恢复→继续推进。
        var paused = CombatEncounterState.CreateAllied("party.persist", seed, allies, selfIndex, enemies);
        CombatActSystem.DecideInitialActions(paused);
        DeterministicCombatEngine.AdvanceTo(paused, 7);
        var allySnapshots = paused.PlayerAllies.Select(CombatEncounterState.Snapshot).ToArray();
        var enemySnapshots = paused.Enemies.Select(CombatEncounterState.Snapshot).ToArray();
        var restored = CombatEncounterState.RestoreAllied("party.persist", seed,
            allies, allySnapshots, selfIndex, enemies, enemySnapshots,
            paused.ElapsedSeconds, paused.Outcome, paused.Sequence, playerRespawnSeconds: 0);
        var restoredTail = DeterministicCombatEngine.AdvanceTo(restored, 20);

        Assert.Equal(continuousTail.Events.Count, restoredTail.Events.Count);
        for (var i = 0; i < continuousTail.Events.Count; i++)
            Assert.Equal(continuousTail.Events[i], restoredTail.Events[i]);
        Assert.Equal(continuous.Outcome, restored.Outcome);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(continuous.PlayerAllies[i].Hitpoints, restored.PlayerAllies[i].Hitpoints, 9);
            Assert.Equal(continuous.Enemies[i].Hitpoints, restored.Enemies[i].Hitpoints, 9);
        }
    }

    /// <summary>
    /// 聚合级端到端：唯一会话推演，两名玩家只消费各自结算投影——
    /// 双方击杀数与远征相位逐位一致（确定性铁律）、经验按在场人头平分、
    /// 共享掉落归属不重不漏（必掉品两钱包之和恒等于击杀数）。
    /// </summary>
    [Fact]
    public void SharedSessionProjectsPersonalRewardsWithoutDuplicateDrops()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var memberA = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", baseline, catalog.Fingerprint);
        var memberB = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "乙", baseline, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(memberA, catalog);
        PlayerAttributeResolver.Apply(memberB, catalog);
        // 冻结面板手工构造：Lv1 新号打不过 Lv3 木灵，组局成员面板给足强度保证波次推进。
        var memberStats = CombatantStats.Create(50_000, 2_000, 500, 1.0);
        ActionParticipantSnapshot MakeSnapshot(PlayerAggregate member, double dropBonus) => new(
            member.PlayerId.Value.ToString("D"), member.DisplayName, member.Progression.RealmLevel,
            dropBonus, 0, memberStats.MaxHitpoints, memberStats.MaxManapoints,
            memberStats, [], null, PlayerCombatSkillState.Empty);
        var snapshotA = MakeSnapshot(memberA, 0);
        var snapshotB = MakeSnapshot(memberB, 0);
        var spec = new ActionParticipationSpec(
            "session.test", "encounter.qi-beast", "node.test", 97,
            0, baseline.ToUnixTimeMilliseconds() / 1000d, [snapshotA, snapshotB])
        {
            ActionId = new ActionId("combat.hunt-qi-beast")
        };
        Assert.True(spec.IsValid);

        // 两端各自把会话行动插到队首（社交仲裁投递的领域等价物）。
        Assert.NotNull(memberA.StartActionParticipation(spec, baseline, catalog.Actions));
        Assert.NotNull(memberB.StartActionParticipation(spec, baseline, catalog.Actions));
        Assert.Equal(spec.ActionId, memberA.Actions[0].Intent.ActionId);
        Assert.Equal(spec.ActionId, memberB.Actions[0].Intent.ActionId);

        // AdvanceTo 内部以传入余额快照建独立钱包，产出经 SimulationResult.AssetDeltas 返回——
        // 逐段 Apply 回测试钱包，复现收益入账的真实口径。
        var walletA = new AssetWallet(memberA.PlayerId);
        var walletB = new AssetWallet(memberB.PlayerId);
        var target = baseline.AddSeconds(600);
        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var settlements = session.AdvanceTo(target);
        foreach (var delta in memberA.ApplyActionParticipationSettlement(settlements[0], catalog).AssetDeltas)
            walletA.Apply(delta);
        foreach (var delta in memberB.ApplyActionParticipationSettlement(settlements[1], catalog).AssetDeltas)
            walletB.Apply(delta);

        // 确定性铁律：两端推演同一场战斗——波次、相位、击杀数逐位一致。
        var expeditionA = memberA.Actions[0].Expedition!;
        var expeditionB = memberB.Actions[0].Expedition!;
        Assert.Equal(expeditionA.WaveNumber, expeditionB.WaveNumber);
        Assert.Equal(expeditionA.Phase, expeditionB.Phase);
        Assert.True(expeditionA.WaveNumber > 1,
            $"600s 推进应至少完成一波（phase={expeditionA.Phase} wave={expeditionA.WaveNumber} kills={memberA.Progression.NormalKills} outcome={expeditionA.Outcome} hp={memberA.Resources.Hitpoints}）");
        Assert.Equal(memberA.Progression.NormalKills, memberB.Progression.NormalKills);
        Assert.True(memberA.Progression.NormalKills > 0);

        // 经验平分：同境界成员各自拿 exp/N，两成员获得经验严格一致且大于 0。
        Assert.True(memberA.Progression.LifetimeExperience > 0);
        Assert.Equal(memberA.Progression.LifetimeExperience, memberB.Progression.LifetimeExperience, 6);

        // 共享掉落归属不重不漏：必掉品（掉率 1.0 ×1）的两钱包之和恒等于击杀数。
        var dropItem = new AssetKey(new ItemId("item.broken-spirit-core"));
        var dropsA = walletA.Get(dropItem);
        var dropsB = walletB.Get(dropItem);
        Assert.Equal(memberA.Progression.NormalKills, dropsA + dropsB);
        // 归属掷点覆盖两成员才说明分流真实生效（单次分布不强求，600s 击杀数足够两路都命中）。
        Assert.True(dropsA > 0 && dropsB > 0, $"归属应覆盖两成员（A={dropsA} B={dropsB}）");

        // 盟友镜像跨相位携带：本机位不进 Allies，对端成员镜像存在且 MemberIndex 正确。
        Assert.Single(expeditionA.Allies!);
        Assert.Single(expeditionB.Allies!);
        Assert.Equal(1, expeditionA.Allies![0].MemberIndex);
        Assert.Equal(0, expeditionB.Allies![0].MemberIndex);
        Assert.Equal(0, expeditionA.SelfIndex);
        Assert.Equal(1, expeditionB.SelfIndex);
    }

    /// <summary>
    /// 单人行动也可通过同一参与绑定入口：组织适配器不改变真实行动 ID，队列仍走普通讨伐执行器。
    /// </summary>
    [Fact]
    public void SingleMemberUsesSameActionParticipationPath()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var member = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "单人", baseline, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(member, catalog);
        var spec = new ActionParticipationSpec(
            "session.single", "encounter.qi-beast", "node.test", 101, 0,
            baseline.ToUnixTimeMilliseconds() / 1000d, [member.CaptureActionParticipantSnapshot(catalog)])
        {
            ActionId = new ActionId("combat.hunt-qi-beast")
        };

        Assert.True(spec.IsValid);
        Assert.NotNull(member.StartActionParticipation(spec, baseline, catalog.Actions));
        Assert.Single(member.Actions);
        Assert.Equal(spec.ActionId, member.Actions[0].Intent.ActionId);
        Assert.NotNull(member.Actions[0].Participation);
    }

    /// <summary>
    /// 阵法全队生效回归（总纲 §21.13）：TeamWide 定格的持续消耗品 Buff 经 PrepareCombatEntry
    /// 注入己方全部在场单位（按持有者定格剩余时长折算）；非阵法持续 Buff 仍只加使用者自身。
    /// </summary>
    [Fact]
    public void TeamWideSustainedBuffsApplyToWholeParty()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var memberA = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", baseline, catalog.Fingerprint);
        var memberB = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "乙", baseline, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(memberA, catalog);
        PlayerAttributeResolver.Apply(memberB, catalog);
        var memberStats = CombatantStats.Create(50_000, 2_000, 500, 1.0);
        ActionParticipantSnapshot MakeSnapshot(PlayerAggregate member) => new(
            member.PlayerId.Value.ToString("D"), member.DisplayName, member.Progression.RealmLevel,
            0, 0, memberStats.MaxHitpoints, memberStats.MaxManapoints,
            memberStats, [], null, PlayerCombatSkillState.Empty);
        // 甲的定格 Buff：阵法（TeamWide）+ 药品（仅自身）两类并存。
        var snapshotA = MakeSnapshot(memberA) with
        {
            SustainedBuffs =
            [
                new ActionSustainedBuff("formation.juling", 240, 1, "聚灵阵", TeamWide: true,
                    Effects: [new BuffEffect(BuffEffectChannel.MpRegen, 5), new BuffEffect(BuffEffectChannel.MpRegenPostRatio, 0.3)]),
                new ActionSustainedBuff("buff.tonic", 120, 1, "回春", TeamWide: false,
                    Effects: [new BuffEffect(BuffEffectChannel.HpRegen, 3)]),
            ]
        };
        var spec = new ActionParticipationSpec(
            "session.formation", "encounter.qi-beast", "node.test", 97,
            0, baseline.ToUnixTimeMilliseconds() / 1000d, [snapshotA, MakeSnapshot(memberB)])
        {
            ActionId = new ActionId("combat.hunt-qi-beast")
        };
        Assert.NotNull(memberA.StartActionParticipation(spec, baseline, catalog.Actions));

        var completedSearch = new CombatExpeditionState(
            "encounter.qi-beast", CombatExpeditionPhase.Search, 10, 8, 97, 0,
            memberA.Resources.Hitpoints, memberA.Resources.Manapoints, 0, 0,
            CombatantState.BaseThreat, CombatOutcome.Running, 1)
        {
            SelfIndex = 0
        };
        var preparation = CombatActionRules.PrepareCombatEntry(memberA, memberA.Actions[0],
            catalog.Actions.Get(spec.ActionId), catalog.CombatEncounters["encounter.qi-beast"],
            completedSearch, catalog, null, baseline.AddSeconds(10));

        var battle = preparation.Battle!;
        Assert.NotNull(battle);
        // 阵法散布全队：甲乙两单位都持有聚灵阵；定格剩余 240s 按会话墙钟 10s 折算为 230s。
        foreach (var unit in battle.PlayerAllies)
        {
            var formation = Assert.Single(unit.Buffs, buff => buff.BuffId == "formation.juling");
            Assert.Equal(230, formation.ExpiresAtSeconds - battle.ElapsedSeconds, 6);
            Assert.Equal(5, formation.EffectValue(BuffEffectChannel.MpRegen));
            Assert.Equal(0.3, formation.EffectValue(BuffEffectChannel.MpRegenPostRatio));
            Assert.True(formation.ResetOnWave);
        }
        // 非阵法 Buff 仍只加使用者：乙的 Buff 列表里没有 buff.tonic。
        Assert.Single(battle.PlayerAllies[0].Buffs, buff => buff.BuffId == "buff.tonic");
        Assert.DoesNotContain(battle.PlayerAllies[1].Buffs, buff => buff.BuffId == "buff.tonic");
    }

    /// <summary>
    /// 散场收尾：EndsAt 写入后推进越过该时刻，会话行动自动出队回到原队首；
    /// EndsAt 未到前会话行动推进战斗不散场。
    /// </summary>
    [Fact]
    public void SessionEndsAtDissolvesActionAndRestoresOriginal()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var memberA = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", baseline, catalog.Fingerprint);
        var memberB = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "乙", baseline, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(memberA, catalog);
        PlayerAttributeResolver.Apply(memberB, catalog);
        var spec = new ActionParticipationSpec(
            "session.end", "encounter.qi-beast", "node.test", 99,
            0, baseline.ToUnixTimeMilliseconds() / 1000d,
            [memberA.CaptureActionParticipantSnapshot(catalog), memberB.CaptureActionParticipantSnapshot(catalog)])
        {
            ActionId = new ActionId("combat.hunt-qi-beast")
        };

        // 成员 A 原本已有同一个讨伐行动；参与会话插队时不得因 ActionId 去重而丢失原行动。
        memberA.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1), baseline, catalog.Actions);
        var soloActionId = memberA.Actions[0].Id;
        Assert.NotNull(memberA.StartActionParticipation(spec, baseline, catalog.Actions));
        Assert.Equal(2, memberA.Actions.Count);
        Assert.Equal(spec.ActionId, memberA.Actions[0].Intent.ActionId);
        Assert.True(memberA.IsInActionParticipation);

        // 到期散场必须优先于个人暂停，否则参与行动会永久占据队首。
        memberA.SetQueuePaused(true);
        // 写入权威散场时刻（未来 30s），推进越过该时刻后参与行动出队、原行动回到队首。
        var endsAt = baseline.AddSeconds(30).ToUnixTimeMilliseconds() / 1000d;
        Assert.True(memberA.EndActionParticipation(spec.SessionId, endsAt));
        SimulationEngine.AdvanceTo(memberA, baseline.AddSeconds(60), catalog, new AssetWallet(memberA.PlayerId));
        Assert.False(memberA.IsInActionParticipation);
        Assert.Single(memberA.Actions);
        Assert.Equal(soloActionId, memberA.Actions[0].Id);
        Assert.Null(memberA.Actions[0].Participation);
    }

    /// <summary>
    /// 散场时刻倒地成员即刻以 1 点气血起身（§21.13）：写入权威结束时刻即恢复，
    /// 不依赖成员下次模拟推进——离线成员与紧随其后的新会话冻结面板都读到复活后气血；
    /// 灵力不恢复，参与绑定仍驻留队首待推进到散场时刻收尾。
    /// </summary>
    [Fact]
    public void SessionEndRevivesDownedMemberToOneHitpoint()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var memberA = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", baseline, catalog.Fingerprint);
        var memberB = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "乙", baseline, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(memberA, catalog);
        PlayerAttributeResolver.Apply(memberB, catalog);
        var spec = new ActionParticipationSpec(
            "session.downed", "encounter.qi-beast", "node.test", 99,
            0, baseline.ToUnixTimeMilliseconds() / 1000d,
            [memberA.CaptureActionParticipantSnapshot(catalog), memberB.CaptureActionParticipantSnapshot(catalog)])
        {
            ActionId = new ActionId("combat.hunt-qi-beast")
        };
        Assert.NotNull(memberA.StartActionParticipation(spec, baseline, catalog.Actions));

        // 会话中倒地：气血归零、灵力同样耗空（验证散场复活不碰灵力）。
        memberA.Resources.Hitpoints = 0;
        memberA.Resources.Manapoints = 0;
        var endsAt = baseline.AddSeconds(30).ToUnixTimeMilliseconds() / 1000d;
        Assert.True(memberA.EndActionParticipation(spec.SessionId, endsAt));
        Assert.Equal(1, memberA.Resources.Hitpoints);
        Assert.Equal(0, memberA.Resources.Manapoints);
        // 绑定仍在队首（推进到散场时刻才收尾出队），结束时刻已写入。
        Assert.True(memberA.Actions[0].Participation is not null);
        Assert.Equal(endsAt, memberA.Actions[0].Participation!.EndsAtUnixSeconds);

        SimulationEngine.AdvanceTo(memberA, baseline.AddSeconds(60), catalog, new AssetWallet(memberA.PlayerId));
        Assert.False(memberA.IsInActionParticipation);
        Assert.Empty(memberA.Actions);
        // 推进窗内气血自然回复会继续生效——断言只锁「已复活且未被再次清零」。
        Assert.True(memberA.Resources.Hitpoints > 0);
    }

    /// <summary>
    /// 倒地盟友跨波存续门禁（总纲 §7.9.1/§7.9.4）：波次清空时倒地盟友不瞬间回满，
    /// 以 0 气血（保留当前灵力）导出并随队进入新波次；新波次入场自动挂载复活倒计时，
    /// 并在复活时钟到期后正式复活起身。
    /// </summary>
    [Fact]
    public void DownedAllyPersistsIntoNextWaveWithoutInstantFullRevive()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var statsA = CombatantStats.Create(50_000, 10, 10, 1.0);
        var statsB = CombatantStats.Create(5_000, 100, 10, 5.0) with { ThreatBonusPoints = 10_000 };
        var allies = new[] { new CombatantTemplate(statsA), new CombatantTemplate(statsB) };
        var killer = CombatantStats.Create(5, 50_000, 0, 0.5);

        var encounterDef = catalog.CombatEncounters["encounter.qi-beast"] with { PlayerRespawnSeconds = 2 };
        var respawnSeconds = encounterDef.PlayerRespawnSeconds;
        var encounter = CombatEncounterState.CreateAllied("party.wave1", 101, allies, selfIndex: 0,
            [new CombatantTemplate(killer)], respawnSeconds);
        CombatActSystem.DecideInitialActions(encounter);

        // t=0.5 敌方出手击倒 B；t=1.0 A 反手击杀敌怪清空波次
        DeterministicCombatEngine.AdvanceTo(encounter, 1.5);
        Assert.Equal(CombatOutcome.PlayerWon, encounter.Outcome);
        Assert.True(encounter.PlayerAllies[0].IsAlive);
        Assert.False(encounter.PlayerAllies[1].IsAlive);
        Assert.Equal(0, encounter.PlayerAllies[1].Hitpoints);

        // 构造参与规格与成员 A 的聚合
        var memberA = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", baseline, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(memberA, catalog);
        var snapshotA = new ActionParticipantSnapshot(
            memberA.PlayerId.Value.ToString("D"), "甲", 1, 0, 0, statsA.MaxHitpoints, statsA.MaxManapoints, statsA, []);
        var snapshotB = new ActionParticipantSnapshot(
            Guid.NewGuid().ToString("D"), "乙", 1, 0, 0, statsB.MaxHitpoints, statsB.MaxManapoints, statsB, []);
        var spec = new ActionParticipationSpec(
            "session.carry", "encounter.qi-beast", "node.test", 101, 0,
            baseline.ToUnixTimeMilliseconds() / 1000d, [snapshotA, snapshotB])
        {
            ActionId = new ActionId("combat.hunt-qi-beast")
        };
        Assert.NotNull(memberA.StartActionParticipation(spec, baseline, catalog.Actions));

        // 波次清空导出：盟友镜像气血必须保持 0，严禁瞬间回满
        var completedSearch = new CombatExpeditionState(
            "encounter.qi-beast", CombatExpeditionPhase.Search, 10, 5, 101, 0,
            memberA.Resources.Hitpoints, memberA.Resources.Manapoints, 0, 0,
            CombatantState.BaseThreat, CombatOutcome.Running, 2)
        {
            SelfIndex = 0,
            Allies = [
                new ExpeditionAllyState(1, CombatEncounterState.Snapshot(encounter.PlayerAllies[1]),
                    CombatSkills: PlayerCombatSkillState.FromCombatant(encounter.PlayerAllies[1], encounter.ElapsedSeconds),
                    CombatBuffs: PlayerCombatBuffState.FromCombatant(encounter.PlayerAllies[1], encounter.ElapsedSeconds))
            ]
        };
        Assert.Equal(0, completedSearch.Allies[0].State.Hitpoints);

        // 搜寻完成，进入第 2 波战斗
        var prep = CombatActionRules.PrepareCombatEntry(memberA, memberA.Actions[0],
            catalog.Actions.Get(spec.ActionId), encounterDef, completedSearch, catalog, null, baseline.AddSeconds(10));
        var battle2 = prep.Battle!;
        Assert.NotNull(battle2);

        // 验证：B 在第 2 波开场时依然倒地，气血为 0，且挂载了复活倒计时（非满状态入场）
        Assert.True(battle2.PlayerAllies[0].IsAlive);
        Assert.False(battle2.PlayerAllies[1].IsAlive);
        Assert.Equal(0, battle2.PlayerAllies[1].Hitpoints);
        Assert.True(double.IsFinite(battle2.PlayerAllies[1].RespawnAtSeconds));
        Assert.Equal(respawnSeconds, battle2.PlayerAllies[1].RespawnAtSeconds, 6);

        // 推进第 2 波战斗至越过复活时钟，产生复活标记并正式起身
        var res2 = DeterministicCombatEngine.AdvanceTo(battle2, respawnSeconds + 0.1);
        Assert.NotEmpty(res2.AllyRespawnMarks ?? []);
        Assert.Contains(res2.AllyRespawnMarks!, mark => mark.MemberIndex == 1);
        Assert.True(battle2.PlayerAllies[1].IsAlive);
        Assert.Equal(statsB.MaxHitpoints, battle2.PlayerAllies[1].Hitpoints);
    }

    /// <summary>单位 ID → 成员序归一：self 位 "player"、盟友 "ally:{i}"、敌人 "enemy:{i}" 映射为整数序，跨视角比对用。</summary>
    private static int MemberOrdinal(CombatEncounterState encounter, string id)
    {
        if (id == "player") return encounter.SelfIndex;
        if (id.StartsWith("ally:", StringComparison.Ordinal) && int.TryParse(id[5..], out var ally)) return ally;
        if (id.StartsWith("enemy:", StringComparison.Ordinal) && int.TryParse(id[6..], out var enemy)) return 100 + enemy;
        return -1;
    }
}
