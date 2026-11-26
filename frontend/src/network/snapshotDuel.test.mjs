import '../../test-loader.mjs'
import test from 'node:test'
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'

const { useGameStore } = await import('../game/gameStore.ts')
const { decodePlayerSnapshotDeltaDto } = await import('./snapshot.ts')

test('斗法增量解码完整保留 duel.events 中的动画与结算字段', async () => {
  useGameStore.getState().resetGameStore()
  const seedSnapshot = {
    ...useGameStore.getState().toSnapshotView(),
    playerId: '50000000-0000-4000-8000-000000000001',
    version: 1,
  }
  useGameStore.getState().applySnapshot(seedSnapshot)
  const currentView = useGameStore.getState().toSnapshotView()

  const actionQueueData = [
    {
      id: 'act-duel-1',
      actionName: '讨伐测试',
      actionType: 'combat',
      executionKind: 'combat-encounter',
      combatEncounterId: 'encounter.test',
      status: 'running',
      intent: { actionId: 'combat.test' },
      expedition: {
        encounterId: 'encounter.test',
        phase: 'duel',
        phaseElapsedSeconds: 0,
        phaseDurationSeconds: 0,
        playerHitpoints: 92,
        playerManapoints: 48,
        seed: 42,
        battleElapsedSeconds: 1.5,
        playerNextAttackAtSeconds: 2.0,
        playerAttackCount: 1,
        playerThreat: 10,
        waveNumber: 1,
        playerRespawnAtSeconds: null,
        playerRespawnDurationSeconds: null,
        playerRespawnWaitingUntilSeconds: null,
        outcome: 0,
        enemies: [],
        difficulty: 0,
        playerBuffs: [],
        duel: {
          duelId: 'duel-test-1',
          opponentPlayerId: '50000000-0000-4000-8000-000000000002',
          opponentName: '乙',
          opponentAvatar: 'player',
          viewerIsInitiator: true,
          viewerOutcome: 0,
          resolved: false,
          forfeited: false,
          durationSeconds: 10,
          opponentHitpoints: 68,
          opponentManapoints: 40,
          opponentMaxHitpoints: 100,
          opponentMaxManapoints: 50,
          opponentAttributes: null,
          opponentSkills: null,
          opponentEquipment: null,
          events: [
            {
              atSeconds: 0.5,
              attackerIsInitiator: true,
              targetIsInitiator: false,
              abilityId: 'item.leiji-talisman',
              targetHitpointsAfter: 68,
              attackerManapointsAfter: 47,
              targetManapointsAfter: 40,
              hit: true,
              damage: 32,
              healing: 0,
              critical: true,
              broken: true,
              parried: false,
            },
            {
              atSeconds: 1.0,
              attackerIsInitiator: true,
              targetIsInitiator: true,
              abilityId: 'item.zhixue-powder',
              targetHitpointsAfter: 100,
              attackerManapointsAfter: 47,
              targetManapointsAfter: 47,
              hit: true,
              damage: 0,
              healing: 15,
              critical: false,
              broken: false,
              parried: true,
            },
          ],
        },
      },
    },
  ]

  const checksum = createHash('sha256')
    .update(Buffer.from(JSON.stringify(actionQueueData), 'utf8'))
    .digest('hex')
  const deltaDto = {
    playerId: '50000000-0000-4000-8000-000000000001',
    stateVersion: 2,
    lastSimulatedAt: '2026-11-17T00:00:01Z',
    changedSections: [
      {
        section: 'action_queue',
        sectionVersion: 1,
        schemaVersion: 43,
        minReaderSchemaVersion: 41,
        checksum,
        updatedAt: '2026-11-17T00:00:01Z',
        data: actionQueueData,
      },
    ],
  }
  const deltaBytes = new TextEncoder().encode(JSON.stringify(deltaDto))
  const patch = await decodePlayerSnapshotDeltaDto(deltaDto, currentView, deltaBytes, ['changedSections'])
  const duel = patch.queue?.entries[0]?.expedition?.duel
  assert.ok(duel)
  assert.equal(duel.events.length, 2)
  assert.deepEqual(duel.events[0], {
    atSeconds: 0.5,
    attackerIsInitiator: true,
    targetIsInitiator: false,
    attackerUnitIndex: 0,
    targetUnitIndex: 0,
    abilityId: 'item.leiji-talisman',
    targetHitpointsAfter: 68,
    attackerManapointsAfter: 47,
    targetManapointsAfter: 40,
    hit: true,
    damage: 32,
    healing: 0,
    critical: true,
    broken: true,
    parried: false,
  })
  assert.deepEqual(duel.events[1], {
    atSeconds: 1.0,
    attackerIsInitiator: true,
    targetIsInitiator: true,
    attackerUnitIndex: 0,
    targetUnitIndex: 0,
    abilityId: 'item.zhixue-powder',
    targetHitpointsAfter: 100,
    attackerManapointsAfter: 47,
    targetManapointsAfter: 47,
    hit: true,
    damage: 0,
    healing: 15,
    critical: false,
    broken: false,
    parried: true,
  })
})

test('多人斗法增量解码保留 selfUnitIndex、selfUnits、opponentUnits 与成员索引', async () => {
  useGameStore.getState().resetGameStore()
  const seedSnapshot = {
    ...useGameStore.getState().toSnapshotView(),
    playerId: '50000000-0000-4000-8000-000000000011',
    version: 1,
  }
  useGameStore.getState().applySnapshot(seedSnapshot)
  const currentView = useGameStore.getState().toSnapshotView()

  const actionQueueData = [
    {
      id: 'act-duel-team',
      actionName: '多人斗法测试',
      actionType: 'combat',
      executionKind: 'combat-encounter',
      combatEncounterId: 'encounter.test',
      status: 'running',
      intent: { actionId: 'combat.test' },
      expedition: {
        encounterId: 'encounter.test',
        phase: 'duel',
        phaseElapsedSeconds: 0,
        phaseDurationSeconds: 0,
        playerHitpoints: 90,
        playerManapoints: 45,
        seed: 77,
        battleElapsedSeconds: 1.0,
        playerNextAttackAtSeconds: 2.0,
        playerAttackCount: 1,
        playerThreat: 0,
        waveNumber: 1,
        playerRespawnAtSeconds: null,
        playerRespawnDurationSeconds: null,
        playerRespawnWaitingUntilSeconds: null,
        outcome: 0,
        enemies: [],
        difficulty: 0,
        playerBuffs: [],
        duel: {
          duelId: 'duel-team-1',
          opponentPlayerId: '50000000-0000-4000-8000-000000000021',
          opponentName: '乙首',
          opponentAvatar: 'player',
          viewerIsInitiator: true,
          viewerOutcome: 0,
          resolved: false,
          forfeited: false,
          durationSeconds: 12,
          opponentHitpoints: 70,
          opponentManapoints: 30,
          opponentMaxHitpoints: 100,
          opponentMaxManapoints: 50,
          selfUnitIndex: 1,
          selfUnits: [
            { playerId: '50000000-0000-4000-8000-000000000010', displayName: '甲首', avatar: 'player', hitpoints: 80, manapoints: 40, maxHitpoints: 100, maxManapoints: 50 },
            { playerId: '50000000-0000-4000-8000-000000000011', displayName: '甲副', avatar: 'player', hitpoints: 90, manapoints: 45, maxHitpoints: 100, maxManapoints: 50 },
          ],
          opponentUnits: [
            { playerId: '50000000-0000-4000-8000-000000000021', displayName: '乙首', avatar: 'player', hitpoints: 70, manapoints: 30, maxHitpoints: 100, maxManapoints: 50 },
            { playerId: '50000000-0000-4000-8000-000000000022', displayName: '乙副', avatar: 'player', hitpoints: 65, manapoints: 35, maxHitpoints: 100, maxManapoints: 50 },
          ],
          events: [
            {
              atSeconds: 0.8,
              attackerIsInitiator: true,
              targetIsInitiator: false,
              attackerUnitIndex: 1,
              targetUnitIndex: 1,
              abilityId: 'ability.heavy-slash',
              targetHitpointsAfter: 65,
              attackerManapointsAfter: 45,
              targetManapointsAfter: 35,
              hit: true,
              damage: 35,
              healing: 0,
              critical: false,
              broken: false,
              parried: false,
            },
          ],
        },
      },
    },
  ]

  const checksum = createHash('sha256')
    .update(Buffer.from(JSON.stringify(actionQueueData), 'utf8'))
    .digest('hex')
  const deltaDto = {
    playerId: '50000000-0000-4000-8000-000000000011',
    stateVersion: 2,
    lastSimulatedAt: '2026-11-26T00:00:01Z',
    changedSections: [
      {
        section: 'action_queue',
        sectionVersion: 1,
        schemaVersion: 50,
        minReaderSchemaVersion: 41,
        checksum,
        updatedAt: '2026-11-26T00:00:01Z',
        data: actionQueueData,
      },
    ],
  }
  const deltaBytes = new TextEncoder().encode(JSON.stringify(deltaDto))
  const patch = await decodePlayerSnapshotDeltaDto(deltaDto, currentView, deltaBytes, ['changedSections'])
  const duel = patch.queue?.entries[0]?.expedition?.duel
  assert.ok(duel)
  assert.equal(duel.selfUnitIndex, 1)
  assert.equal(duel.selfUnits?.length, 2)
  assert.equal(duel.opponentUnits?.length, 2)
  assert.equal(duel.events[0].attackerUnitIndex, 1)
  assert.equal(duel.events[0].targetUnitIndex, 1)
})
