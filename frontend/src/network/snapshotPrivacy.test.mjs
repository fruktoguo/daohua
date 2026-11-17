import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'
import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { SNAPSHOT_SCHEMA_VERSION } from './snapshotSchemaGate.ts'

// snapshot 与 gameStore 涉及跨域模块路径，用 esbuild 打包后以原生 node:test 校验隐私状态默认与增量同步。
const require = createRequire(import.meta.url)
const esbuild = require('esbuild')
const esbuildDefine = {
  'import.meta.env': JSON.stringify({ DEV: false, VITE_APP_VERSION: 'test' }),
}

const tempOut = path.join(tmpdir(), `daohua-snapshot-privacy-${Date.now()}.mjs`)
await esbuild.build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/gameStore.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  define: esbuildDefine,
  logLevel: 'error',
})
const { useGameStore } = await import(pathToFileURL(tempOut).href)

const snapshotOut = path.join(tmpdir(), `daohua-snapshot-codec-${Date.now()}.mjs`)
await esbuild.build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/network/snapshot.ts')],
  bundle: true,
  format: 'esm',
  outfile: snapshotOut,
  define: esbuildDefine,
  logLevel: 'error',
})
const { decodePlayerSnapshotDeltaDto } = await import(pathToFileURL(snapshotOut).href)

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
  })
  assert.equal(useGameStore.getState().publicLoadoutVisible, false)

  // 通过协议增量解码 decodePlayerSnapshotDeltaDto 将 settings.publicLoadoutVisible 切回 true 并合并到 store。
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
      publicLoadoutVisible: true,
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
        schemaVersion: 43,
        minReaderSchemaVersion: 41,
        checksum: settingsChecksum,
        updatedAt: '2026-11-14T12:00:05Z',
        data: settingsData,
      },
    ],
  }
  const deltaBytes = new TextEncoder().encode(JSON.stringify(deltaDto))
  const deltaPatch = await decodePlayerSnapshotDeltaDto(deltaDto, currentView, deltaBytes, ['changedSections'])
  assert.equal(deltaPatch.publicLoadoutVisible, true)
  useGameStore.getState().applyPatch(deltaPatch)
  assert.equal(useGameStore.getState().publicLoadoutVisible, true)

  useGameStore.getState().resetGameStore()
  assert.equal(useGameStore.getState().publicLoadoutVisible, true)
})
