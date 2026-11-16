import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'
import { createHash } from 'node:crypto'

const require = createRequire(import.meta.url)
const esbuild = require('esbuild')
const esbuildDefine = {
  'import.meta.env': JSON.stringify({ DEV: false, VITE_APP_VERSION: 'test' }),
}

const gameStoreOut = path.join(tmpdir(), `daohua-duel-store-${Date.now()}.mjs`)
await esbuild.build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/gameStore.ts')],
  bundle: true,
  format: 'esm',
  outfile: gameStoreOut,
  define: esbuildDefine,
  logLevel: 'error',
})
const { useGameStore } = await import(pathToFileURL(gameStoreOut).href)

const snapshotOut = path.join(tmpdir(), `daohua-duel-snapshot-${Date.now()}.mjs`)
await esbuild.build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/network/snapshot.ts')],
  bundle: true,
  format: 'esm',
  outfile: snapshotOut,
  define: esbuildDefine,
  logLevel: 'error',
})
const { decodePlayerSnapshotDeltaDto } = await import(pathToFileURL(snapshotOut).href)

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
