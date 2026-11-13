import { buildGameDataCatalog } from './engine/catalog.ts'
import {
  createDefaultProfile,
  listCombatNodeIds,
  resolveProfilesResult,
  runSimulation,
} from './engine/simulatorRunner.ts'
import type {
  SimulatorResolveRequestDto,
  SimulatorRunRequestDto,
} from './simulatorTypes.ts'

type GameDataCatalog = ReturnType<typeof buildGameDataCatalog>
type CatalogJsonShape = Parameters<typeof buildGameDataCatalog>[0]

interface WorkerRequestMessage {
  id?: number
  op?: string
  payload?: string
}

let catalogPromise: Promise<GameDataCatalog> | null = null

/** 懒加载并构建推演配表目录，避免进入主包或在页面首屏阶段加载配表 JSON。 */
function getCatalog(): Promise<GameDataCatalog> {
  if (!catalogPromise) {
    catalogPromise = import('./data/simulatorCatalog.json').then((mod) => {
      const raw = (mod as { default?: unknown }).default ?? mod
      return buildGameDataCatalog(raw as CatalogJsonShape)
    })
  }
  return catalogPromise
}

async function handleOperation(op: string, payload?: string): Promise<string> {
  const catalog = await getCatalog()
  switch (op) {
    case 'fingerprint':
      return JSON.stringify({ fingerprint: catalog.fingerprint })
    case 'listCombatNodeIds':
      return JSON.stringify(listCombatNodeIds(catalog))
    case 'createDefaultProfile':
      return JSON.stringify(createDefaultProfile(catalog))
    case 'resolveProfiles': {
      const request = JSON.parse(payload ?? '') as SimulatorResolveRequestDto
      return JSON.stringify(resolveProfilesResult(request, catalog))
    }
    case 'run': {
      const request = JSON.parse(payload ?? '') as SimulatorRunRequestDto
      return JSON.stringify(runSimulation(request, catalog))
    }
    default:
      throw new Error(`未知模拟器指令: ${op}`)
  }
}

self.addEventListener('message', (event: MessageEvent<WorkerRequestMessage>) => {
  const { id, op, payload } = event.data ?? {}
  if (typeof id !== 'number' || typeof op !== 'string') return

  void handleOperation(op, payload).then(
    (result) => {
      self.postMessage({ id, ok: true, result })
    },
    (error: unknown) => {
      self.postMessage({
        id,
        ok: false,
        error: error instanceof Error ? error.message : String(error),
      })
    },
  )
})

self.postMessage({ type: 'ready' })
