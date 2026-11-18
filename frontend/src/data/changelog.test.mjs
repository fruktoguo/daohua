import { after, test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

const tempOut = path.join(tmpdir(), `daohua-changelog-test-${Date.now()}.mjs`)

await build({
  stdin: {
    contents: `
      export * from './frontend/src/data/changelog.ts';
      export { parseLogTemplate } from './frontend/src/logging/gameLogService.ts';
      export { TRAIT_DEFINITIONS } from './frontend/src/game/combatConstants.ts';
    `,
    resolveDir: process.cwd(),
    loader: 'ts',
  },
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
  plugins: [
    {
      name: 'mock-i18n',
      setup(b) {
        b.onResolve({ filter: /(^|\/)i18n(\/|$)/ }, args => ({ path: args.path, namespace: 'mock-i18n' }))
        b.onLoad({ filter: /.*/, namespace: 'mock-i18n' }, () => ({
          contents: 'export const getT = () => (key) => key;',
          loader: 'js',
        }))
      },
    },
  ],
})

const mod = await import(pathToFileURL(tempOut).href)

after(() => {
  fs.rmSync(tempOut, { force: true })
})

function createMemoryStorage(initial = {}) {
  const store = new Map(Object.entries(initial))
  return {
    getItem(key) {
      return store.has(key) ? store.get(key) : null
    },
    setItem(key, value) {
      store.set(key, String(value))
    },
  }
}

function loadJsonDir(dir) {
  const fullDir = path.resolve(process.cwd(), dir)
  const records = []
  for (const file of fs.readdirSync(fullDir)) {
    if (!file.endsWith('.json')) continue
    const parsed = JSON.parse(fs.readFileSync(path.join(fullDir, file), 'utf8'))
    if (Array.isArray(parsed)) records.push(...parsed)
    else if (parsed && typeof parsed === 'object') records.push(parsed)
  }
  return records
}

test('更新日志：通知标识解锁、首次弹窗与解耦持久化逻辑正确', () => {
  const latest = mod.resolveLatestNoticeEntry()
  assert.ok(latest?.noticeId, '最新公告条目必须携带非空 noticeId')

  const storage = createMemoryStorage()
  assert.equal(mod.shouldShowChangelogNotice(latest, mod.readDismissedNoticeIds(storage)), true)

  // 关闭当前公告后写入本地存储，再次检测应不再弹出
  const afterDismiss = mod.dismissChangelogNotice(latest.noticeId, storage)
  assert.equal(afterDismiss.has(latest.noticeId), true)
  assert.equal(mod.shouldShowChangelogNotice(latest, mod.readDismissedNoticeIds(storage)), false)

  // 日常发版新增一条无 noticeId 的日志时，已确认过最新 noticeId 的玩家不会被动再次弹窗
  const entriesWithMaintenance = [
    {
      date: '2026-11-15',
      versionLabel: 'v0.3.7',
      title: '日常维护热修',
      summary: '无重大公告，不弹窗打扰。',
      sections: [{ category: 'fix', items: ['例行稳定优化。'] }],
    },
    ...mod.CHANGELOG_ENTRIES,
  ]
  const activeNoticeAfterMaintenance = mod.resolveLatestNoticeEntry(entriesWithMaintenance)
  assert.equal(activeNoticeAfterMaintenance?.noticeId, latest.noticeId)
  assert.equal(
    mod.shouldShowChangelogNotice(activeNoticeAfterMaintenance, mod.readDismissedNoticeIds(storage)),
    false,
  )

  // 仅当发布带全新 noticeId 的新日志时，才重新向玩家触发一次弹窗
  const entriesWithNextNotice = [
    {
      date: '2026-11-20',
      versionLabel: 'v0.4.0',
      title: '全新大版本',
      summary: '大版本更新。',
      noticeId: 'notice-20261120-v0.4.0',
      sections: [{ category: 'feature', items: ['全新内容上线。'] }],
    },
    ...mod.CHANGELOG_ENTRIES,
  ]
  const nextNotice = mod.resolveLatestNoticeEntry(entriesWithNextNotice)
  assert.equal(nextNotice?.noticeId, 'notice-20261120-v0.4.0')
  assert.equal(mod.shouldShowChangelogNotice(nextNotice, mod.readDismissedNoticeIds(storage)), true)

  // 关闭新 noticeId 时同时保留历史已读记录
  mod.dismissChangelogNotice(nextNotice.noticeId, storage)
  const bothDismissed = mod.readDismissedNoticeIds(storage)
  assert.equal(bothDismissed.has(latest.noticeId), true)
  assert.equal(bothDismissed.has(nextNotice.noticeId), true)
})

test('更新日志：损坏的 localStorage 内容静默回退为空集不崩溃', () => {
  for (const corrupt of ['not-json', '{}', 'null', '[123, "", "   "]']) {
    const storage = createMemoryStorage({ [mod.CHANGELOG_DISMISSED_STORAGE_KEY]: corrupt })
    const result = mod.readDismissedNoticeIds(storage)
    assert.equal(result.size, 0, `损坏存储 ${corrupt} 应安全降级为空集`)
  }
})

const FORBIDDEN_DEV_JARGON = /\b(?:ECS|Schema|Canvas|VFX|WebSocket|WS|Caddy|Docker|DTO|POCO|WASM|TypeScript|React|Vite|PostgreSQL|PG|WAL|MOSS|RMBG|cf-uploader|is_bot|localStorage|IndexedDB|OAuth|HMAC|PBKDF2|ETag|JSON|SVG|DOM|DPI|API|CI|GitHub|Google Drive|rclone|PID|TTL|XSS|Origin)\b/i

test('更新日志：全部历史条目的富文本实体 ID 与真实配表、特质全量对齐且无开发术语泄露', () => {
  assert.ok(mod.CHANGELOG_ENTRIES.length >= 9, '应包含 2026-11-10 起至少 9 期更新日志')

  const items = new Map([
    ...loadJsonDir('config/game-data/items').map(entry => [entry.id, entry]),
    ...loadJsonDir('config/game-data/equipment').map(entry => [entry.id, entry]),
  ])
  const abilities = new Map(loadJsonDir('config/game-data/abilities').map(entry => [entry.id, entry]))
  const buildings = new Map(loadJsonDir('config/game-data/buildings').map(entry => [entry.id, entry]))
  const monsters = new Map(loadJsonDir('config/game-data/monsters').map(entry => [entry.id, entry]))
  const actions = new Map(loadJsonDir('config/game-data/actions').map(entry => [entry.id, entry]))
  const nodes = new Map()
  for (const mapDef of loadJsonDir('config/game-data/maps')) {
    for (const node of mapDef.nodes ?? []) {
      nodes.set(node.id, node)
    }
  }

  const knownBuffIds = new Set()
  for (const trait of Object.values(mod.TRAIT_DEFINITIONS)) {
    if (trait.buff?.buffId) knownBuffIds.add(trait.buff.buffId)
  }
  for (const ability of abilities.values()) {
    const buffCandidates = [
      ability.buff,
      ...(ability.buffs ?? []),
      ability.onHitBuff,
      ...(ability.onHitBuffs ?? []),
      ability.selfBuff,
      ...(ability.selfBuffs ?? []),
    ]
    for (const spec of buffCandidates) {
      if (spec?.buffId) knownBuffIds.add(spec.buffId)
    }
  }
  for (const item of items.values()) {
    for (const buff of item.buffEffects ?? []) {
      if (buff.buffId) knownBuffIds.add(buff.buffId)
    }
  }

  let checkedRefs = 0
  let prevDate = '9999-99-99'
  for (const entry of mod.CHANGELOG_ENTRIES) {
    assert.match(entry.date, /^\d{4}-\d{2}-\d{2}$/)
    assert.ok(entry.date < prevDate, `更新日志必须按日期严格倒序排列: ${entry.date} 不早于 ${prevDate}`)
    prevDate = entry.date
    assert.ok(entry.versionLabel.length > 0)
    assert.ok(entry.sections.length > 0)
    assert.doesNotMatch(entry.title, FORBIDDEN_DEV_JARGON, `标题不应包含面向开发者的技术术语: ${entry.title}`)
    assert.doesNotMatch(entry.summary, FORBIDDEN_DEV_JARGON, `导语不应包含面向开发者的技术术语: ${entry.summary}`)
    for (const section of entry.sections) {
      for (const line of section.items) {
        assert.doesNotMatch(line, /v\d+\.\d+\.\d+/, `正文不应内嵌版本号文本（会被 ValueText 切分）: ${line}`)
        // 剥离 {{kind:id|label}} 的内部英文 ID，仅对玩家可见文本做工程术语门禁检测
        const visibleText = line.replace(/\{\{[a-z]+:[^|}]+\|([^}]+)\}\}/g, '$1')
        assert.doesNotMatch(visibleText, FORBIDDEN_DEV_JARGON, `正文不应包含面向开发者的技术术语: ${visibleText}`)
        const segments = mod.parseLogTemplate(line)
        for (const seg of segments) {
          if (seg.type !== 'reference') continue
          checkedRefs += 1
          switch (seg.kind) {
            case 'item': {
              const def = items.get(seg.id)
              assert.ok(def, `物品或装备 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `物品显示名与配表不一致: ${seg.id}`)
              break
            }
            case 'ability': {
              const def = abilities.get(seg.id)
              assert.ok(def, `功法 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `功法显示名与配表不一致: ${seg.id}`)
              break
            }
            case 'trait': {
              const def = mod.TRAIT_DEFINITIONS[seg.id]
              assert.ok(def, `特质 ID 不存在于 TRAIT_DEFINITIONS: ${seg.id}`)
              assert.equal(def.name, seg.label, `特质显示名与定义不一致: ${seg.id}`)
              break
            }
            case 'building': {
              const def = buildings.get(seg.id)
              assert.ok(def, `建筑 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `建筑显示名与配表不一致: ${seg.id}`)
              break
            }
            case 'enemy': {
              const def = monsters.get(seg.id)
              assert.ok(def, `妖兽 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `妖兽显示名与配表不一致: ${seg.id}`)
              break
            }
            case 'action': {
              const def = actions.get(seg.id)
              assert.ok(def, `行动 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `行动显示名与配表不一致: ${seg.id}`)
              break
            }
            case 'location': {
              const def = nodes.get(seg.id)
              assert.ok(def, `地点节点 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `地点显示名与配表不一致: ${seg.id}`)
              break
            }
            case 'buff': {
              assert.ok(knownBuffIds.has(seg.id), `Buff ID 无法在特质/功法/物品配表中回查: ${seg.id}`)
              break
            }
            default:
              assert.fail(`更新日志出现未校验的引用类型 ${seg.kind}: ${seg.id}`)
          }
        }
      }
    }
  }
  assert.ok(checkedRefs >= 20, `必须实际校验到足够数量的富文本引用，当前仅 ${checkedRefs} 条`)
})
