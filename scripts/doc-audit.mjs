#!/usr/bin/env node
// docs/ 文档体检：索引可达性、链接完整性、真源重复度、任务看板膨胀、排版形态与路径引用失效。
//
// 本脚本是 doc-optimize Skill 的取证工具，**不参与 scripts/verify.sh 合并门禁**——文档质量
// 是渐进优化目标，阈值随文档演进调整，纳入门禁会让一次合理的结构调整阻塞全量提交。
// 阈值来源与处置口径见 .agents/skills/doc-optimize/SKILL.md，改任一侧需同步另一侧。
//
// 用法：
//   node scripts/doc-audit.mjs            # 人读报告
//   node scripts/doc-audit.mjs --json     # 机器读，供 agent 逐条处理
//   node scripts/doc-audit.mjs --top 30   # 放大每节样本条数（默认 12）
//
// 退出码：存在硬缺陷（坏链、看板编号重复、看板详情死链）返回 1，仅软指标超限返回 0。
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs'
import { join, relative, dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = fileURLToPath(new URL('..', import.meta.url)).replace(/[\\/]$/, '')
const DOCS = join(ROOT, 'docs')

/** 软指标阈值：与 SKILL.md 阈值表一一对应，改这里必须同步改文档。 */
const THRESHOLDS = {
  shingleSize: 8, // 内容重复判定的字符切片长度
  dupWarn: 0.15, // 8-gram 重合占较短篇比例，超过需判定真源归属
  dupMerge: 0.25, // 超过必须合并
  maxLine: 400, // 正文单行字符上限（代码块内不计）
  maxTableLine: 320, // 表格行字符上限
  maxBoldPerKb: 10, // 每 KB 加粗（**）对数上限
  boardLine: 400, // tasks.md 看板表格行字符上限
  taskBytes: 12288, // 单篇 TASK 详情字节上限
  taskTimestamps: 10, // 单篇 TASK 详情时间戳数量上限（过程日志未压缩信号）
  sourceBytes: 61440, // 真源单篇字节上限，超过建议拆分
}

/** 真源目录：参与内容重复度比较、受体量上限约束的正式规范。 */
const SOURCE_DIRS = ['docs/design', 'docs/backend', 'docs/frontend', 'docs/ops', 'docs/world']
/**
 * 时点证据库：historical 归档与 audits 审计报告记录的是当时源码状态，其中的路径、行号与
 * 形态允许失效。这类文档只参与「索引可达性」判据，不参与重复度、路径引用与形态判据；
 * 若要按它们订正现行实现，必须重新取证，不得直接以报告为准。
 */
const EVIDENCE_PREFIXES = ['docs/archive/', 'docs/audits/']
const isEvidence = (p) => EVIDENCE_PREFIXES.some((prefix) => p.startsWith(prefix))
/** 反引号内视为「仓库路径引用」的前缀；命中前缀且文件不存在即报失效引用。 */
const PATH_PREFIXES = ['src/', 'tests/', 'scripts/', 'frontend/', 'deploy/', 'data/', 'config/', 'docs/', 'vendor/', 'tools/']
/** 链接来源：docs/ 全域 md，另加仓库根入口文档，保证「从根可达」口径成立。 */
const EXTRA_SOURCES = ['README.md', 'AGENTS.md']

const strip = (p) => p.replace(/\\/g, '/')
const repoRel = (abs) => strip(relative(ROOT, abs))
const posixJoin = (base, rel) => strip(join(base, rel))

/** 递归收集指定后缀文件，跳过构建产物与版本库。 */
function walk(dir, exts, out = []) {
  let entries
  try {
    entries = readdirSync(dir)
  } catch {
    return out
  }
  for (const name of entries) {
    if (name === 'node_modules' || name === '.git' || name === 'dist' || name === 'bin' || name === 'obj') continue
    const path = join(dir, name)
    const stat = statSync(path)
    if (stat.isDirectory()) walk(path, exts, out)
    else if (exts.some((ext) => path.endsWith(ext))) out.push(path)
  }
  return out
}

/** 按行遮掉 ``` 围栏内的内容：代码块里的长行、反引号和路径都不参与正文判据。 */
function maskFences(text) {
  const lines = text.split('\n')
  let fenced = false
  return lines.map((line) => {
    if (/^\s*```/.test(line)) {
      fenced = !fenced
      return ''
    }
    return fenced ? '' : line
  })
}

/** 抽取 markdown 行内链接目标（排除图片、锚点与外部 URL）。 */
function extractLinks(text) {
  const links = []
  for (const m of text.matchAll(/\[[^\]]*\]\(([^)\s]+)\)/g)) {
    const raw = m[1]
    if (/^(https?:|mailto:|#)/.test(raw)) continue
    links.push(raw.split('#')[0].trim())
  }
  return links.filter(Boolean)
}

/** 内容指纹：去掉全部空白与标记噪音后取 8-gram 集合。 */
function shingles(text, n) {
  const flat = text.replace(/[\s*`>|#\-\u3000~]/g, '')
  const set = new Set()
  for (let i = 0; i + n <= flat.length; i += 1) set.add(flat.slice(i, i + n))
  return set
}

// ---------- 采集 ----------

const docFiles = walk(DOCS, ['.md'])
const docsByRepoPath = new Map(docFiles.map((abs) => [repoRel(abs), readFileSync(abs, 'utf8')]))
const dirSet = new Set()
for (const abs of docFiles) {
  let dir = dirname(abs)
  while (strip(dir).startsWith(strip(ROOT)) && dir !== ROOT) {
    dirSet.add(repoRel(dir))
    dir = dirname(dir)
  }
}

const brokenLinks = []
const inbound = new Map()
for (const repoPath of [...docsByRepoPath.keys()]) inbound.set(repoPath, 0)

const sources = [...docsByRepoPath.keys()]
for (const extra of EXTRA_SOURCES) {
  if (existsSync(join(ROOT, extra))) sources.push(extra)
}

for (const source of sources) {
  const abs = join(ROOT, source)
  const text = readFileSync(abs, 'utf8')
  for (const target of extractLinks(text)) {
    const resolved = posixJoin(dirname(abs), target)
    const relFromRoot = repoRel(resolve(ROOT, resolved))
    const isDir = relFromRoot === '' || existsSync(join(ROOT, relFromRoot)) && statSync(join(ROOT, relFromRoot)).isDirectory()
    if (docsByRepoPath.has(relFromRoot)) {
      inbound.set(relFromRoot, (inbound.get(relFromRoot) ?? 0) + 1)
      continue
    }
    if (isDir && existsSync(join(ROOT, relFromRoot))) {
      // 目录链接：把该目录下全部文档计为可达，避免「按目录挂载」被误报为孤儿。
      for (const repoPath of docsByRepoPath.keys()) {
        if (repoPath.startsWith(`${relFromRoot}/`)) inbound.set(repoPath, (inbound.get(repoPath) ?? 0) + 1)
      }
      continue
    }
    if (!existsSync(join(ROOT, relFromRoot))) brokenLinks.push({ from: source, target, resolved: relFromRoot })
  }
}

const orphanDocs = [...docsByRepoPath.keys()].filter((p) => (inbound.get(p) ?? 0) === 0 && p !== 'docs/README.md')

// 目录索引：本仓以 docs/README.md 为唯一入口，不要求每目录 README（再造一层目录索引就是重复索引）。
// 仅当某目录文档数远超单页可导航上限时才需要目录入口，判定交给人工报告，不做自动判据。



// 真源内容重复度（归档文档是时点快照，不参与）。
const sourceFiles = [...docsByRepoPath.keys()].filter(
  (p) => !isEvidence(p) && (SOURCE_DIRS.some((d) => p.startsWith(`${d}/`)) || p === 'docs/DESIGN.md'),
)
const shingleMap = new Map(sourceFiles.map((p) => [p, shingles(docsByRepoPath.get(p), THRESHOLDS.shingleSize)]))
const dupPairs = []
for (let i = 0; i < sourceFiles.length; i += 1) {
  for (let j = i + 1; j < sourceFiles.length; j += 1) {
    const a = shingleMap.get(sourceFiles[i])
    const b = shingleMap.get(sourceFiles[j])
    if (!a.size || !b.size) continue
    let inter = 0
    for (const g of a) if (b.has(g)) inter += 1
    const ratio = inter / Math.min(a.size, b.size)
    if (ratio >= THRESHOLDS.dupWarn) dupPairs.push({ a: sourceFiles[i], b: sourceFiles[j], ratio })
  }
}
dupPairs.sort((x, y) => y.ratio - x.ratio)

// 单篇形态：长行、加粗密度、体量（归档文档不追优化形态）。
const fileMetrics = []
for (const [repoPath, text] of docsByRepoPath) {
  if (isEvidence(repoPath)) continue
  const masked = maskFences(text)
  const bodyLines = masked.filter((l) => l.trim())
  const overLong = bodyLines.filter((l) => [...l].length > THRESHOLDS.maxLine || (l.startsWith('|') && [...l].length > THRESHOLDS.maxTableLine))
  const boldCount = (text.match(/\*\*/g)?.length ?? 0) / 2
  const kb = Math.max(text.length / 1024, 0.001)
  fileMetrics.push({
    path: repoPath,
    bytes: Buffer.byteLength(text, 'utf8'),
    longLines: overLong.length,
    maxLine: bodyLines.reduce((max, l) => Math.max(max, [...l].length), 0),
    boldPerKb: boldCount / kb,
  })
}

// 反引号内的仓库路径引用失效（时点证据库文档记录当时源码，允许失效）。
// 只判定可机械验证的两类写法：带扩展名的文件路径、以 / 结尾的目录路径。
// 无扩展名且不带尾斜杠的（如 `src/Domain/Player/PlayerAggregate`，实际是类型/命名空间）
// 一律跳过——它既非文件也非目录，无法用 `existsSync` 证伪，误报会淹没真实失效项。
const stalePathRefs = []
for (const [repoPath, text] of docsByRepoPath) {
  if (isEvidence(repoPath)) continue
  for (const line of maskFences(text)) {
    for (const m of line.matchAll(/`([^`\n]+)`/g)) {
      const ref = m[1].trim()
      if (!PATH_PREFIXES.some((p) => ref.startsWith(p))) continue
      if (/[\s→<>{}$*]/.test(ref)) continue // 表达式、glob 通配与示意语法，不是确定路径
      const isDirRef = ref.endsWith('/')
      const hasExt = /\.[a-z0-9]{1,5}$/i.test(ref)
      if (!isDirRef && !hasExt) continue
      const target = ref.replace(/[#?].*$/, '').replace(/\/$/, '')
      if (existsSync(join(ROOT, target))) continue
      stalePathRefs.push({ from: repoPath, ref })
    }
  }
}

// 任务看板：行宽、编号唯一性、详情链接与孤儿详情。
const boardPath = join(DOCS, 'tasks.md')
const boardIssues = { rows: 0, longRows: [], duplicateIds: [], deadDetailLinks: [], orphanDetails: [] }
if (existsSync(boardPath)) {
  const boardText = readFileSync(boardPath, 'utf8')
  const rows = boardText.split('\n').filter((l) => l.startsWith('| TASK-'))
  boardIssues.rows = rows.length
  const ids = new Map()
  const linkedDetails = new Set()
  for (const row of rows) {
    const len = [...row].length
    if (len > THRESHOLDS.boardLine) boardIssues.longRows.push({ len, id: row.slice(0, 24) })
    const id = row.match(/^\| (TASK-\d{8}-\d{2})/)?.[1]
    if (id) ids.set(id, (ids.get(id) ?? 0) + 1)
    for (const m of row.matchAll(/\]\((tasks\/[^)]+)\)/g)) {
      linkedDetails.add(m[1].replace(/^tasks\//, ''))
      if (!existsSync(join(DOCS, m[1]))) boardIssues.deadDetailLinks.push(m[1])
    }
  }
  boardIssues.duplicateIds = [...ids.entries()].filter(([, n]) => n > 1).map(([id, n]) => ({ id, count: n }))
  const detailFiles = existsSync(join(DOCS, 'tasks')) ? readdirSync(join(DOCS, 'tasks')).filter((f) => f.endsWith('.md')) : []
  boardIssues.orphanDetails = detailFiles.filter((f) => !linkedDetails.has(f))
}

// 任务详情体量：过程日志未压缩信号（以字节与时间戳数量衡量）。
const taskMetrics = []
for (const [repoPath, text] of docsByRepoPath) {
  if (!repoPath.startsWith('docs/tasks/') || repoPath.startsWith('docs/tasks/archive/')) continue
  taskMetrics.push({
    path: repoPath,
    bytes: Buffer.byteLength(text, 'utf8'),
    timestamps: (text.match(/\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}/g) ?? []).length,
  })
}

// ---------- 汇总 ----------

const report = {
  totals: {
    docs: docsByRepoPath.size,
    bytes: [...docsByRepoPath.values()].reduce((sum, t) => sum + Buffer.byteLength(t, 'utf8'), 0),
    sourceFiles: sourceFiles.length,
    taskDocs: taskMetrics.length,
    boardRows: boardIssues.rows,
  },
  brokenLinks,
  orphanDocs,

  dupPairs,
  fileMetrics: fileMetrics.filter((m) => m.longLines > 0 || m.boldPerKb > THRESHOLDS.maxBoldPerKb || m.bytes > THRESHOLDS.sourceBytes),
  stalePathRefs,
  boardIssues,
  taskOverweight: taskMetrics.filter((m) => m.bytes > THRESHOLDS.taskBytes || m.timestamps > THRESHOLDS.taskTimestamps),
}

const hardDefects = brokenLinks.length + boardIssues.duplicateIds.length + boardIssues.deadDetailLinks.length

if (process.argv.includes('--json')) {
  console.log(JSON.stringify({ thresholds: THRESHOLDS, hardDefects, ...report }, null, 2))
  process.exit(hardDefects > 0 ? 1 : 0)
}

const topArg = process.argv.indexOf('--top')
const TOP = topArg >= 0 ? Number(process.argv[topArg + 1]) || 12 : 12
const section = (title) => console.log(`\n== ${title}`)
const q = (n) => String(n).padStart(3)

console.log(`docs 体检：${report.totals.docs} 篇 / ${(report.totals.bytes / 1024).toFixed(0)} KB · 真源 ${report.totals.sourceFiles} 篇 · TASK 详情 ${report.totals.taskDocs} 篇 · 看板 ${report.totals.boardRows} 行`)

section(`硬缺陷 ${hardDefects}`)
console.log(`  坏链 ${q(brokenLinks.length)}`)
for (const b of brokenLinks.slice(0, TOP)) console.log(`     ${b.from} -> ${b.target}  (解析为 ${b.resolved}，不存在)`)
console.log(`  看板重复编号 ${q(boardIssues.duplicateIds.length)}`)
for (const d of boardIssues.duplicateIds.slice(0, TOP)) console.log(`     ${d.id} × ${d.count}`)
console.log(`  看板详情死链 ${q(boardIssues.deadDetailLinks.length)}`)
for (const d of boardIssues.deadDetailLinks.slice(0, TOP)) console.log(`     ${d}`)

section(`索引缺口`)
console.log(`  零入链文档 ${q(orphanDocs.length)}（除 docs/README.md 自身；索引唯一入口是 docs/README.md）`)
for (const p of orphanDocs.slice(0, TOP)) console.log(`     ${p}`)
console.log(`  看板未登记详情 ${q(boardIssues.orphanDetails.length)}`)
for (const p of boardIssues.orphanDetails.slice(0, TOP)) console.log(`     docs/tasks/${p}`)

section(`真源重复（8-gram 占较短篇比例，告警 ≥15%，合并 ≥25%）`)
console.log(`  超阈值 ${q(dupPairs.length)} 对`)
for (const d of dupPairs.slice(0, TOP)) {
  const flag = d.ratio >= THRESHOLDS.dupMerge ? '合并' : '判归属'
  console.log(`     ${(d.ratio * 100).toFixed(1).padStart(5)}%  ${flag}  ${d.a} <-> ${d.b}`)
}

section(`路径引用失效（反引号内仓库路径）`)
console.log(`  失效 ${q(stalePathRefs.length)}`)
for (const s of stalePathRefs.slice(0, TOP)) console.log(`     ${s.ref}   <- ${s.from}`)

section(`形态与体量超限`)
for (const m of report.fileMetrics.slice(0, TOP)) {
  console.log(`     ${String(m.bytes).padStart(7)}B  长行 ${String(m.longLines).padStart(2)}  最长 ${String(m.maxLine).padStart(4)}  加粗/KB ${m.boldPerKb.toFixed(1).padStart(5)}  ${m.path}`)
}
console.log(`  看板超宽行 ${q(boardIssues.longRows.length)}（>${THRESHOLDS.boardLine} 字符，最长 ${boardIssues.longRows.reduce((m, r) => Math.max(m, r.len), 0)}）`)
for (const r of boardIssues.longRows.slice(0, TOP)) console.log(`     ${r.len} 字符  ${r.id}…`)
console.log(`  TASK 详情超重 ${q(report.taskOverweight.length)}（>${THRESHOLDS.taskBytes}B 或时间戳 >${THRESHOLDS.taskTimestamps}）`)
for (const t of report.taskOverweight.slice(0, TOP)) console.log(`     ${String(t.bytes).padStart(7)}B  时间戳 ${String(t.timestamps).padStart(2)}  ${t.path}`)

console.log(`\n处置口径见 .agents/skills/doc-optimize/SKILL.md（删除/合并/数值订正一律报审）。退出码 ${hardDefects > 0 ? 1 : 0}。`)
process.exit(hardDefects > 0 ? 1 : 0)
