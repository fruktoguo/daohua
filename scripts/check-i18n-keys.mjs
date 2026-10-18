#!/usr/bin/env node
// 前端 i18n 词条覆盖门禁：校验源码中出现的全部翻译键、以及配表驱动的动态键族是否都有中文词条。
// 任何一条缺失都会让界面把词条键（如 game.item.item.foo.label）或原始实体 ID 裸显给玩家，
// 因此本脚本在缺失时以非零码退出，作为合并前的最小门禁之一（由 scripts/verify.sh 调用）。
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, extname } from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { tmpdir } from 'node:os'

const ROOT = fileURLToPath(new URL('..', import.meta.url)).replace(/[\\/]$/, '')
const FE = join(ROOT, 'frontend')
const DOMAINS = ['common', 'app', 'insight', 'market', 'combat', 'game', 'social']
const failures = []

/** 递归列出目录下指定后缀的文件 */
function walk(dir, exts, out = []) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name)
    const stat = statSync(path)
    if (stat.isDirectory()) {
      if (name === 'node_modules' || name === 'dist') continue
      walk(path, exts, out)
    } else if (exts.includes(extname(path))) {
      out.push(path)
    }
  }
  return out
}

// 1) 用 esbuild 把中文词典打成一个可直接 require 的模块，避免另写一套 TS 解析。
const requireFromFrontend = createRequire(join(FE, 'package.json'))
const esbuild = requireFromFrontend('esbuild')
const dictOut = join(tmpdir(), `daohua-i18n-dict-${process.pid}.cjs`)
esbuild.buildSync({
  entryPoints: [join(FE, 'src/i18n/locales/zh-CN.ts')],
  bundle: true,
  format: 'cjs',
  platform: 'node',
  outfile: dictOut,
  logLevel: 'error',
})
const zhCN = createRequire(import.meta.url)(dictOut).zhCN

const dictKeys = new Set()
;(function flatten(node, prefix) {
  for (const [key, value] of Object.entries(node)) {
    const full = prefix ? `${prefix}.${key}` : key
    if (value && typeof value === 'object' && !Array.isArray(value)) flatten(value, full)
    else if (value !== undefined) dictKeys.add(full)
  }
})(zhCN, '')
/** 词条键前缀（父路径）集合：允许源码里存父路径常量后自行拼接子键 */
const dictPrefixes = new Set()
for (const key of dictKeys) {
  const parts = key.split('.')
  for (let i = 1; i < parts.length; i++) dictPrefixes.add(parts.slice(0, i).join('.'))
}
const has = key => dictKeys.has(key)

// 2) 配表实体与枚举采集（动态键族的真值来源）。
const dataDir = join(ROOT, 'config/game-data')
const dataFiles = walk(dataDir, ['.json']).filter(
  file => !file.endsWith('manifest.json') && !file.endsWith('expected-attributes.json'),
)
const rows = []
for (const file of dataFiles) {
  const parsed = JSON.parse(readFileSync(file, 'utf8'))
  if (Array.isArray(parsed)) rows.push(...parsed)
}

const items = new Set()
const equipments = new Set()
const actions = new Set()
const techniques = new Set()
const styles = new Set()
const targets = new Set()
const slots = new Set()
const buffs = new Set()

for (const row of rows) {
  const id = typeof row.id === 'string' ? row.id : undefined
  if (id) {
    if (id.startsWith('item.')) items.add(id)
    else if (id.startsWith('equipment.')) equipments.add(id)
    // 行动表以 durationSeconds + type 为判别特征（地图节点、敌怪、建筑无此组合）。
    if (typeof row.durationSeconds === 'number' && typeof row.type === 'string') actions.add(id)
  }
  if (typeof row.itemId === 'string' && row.itemId.startsWith('item.')) items.add(row.itemId)
  if (typeof row.technique === 'string') techniques.add(row.technique)
  if (typeof row.style === 'string') styles.add(row.style)
  if (typeof row.targetType === 'string') targets.add(row.targetType)
  if (typeof row.slot === 'string') slots.add(row.slot)
  if (typeof row.buffId === 'string') buffs.add(row.buffId)
  if (row.yields) for (const entry of row.yields) if (typeof entry.buffId === 'string') buffs.add(entry.buffId)
}

// 3) 源码字面量扫描：形如 domain.path 的字符串必须是词条键或其父路径。
//    网络层协议字段路径（snapshot.ts 的错误上下文）、WebSocket 指令名与行动 ID 不是词条，予以排除。
const protocolCommand = /sendCommand\(/
const literalRe = /['"`]([\w-]+(?:\.[\w-]+)+)['"`]/g
const sourceFiles = walk(join(FE, 'src'), ['.ts', '.tsx'])
const badLiterals = new Map()
for (const file of sourceFiles) {
  const rel = file.slice(ROOT.length + 1).replace(/\\/g, '/')
  if (rel.includes('/network/')) continue
  readFileSync(file, 'utf8').split('\n').forEach((line, index) => {
    const trimmed = line.trim()
    if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return
    literalRe.lastIndex = 0
    let match
    while ((match = literalRe.exec(line))) {
      const key = match[1]
      if (!DOMAINS.includes(key.split('.')[0])) continue
      if (actions.has(key)) continue
      if (protocolCommand.test(line)) continue
      if (has(key) || dictPrefixes.has(key)) continue
      const where = badLiterals.get(key) ?? []
      where.push(`${rel}:${index + 1}`)
      badLiterals.set(key, where)
    }
  })
}
for (const [key, where] of badLiterals) {
  failures.push(`源码引用了不存在的词条键 ${key}（${where.slice(0, 3).join('，')}）`)
}

// 4) 配表驱动的动态键族：用真实配表枚举值反查词条，缺失即视为界面裸显风险。
const requireKey = key => {
  if (!has(key)) failures.push(`配表枚举缺少词条 ${key}`)
}
for (const id of [...items, ...equipments]) {
  requireKey(`game.item.${id}.label`)
  requireKey(`game.item.${id}.description`)
}
for (const id of actions) requireKey(`game.action.${id}.name`)
// 服务端 ToAbilityDto 会把 fiveElements 归一为 five-elements，两种写法都验。
for (const style of styles) requireKey(`game.abilityStyle.${style === 'fiveElements' ? 'five-elements' : style}`)
for (const target of targets) {
  const normalized = { allEnemies: 'all-enemies', allAllies: 'all-allies', lowestHitpointsAlly: 'lowest-hp-ally' }[target] ?? target
  requireKey(`game.abilityTarget.${normalized}`)
}
for (const technique of techniques) {
  requireKey(`insight.technique.${technique}`)
  requireKey(`app.crafting.${technique}`)
}
// 装备槽位 tool-<technique> 在 resolveItemCategoryLabel 里归一为 tool，两种取值都要有词条。
for (const slot of slots) requireKey(`game.category.${slot.startsWith('tool-') ? 'tool' : slot}`)
for (const buffId of buffs) {
  requireKey(`combat.trigger.buff.${buffId.startsWith('buff.') ? buffId.slice(5) : buffId}`)
}

// 5) 结论
if (failures.length === 0) {
  console.log(
    `i18n 词条校验通过：${dictKeys.size} 条中文词条，${sourceFiles.length} 个源码文件，` +
    `${items.size} 个物品 + ${equipments.size} 件装备，${actions.size} 个行动。`,
  )
  process.exit(0)
}
console.error(`i18n 词条校验失败：共 ${failures.length} 项缺失，界面会裸显词条键或实体 ID。`)
for (const failure of failures) console.error(`  - ${failure}`)
process.exit(1)
