#!/usr/bin/env node
// ==============================================================================
// 脚本名称: gen-asset-manifest.mjs
// 主要职责: 生成前端「可变静态资源」清单 asset-manifest.json
//
// 背景:
//   Android 壳已改为 Web 优先加载：index.html 与 /assets/*-HASH.* 一律走网络，
//   仅 avatars/emotes/locales/theme.js 等「同名可变」资源走本地优先 + 云端热更。
//   本清单是原生 AssetHotpatchManager 判定「本地副本是否为服务端当前版本」的唯一依据：
//   key 为请求路径（如 "/avatars/player.webp"），value 为 sha256 + size。
//
// 排除项（不进入清单）:
//   index.html / privacy.html / app-version.json / asset-manifest.json /
//   assets/**（Vite 内容 hash 自版本化）/ .vite/** / 点开头路径 / *.map
//
// Vite 门禁及 HD 别名:
//   默认读取 <dir>/.vite/manifest.json，校验道具源图标后附带
//   hdAliases：把 /assets/<name>-<hash>.(webp|png) 图标产物映射到
//   /hd/items|buildings/<itemId>.webp，供 Android 原生层把 hash 图标请求透明改写为 HD 资源。
//
// 用法:
//   node scripts/gen-asset-manifest.mjs <dir> [--out <file>] [--prefix </hd>]
//     [--vite-root <dir>] [--vite-manifest <file>] [--no-vite-manifest]
//     <dir>           被扫描目录（frontend/dist、android-shell assets/www、hd-pack 等）
//     --out           输出文件（默认 <dir>/asset-manifest.json）
//     --prefix        key 前缀（默认 ""；hd-pack 传 "/hd" 使 key 形如 "/hd/items/x.webp"）
//     --vite-root     Vite 项目根（默认脚本所在仓库的 frontend/；manifest 内源路径相对此目录）
//     --vite-manifest Vite manifest 路径（默认 <dir>/.vite/manifest.json；APK 传 Web 构建产物）
//     --no-vite-manifest  独立 HD 画质包无 Vite 产物，跳过 Vite 门禁及别名
// ==============================================================================

import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import { fileURLToPath } from 'node:url'

const DEFAULT_VITE_ROOT = fileURLToPath(new URL('../frontend/', import.meta.url))

const EXCLUDED_FILES = new Set([
  'index.html',
  'privacy.html',
  'app-version.json',
  'asset-manifest.json',
])
const EXCLUDED_DIRS = new Set(['assets', '.vite'])
const EXCLUDED_EXT = new Set(['.map'])

function parseArgs(argv) {
  const args = { dir: null, out: null, prefix: '', viteRoot: null, viteManifest: null, noViteManifest: false }
  const options = new Map([
    ['--out', 'out'], ['--prefix', 'prefix'], ['--vite-root', 'viteRoot'], ['--vite-manifest', 'viteManifest'],
  ])
  function usageError(message) {
    console.error(`[gen-asset-manifest] ${message}`)
    console.error('用法: node scripts/gen-asset-manifest.mjs <dir> [--out <file>] [--prefix </hd>] [--vite-root <dir>] [--vite-manifest <file>] [--no-vite-manifest]')
    process.exit(2)
  }
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]
    if (options.has(a)) {
      const value = argv[++i]
      if (!value || value.startsWith('--')) usageError(`${a} 缺少参数值`)
      args[options.get(a)] = value
    } else if (a === '--no-vite-manifest') args.noViteManifest = true
    else if (!a.startsWith('-') && !args.dir) args.dir = a
    else usageError(`未知参数: ${a}`)
  }
  if (!args.dir) usageError('缺少被扫描目录')
  if (args.noViteManifest && (args.viteRoot !== null || args.viteManifest !== null)) {
    usageError('--no-vite-manifest 不能与 --vite-root 或 --vite-manifest 同时使用')
  }
  return args
}

function isExcluded(relPath) {
  const rel = relPath.split(path.sep).join('/')
  const base = path.posix.basename(rel)
  if (EXCLUDED_FILES.has(base)) return true
  if (rel.startsWith('.')) return true
  const top = rel.split('/')[0]
  if (EXCLUDED_DIRS.has(top)) return true
  const ext = path.posix.extname(base).toLowerCase()
  if (EXCLUDED_EXT.has(ext)) return true
  return false
}

function walk(dir, base = dir, acc = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name)
    if (entry.isDirectory()) {
      if (entry.name.startsWith('.')) continue
      walk(full, base, acc)
    } else if (entry.isFile()) {
      const rel = path.relative(base, full)
      if (!isExcluded(rel)) acc.push(full)
    }
  }
  return acc
}

function sha256File(file) {
  const hash = crypto.createHash('sha256')
  hash.update(fs.readFileSync(file))
  return hash.digest('hex')
}

/** 道具图标运行时规范尺寸（AGENTS.md §2：道具图标统一 48px WebP q90）。 */
const ITEM_ICON_PIXELS = 48

/** 只解析 WebP 尺寸头，不解码像素；格式或头部不合法时返回 null，I/O 错误由调用方报告。 */
function webpPixelSize(file) {
  const data = fs.readFileSync(file)
  if (data.length < 20 || data.toString('latin1', 0, 4) !== 'RIFF' || data.toString('latin1', 8, 12) !== 'WEBP') return null
  const riffSize = data.readUInt32LE(4) + 8
  const chunkSize = data.readUInt32LE(16)
  if (riffSize > data.length || 20 + chunkSize + (chunkSize % 2) > riffSize) return null
  const tag = data.toString('latin1', 12, 16)
  if (tag === 'VP8L' && chunkSize >= 5 && data[20] === 0x2f && (data[24] & 0xe0) === 0) {
    const bits = data.readUInt32LE(21)
    return { width: (bits & 0x3fff) + 1, height: ((bits >> 14) & 0x3fff) + 1 }
  }
  if (tag === 'VP8 ' && chunkSize >= 10 && (data[20] & 1) === 0 && data.readUIntLE(23, 3) === 0x2a019d) {
    return { width: data.readUInt16LE(26) & 0x3fff, height: data.readUInt16LE(28) & 0x3fff }
  }
  if (tag === 'VP8X' && chunkSize === 10 && riffSize >= 38) {
    return { width: data.readUIntLE(24, 3) + 1, height: data.readUIntLE(27, 3) + 1 }
  }
  return null
}

/**
 * 从 Vite .vite/manifest.json 提取 HD 图标别名。
 * 仅覆盖 assets/items 与 assets/buildings 两类已被 HD 画质包接管的目录；
 * glyphs/scenes 无 HD 母模，不产生别名（继续走 Web hash 资源）。
 * 附门规：assets/items 源图标必须严格 48x48 WebP，违例直接构建失败（防止候选稿混入生产死重）。
 */
function buildHdAliases(viteManifestPath, viteRoot) {
  let manifest
  try {
    manifest = JSON.parse(fs.readFileSync(viteManifestPath, 'utf8'))
  } catch (err) {
    throw new Error(`无法读取或解析 Vite manifest ${viteManifestPath}: ${err.message}`)
  }
  if (!manifest || typeof manifest !== 'object' || Array.isArray(manifest)) {
    throw new Error(`Vite manifest 必须是对象: ${viteManifestPath}`)
  }
  const aliases = {}
  const violations = []
  for (const [key, info] of Object.entries(manifest)) {
    if (!info || typeof info.file !== 'string' || !info.file || (info.src !== undefined && (typeof info.src !== 'string' || !info.src))) {
      throw new Error(`Vite manifest 条目格式错误: ${viteManifestPath} (${key})`)
    }
    const src = (info.src ?? key).replace(/\\/g, '/')
    const m = src.match(/(?:^|\/)assets\/(items|buildings)\/([^/]+)$/)
    if (!m) continue
    const kind = m[1]
    if (kind === 'items') {
      try {
        const size = /\.webp$/i.test(src) ? webpPixelSize(path.resolve(viteRoot, src)) : null
        if (!size) violations.push(`${src}（无法解析 WebP 尺寸头或后缀不是 .webp）`)
        else if (size.width !== ITEM_ICON_PIXELS || size.height !== ITEM_ICON_PIXELS) {
          violations.push(`${src} (${size.width}x${size.height})`)
        }
      } catch (err) {
        violations.push(`${src}（无法读取源文件: ${err.message}）`)
      }
    }
    const itemId = m[2].replace(/\.(webp|png|jpe?g|gif|svg)$/i, '')
    if (itemId) aliases[`/${info.file}`] = `/hd/${kind}/${itemId}.webp`
  }
  if (violations.length > 0) {
    throw new Error(`道具图标违规（要求 ${ITEM_ICON_PIXELS}x${ITEM_ICON_PIXELS} WebP，Vite root: ${viteRoot}）:\n  ${violations.join('\n  ')}`)
  }
  return Object.keys(aliases).length > 0 ? aliases : null
}

function main() {
  const args = parseArgs(process.argv.slice(2))
  const dir = path.resolve(args.dir)
  if (!fs.existsSync(dir) || !fs.statSync(dir).isDirectory()) {
    console.error(`[gen-asset-manifest] 目录不存在: ${dir}`)
    process.exit(1)
  }

  const viteRoot = path.resolve(args.viteRoot ?? DEFAULT_VITE_ROOT)
  const viteManifestPath = args.viteManifest === null
    ? path.join(dir, '.vite', 'manifest.json')
    : path.resolve(args.viteManifest)
  const hdAliases = args.noViteManifest ? null : buildHdAliases(viteManifestPath, viteRoot)

  const files = {}
  const absFiles = walk(dir)
  for (const abs of absFiles) {
    const rel = path.relative(dir, abs).split(path.sep).join('/')
    const key = `${args.prefix}/${rel}`
    const stat = fs.statSync(abs)
    files[key] = { sha256: sha256File(abs), size: stat.size }
  }

  const sortedFiles = Object.fromEntries(Object.keys(files).sort().map(k => [k, files[k]]))

  const manifest = {
    v: 1,
    generatedAt: new Date().toISOString(),
    files: sortedFiles,
  }

  if (hdAliases) manifest.hdAliases = hdAliases

  const outFile = args.out ? path.resolve(args.out) : path.join(dir, 'asset-manifest.json')
  fs.mkdirSync(path.dirname(outFile), { recursive: true })
  fs.writeFileSync(outFile, JSON.stringify(manifest, null, 2) + '\n')

  const aliasInfo = hdAliases ? `，hdAliases ${Object.keys(hdAliases).length} 条` : ''
  console.log(`[gen-asset-manifest] ${dir} → ${outFile}（${absFiles.length} 个资源${aliasInfo}）`)
}

try {
  main()
} catch (err) {
  console.error(`[gen-asset-manifest] ${err.message}`)
  process.exitCode = 1
}
