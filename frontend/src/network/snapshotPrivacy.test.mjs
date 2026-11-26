import '../../test-loader.mjs'
import test from 'node:test'
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { SNAPSHOT_SCHEMA_VERSION } from './snapshotSchemaGate.ts'

const { useGameStore } = await import('../game/gameStore.ts')
const { decodePlayerSnapshotDeltaDto } = await import('./snapshot.ts')

test('名片隐私与快照 Schema：版本对齐且 gameStore 默认公开配装、增量补丁可准确切换并随 reset 复位', async () => {
  const codec = await readFile(new URL('../../../src/Domain/Persistence/SectionCodec.cs', import.meta.url), 'utf8')
  const serverSchema = /public const int CurrentSchemaVersion = (\d+);/.exec(codec)
  assert.ok(serverSchema, '服务端必须声明当前快照 Schema')
  assert.equal(SNAPSHOT_SCHEMA_VERSION, Number(serverSchema[1]))

  useGameStore.getState().resetGameStore()
  assert.equal(useGameStore.getState().publicLoadoutVisible, true)

  const seedSnapshot = {
    ...useGameStore.getState().toSnapshotView(),
    playerId: '50000000-0000-4000-8000-000000000001',
    version: 1,
    publicLoadoutVisible: true,
  }
  useGameStore.getState().applySnapshot(seedSnapshot)
  assert.equal(useGameStore.getState().publicLoadoutVisible, true)

  useGameStore.getState().applyPatch({
    ...seedSnapshot,
    version: 2,
    publicLoadoutVisible: false,
    loadoutInspectPrice: 120,
  })
  assert.equal(useGameStore.getState().publicLoadoutVisible, false)
  assert.equal(useGameStore.getState().loadoutInspectPrice, 120)

  // 通过协议增量解码 decodePlayerSnapshotDeltaDto 将 settings.publicLoadoutVisible 切回 false 且设 loadoutInspectPrice=66 并合并到 store。
  const currentView = useGameStore.getState().toSnapshotView()
  const settingsData = {
    presence: 'online',
    settings: {
      locale: 'zh-CN',
      notificationsEnabled: true,
      loopEnabled: false,
      autoTravelEnabled: false,
      queuePaused: false,
      autoUseSlots: [],
      avatar: 'player',
      unlockedAvatars: [],
      tutorialStep: 0,
      tutorialRewardsStep: 0,
      hasCompletedFirstAction: false,
      publicLoadoutVisible: false,
      loadoutInspectPrice: 66,
    },
  }
  const settingsChecksum = createHash('sha256')
    .update(Buffer.from(JSON.stringify(settingsData), 'utf8'))
    .digest('hex')
  const deltaDto = {
    playerId: '50000000-0000-4000-8000-000000000001',
    stateVersion: 3,
    lastSimulatedAt: '2026-11-14T12:00:05Z',
    changedSections: [
      {
        section: 'settings',
        sectionVersion: 2,
        schemaVersion: SNAPSHOT_SCHEMA_VERSION,
        minReaderSchemaVersion: 41,
        checksum: settingsChecksum,
        updatedAt: '2026-11-14T12:00:05Z',
        data: settingsData,
      },
    ],
  }
  const deltaBytes = new TextEncoder().encode(JSON.stringify(deltaDto))
  const deltaPatch = await decodePlayerSnapshotDeltaDto(deltaDto, currentView, deltaBytes, ['changedSections'])
  assert.equal(deltaPatch.publicLoadoutVisible, false)
  assert.equal(deltaPatch.loadoutInspectPrice, 66)
  useGameStore.getState().applyPatch(deltaPatch)
  assert.equal(useGameStore.getState().publicLoadoutVisible, false)
  assert.equal(useGameStore.getState().loadoutInspectPrice, 66)

  useGameStore.getState().resetGameStore()
  assert.equal(useGameStore.getState().publicLoadoutVisible, true)
  assert.equal(useGameStore.getState().loadoutInspectPrice, 0)
})
