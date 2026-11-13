import { createHash } from 'node:crypto'
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'

const repoRoot = resolve(import.meta.dirname, '..')
const gameDataDir = join(repoRoot, 'config', 'game-data')
const outFile = join(repoRoot, 'frontend', 'src', 'game', 'simulator', 'data', 'simulatorCatalog.json')

function collectJsonFiles(dir) {
  const result = []
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const fullPath = join(dir, entry.name)
    if (entry.isDirectory()) {
      result.push(...collectJsonFiles(fullPath))
    } else if (entry.name.endsWith('.json')) {
      const rel = relative(gameDataDir, fullPath).replace(/\//g, '\\')
      result.push({
        key: `Domain.Catalog.Defaults.${rel}`,
        rel: rel.replace(/\\/g, '/'),
        fullPath,
      })
    }
  }
  result.sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0))
  return result
}

export function buildSimulatorCatalogPayload() {
  const files = collectJsonFiles(gameDataDir)
  const digest = createHash('sha256')
  let contentVersion = 'unknown'
  let expectedConfig = null
  let duel = null
  const abilities = {}
  const combatActions = []
  const buildings = {}
  const encounters = []
  const monsters = {}
  const maps = []
  const rawItems = {}
  const rawEquipments = {}
  const actionYieldBuffs = {}

  for (const file of files) {
    const bytes = readFileSync(file.fullPath)
    digest.update(bytes)
    const parsed = JSON.parse(bytes.toString('utf8'))
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      if (parsed.contentVersion) contentVersion = String(parsed.contentVersion)
      if (parsed.baseAttributeAnchors || parsed.levelExpected || parsed.realmProgression) {
        expectedConfig = {
          baseAttributeAnchors: parsed.baseAttributeAnchors ?? null,
          levelExpected: parsed.levelExpected ?? null,
          realmProgression: parsed.realmProgression ?? null,
        }
        continue
      }
      if (parsed.id === 'duel.settings') {
        duel = parsed
        continue
      }
      if (parsed.id === 'market.reference-price') continue
    }
    const entries = Array.isArray(parsed) ? parsed : [parsed]
    for (const entry of entries) {
      if (!entry || typeof entry !== 'object' || typeof entry.id !== 'string') continue
      const id = entry.id
      if (id.startsWith('item.') || id.startsWith('currency.')) {
        if (!rawItems[id]) rawItems[id] = entry
      } else if (id.startsWith('ability.')) {
        abilities[id] = entry
      } else if (id.startsWith('equipment.')) {
        rawEquipments[id] = entry
      } else if (id.startsWith('enemy.')) {
        monsters[id] = entry
      } else if (id.startsWith('encounter.')) {
        encounters.push(entry)
      } else if (id.startsWith('map.')) {
        maps.push(entry)
      } else if (id.startsWith('building.')) {
        buildings[id] = entry
      } else if (id.startsWith('shop.')) {
        // 模拟器无需系统商店直购目录
      } else if ('durationSeconds' in entry) {
        if (Array.isArray(entry.yields)) {
          for (const y of entry.yields) {
            if (y?.buff?.buffId && !actionYieldBuffs[y.buff.buffId]) {
              actionYieldBuffs[y.buff.buffId] = y.buff.name || y.buff.buffId
            }
          }
        }
        if ((entry.type === 'combat' || entry.kind === 'combat') && entry.executionKind === 'combatEncounter') {
          combatActions.push(entry)
        }
      }
    }
  }

  const fingerprint = digest.digest('hex').slice(0, 16)
  return {
    contentVersion,
    fingerprint,
    duel,
    expectedConfig,
    abilities,
    combatActions,
    buildings,
    encounters,
    monsters,
    maps,
    rawItems,
    rawEquipments,
    actionYieldBuffs,
  }
}

if (import.meta.url === `file://${process.argv[1].replace(/\\/g, '/')}` || process.argv[1]?.endsWith('gen-simulator-catalog.mjs')) {
  const payload = buildSimulatorCatalogPayload()
  const json = JSON.stringify(payload)
  mkdirSync(dirname(outFile), { recursive: true })
  writeFileSync(outFile, json, 'utf8')
  console.log(`[gen-simulator-catalog] 已生成 ${outFile}（fingerprint=${payload.fingerprint}，${json.length} bytes）`)
}
