#!/usr/bin/env node
// 前端 i18n 词条覆盖门禁：校验源码中出现的全部翻译键、以及配表驱动的动态键族是否都有中文词条。
// 任何一条缺失都会让界面把词条键（如 game.item.item.foo.label）或原始实体 ID 裸显给玩家，
// 因此本脚本在缺失时以非零码退出，作为合并前的最小门禁之一（由 scripts/verify.sh 调用）。
//
// 覆盖面（七类）：
// 1. 源码字面量键 ↔ 词典；2. 配表驱动动态键族；3. 前端静态元数据；4. 引用完整性；
// 5. 模板字面量键（t(`prefix.${x}`) 前缀必须是词典父路径）；6. 占位符契约（无参调用不得命中带 {x} 占位符的词条）；
// 7. CJK 硬编码（UI 层绕过 i18n 的中文字面量）与 template.csv ↔ 词典双向同步。
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, extname } from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { tmpdir } from 'node:os'

const ROOT = fileURLToPath(new URL('..', import.meta.url)).replace(/[\\/]$/, '')
const FE = join(ROOT, 'frontend')
const DOMAINS = ['common', 'app', 'insight', 'market', 'combat', 'game', 'social', 'shop']
const failures = []

/** 递归列出目录下指定后缀的文件 */
function walk(dir, exts, out = []) {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name === 'dist' || name === 'private-impl') continue
    const path = join(dir, name)
    try {
      const stat = statSync(path)
      if (stat.isDirectory()) {
        walk(path, exts, out)
      } else if (exts.includes(extname(path))) {
        out.push(path)
      }
    } catch {
      // 跨平台挂载点/无权限路径安全跳过
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
/** 读取中文词条值（扁平路径）；缺失返回 undefined，供「词条值 == 原始 ID」反向校验使用。 */
const lookupLabel = key => {
  let node = zhCN
  for (const part of key.split('.')) {
    if (node === null || typeof node !== 'object') return undefined
    node = node[part]
  }
  return typeof node === 'string' ? node : undefined
}

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
/** 配表中真实定义的物品/装备实体 ID（rows[].id 前缀即实体类型） */
const definedAssets = new Set()
/** 配表任意位置引用到的物品/装备实体 ID（制造输入/产出、掉落表、商店报价等） */
const referencedAssets = new Set()

/** 采集物品/装备型 ID 引用：这些位置一旦指向未定义实体，界面会裸显原始 ID */
const collectAssetRef = value => {
  if (typeof value !== 'string' || value.length === 0) return
  if (!value.startsWith('item.') && !value.startsWith('equipment.')) return
  referencedAssets.add(value)
  if (value.startsWith('item.')) items.add(value)
  else equipments.add(value)
}

for (const row of rows) {
  const id = typeof row.id === 'string' ? row.id : undefined
  if (id) {
    if (id.startsWith('item.')) {
      items.add(id)
      definedAssets.add(id)
    } else if (id.startsWith('equipment.')) {
      equipments.add(id)
      definedAssets.add(id)
    }
    // 行动表以 durationSeconds + type 为判别特征（地图节点、敌怪、建筑无此组合）。
    if (typeof row.durationSeconds === 'number' && typeof row.type === 'string') actions.add(id)
  }
  if (typeof row.itemId === 'string' && row.itemId.startsWith('item.')) items.add(row.itemId)
  if (typeof row.technique === 'string') techniques.add(row.technique)
  if (typeof row.style === 'string') styles.add(row.style)
  if (typeof row.targetType === 'string') targets.add(row.targetType)
  if (typeof row.slot === 'string') slots.add(row.slot)
  // buffId 可能嵌套在 buff/buffs/defaultTriggers 等任意层级（功法自带状态、触发条件），统一深收集防裸显。
  const collectBuffIds = node => {
    if (Array.isArray(node)) {
      for (const entry of node) collectBuffIds(entry)
    } else if (node && typeof node === 'object') {
      for (const [key, value] of Object.entries(node)) {
        if (key === 'buffId' && typeof value === 'string') buffs.add(value)
        else collectBuffIds(value)
      }
    }
  }
  collectBuffIds(row)
  // 引用面：掉落表、制造输入/产出、商店与奖励清单（嵌套字段不在顶层 row.itemId 内）。
  collectAssetRef(row.itemId)
  collectAssetRef(row.targetItemId)
  for (const drop of row.dropTable ?? []) collectAssetRef(drop.itemId)
  for (const key of Object.keys(row.inputs ?? {})) collectAssetRef(key)
  for (const key of Object.keys(row.consumes ?? {})) collectAssetRef(key)
  for (const entry of row.yields ?? []) collectAssetRef(entry.id)
  for (const entry of row.outputs ?? []) collectAssetRef(entry.itemId ?? entry.id)
  for (const entry of row.rewards ?? []) collectAssetRef(entry.itemId ?? entry.id)
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
// 引用完整性：任何被制造配方、掉落表、商店或奖励引用的实体必须在配表中真实定义，
// 否则服务端无法下发名称，前端只能裸显 ID。
for (const id of referencedAssets) {
  if (!definedAssets.has(id)) {
    failures.push(`配表引用了未定义的物品/装备 ${id}（界面会裸显原始 ID）`)
  }
}
// 前端静态元数据覆盖：图标/品质/品类由 ITEM_META_CORES 驱动，配表新增实体必须同步登记。
const idsSource = readFileSync(join(FE, 'src/game/ids.ts'), 'utf8')
const metaCoreIds = new Set(
  [...idsSource.matchAll(/^\s*'((?:item|equipment)\.[\w.-]+)':\s*\{\s*glyph:/gm)].map(match => match[1]),
)
for (const id of definedAssets) {
  if (!metaCoreIds.has(id)) {
    failures.push(`前端静态元数据缺少 ${id}（物品图标与品质会退化）`)
  }
}
for (const id of [...items, ...equipments]) {
  if (!definedAssets.has(id)) continue // 未定义实体已由引用完整性检查报告
  requireKey(`game.item.${id}.label`)
  requireKey(`game.item.${id}.description`)
  // 词条值本身不得是实体 ID：否则界面「有词条」却仍然裸显标识。
  const label = lookupLabel(`game.item.${id}.label`)
  if (label === id) failures.push(`词条 ${`game.item.${id}.label`} 的值是原始 ID，界面会裸显标识`)
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

// 5) 模板字面量键前缀校验：t(`prefix.${expr}`) 的静态前缀必须是词典中真实存在的键或键前缀，
//    否则该键族整族脱离门禁视野，拼错前缀会让整组词条回显键名。
//    兼容两类真实现：跨段父路径（`game.item.${id}.label`）与同段拼接族（`insight.traits.qualityQ${q}` → qualityQ1..5）。
const templateCallRe = /(?<![A-Za-z0-9_$.])t\(\s*`([^`{]*)\$\{/g
const hasKeyWithStem = stem => {
  for (const key of dictKeys) {
    if (key.startsWith(stem)) return true
  }
  return false
}
for (const file of sourceFiles) {
  const rel = file.slice(ROOT.length + 1).replace(/\\/g, '/')
  if (rel.includes('/network/')) continue
  const content = readFileSync(file, 'utf8')
  content.split('\n').forEach((line, index) => {
    const trimmed = line.trim()
    if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return
    templateCallRe.lastIndex = 0
    let match
    while ((match = templateCallRe.exec(line))) {
      const stem = match[1].replace(/\.$/, '') // 去掉 ${ 前的尾点后按父路径校验
      if (stem.length === 0) continue // 前缀完全动态（${} 开头），无法静态校验
      if (!DOMAINS.includes(stem.split('.')[0])) continue // 非词条前缀（如 aria-id、css 类拼接）
      if (dictPrefixes.has(stem) || has(stem) || hasKeyWithStem(stem)) continue
      failures.push(`模板键前缀不存在于词典 ${match[1]}*（${rel}:${index + 1}）`)
    }
  })
}

// 6) 占位符契约：t('key') 无参调用命中的词条若含 {name} 占位符，会把占位符原样裸显给玩家。
const noArgCallRe = /(?<![A-Za-z0-9_$.])t\(\s*(['"])([\w-]+(?:\.[\w-]+)*)\1\s*\)/g
const placeholderRe = /\{[a-zA-Z]\w*\}/
for (const file of sourceFiles) {
  const rel = file.slice(ROOT.length + 1).replace(/\\/g, '/')
  if (rel.includes('/network/')) continue
  readFileSync(file, 'utf8').split('\n').forEach((line, index) => {
    const trimmed = line.trim()
    if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return
    noArgCallRe.lastIndex = 0
    let match
    while ((match = noArgCallRe.exec(line))) {
      const key = match[2]
      if (!DOMAINS.includes(key.split('.')[0])) continue
      const value = lookupLabel(key)
      if (value === undefined || !placeholderRe.test(value)) continue
      failures.push(`词条 ${key} 含占位符但被无参调用（${rel}:${index + 1}，会裸显 {…}）`)
    }
  })
}

// 7) CJK 硬编码检测：UI 层（App/main/components）绕过 i18n 的中文字符串与 JSX 文本。
//    豁免面（均有明确设计理由，新增豁免须在此登记）：
//    - components/simulator/**：开发沙盘工具，非正式玩家面（tools 口径）；
//    - MarketBrowseGrid.tsx：labelKey + fallback 双保险模式（fallback 仅在缺键时兜底，键存在即不显示）；
//    - VectorCharacter.tsx：按服务端下发中文名匹配立绘形态，属 zh 输入耦合逻辑而非展示文案；
//    - LoginPage.tsx：随机占位道号池，属游戏内容数据；
//    - ErrorBoundary.tsx / main.tsx：崩溃诊断与启动失败信息，面向开发者控制台。
//    行级豁免：单字 CJK（图标化字形 token，如 glyph: '修'）；includes('中文') 对译文的运行时反查。
const cjkTwoPlus = /[\u4e00-\u9fff][\u4e00-\u9fff]/
const zhCoupledMatchRe = /\.includes\(\s*(['"`])[^'"`]*[\u4e00-\u9fff]/
const CJK_FILE_EXEMPT = fragment =>
  fragment.includes('/simulator/') ||
  fragment.endsWith('/market/MarketBrowseGrid.tsx') ||
  fragment.endsWith('/VectorCharacter.tsx') ||
  fragment.endsWith('/LoginPage.tsx') ||
  fragment.endsWith('/ErrorBoundary.tsx') ||
  fragment.endsWith('/main.tsx')
for (const file of sourceFiles) {
  const rel = file.slice(ROOT.length + 1).replace(/\\/g, '/')
  if (!rel.startsWith('frontend/src/components/') && !rel.endsWith('frontend/src/App.tsx')) continue
  if (CJK_FILE_EXEMPT(rel)) continue
  readFileSync(file, 'utf8').split('\n').forEach((line, index) => {
    const trimmed = line.trim()
    if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return
    const code = trimmed.replace(/(?<!:)\/\/.*$/, '') // 去行尾注释（URL 的 // 前有冒号不剥离）
    if (!/[\u4e00-\u9fff]/.test(code)) return
    if (zhCoupledMatchRe.test(code)) return
    const inStr = code.match(/(['"`])((?:(?!\1).)*[\u4e00-\u9fff](?:(?!\1).)*)\1/)
    const inJsx = code.match(/>([^<>{}]*[\u4e00-\u9fff][^<>{}]*)</)
    const hit = inStr?.[2] ?? inJsx?.[1]
    if (hit === undefined || !cjkTwoPlus.test(hit)) return
    failures.push(`UI 层疑似绕过 i18n 的中文硬编码「${hit.trim().slice(0, 24)}」（${rel}:${index + 1}）`)
  })
}

// 8) template.csv ↔ 词典双向同步：模板是翻译工单的提交载体，与词典漂移会让外语包
//    拿到过期键集；zh_source 列必须与词典值逐字一致（构建脚本 export-i18n-csv.mjs 负责生成）。
const csvPath = join(FE, 'public/locales/template.csv')
{
  const raw = readFileSync(csvPath, 'utf8')
  if (raw.charCodeAt(0) === 0xfeff) failures.push('template.csv 带 BOM，翻译管线会静默吞掉首行')
  // RFC4180 状态机：引号字段可含逗号/换行/双写引号
  const rows = []
  let field = ''
  let record = []
  let inQuotes = false
  for (let i = 0; i < raw.length; i++) {
    const ch = raw[i]
    if (inQuotes) {
      if (ch === '"') {
        if (raw[i + 1] === '"') { field += '"'; i++ } else inQuotes = false
      } else field += ch
    } else if (ch === '"') {
      inQuotes = true
    } else if (ch === ',') {
      record.push(field); field = ''
    } else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && raw[i + 1] === '\n') i++
      if (record.length > 0 || field.length > 0) { record.push(field); rows.push(record) }
      record = []; field = ''
    } else field += ch
  }
  if (record.length > 0 || field.length > 0) { record.push(field); rows.push(record) }
  const csvKeys = new Map()
  for (let i = 1; i < rows.length; i++) { // 首行为表头
    const [key, source] = rows[i]
    if (key === undefined || key.length === 0) continue
    if (csvKeys.has(key)) failures.push(`template.csv 重复键 ${key}`)
    csvKeys.set(key, source ?? '')
  }
  for (const key of csvKeys.keys()) {
    if (!dictKeys.has(key)) failures.push(`template.csv 含词典不存在的键 ${key}`)
  }
  for (const key of dictKeys) {
    if (!csvKeys.has(key)) failures.push(`词典键 ${key} 未同步进 template.csv`)
  }
  for (const [key, source] of csvKeys) {
    const dictValue = lookupLabel(key)
    if (dictValue !== undefined && dictValue !== source) {
      failures.push(`template.csv 的 ${key} 中文源与词典漂移（需重跑 export-i18n-csv.mjs）`)
    }
  }
}

// 9) 结论
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
