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
// HD 别名（可选）:
//   若 <dir>/.vite/manifest.json 存在（即对 frontend/dist 生成时），自动附带
//   hdAliases：把 /assets/<name>-<hash>.(webp|png) 图标产物映射到
//   /hd/items|buildings/<itemId>.webp，供 Android 原生层把 hash 图标请求透明改写为 HD 资源。
//
// 用法:
//   node scripts/gen-asset-manifest.mjs <dir> [--out <file>] [--prefix </hd>] [--vite-manifest <file>]
//     <dir>           被扫描目录（frontend/dist、android-shell assets/www、hd-pack 等）
//     --out           输出文件（默认 <dir>/asset-manifest.json）
//     --prefix        key 前缀（默认 ""；hd-pack 传 "/hd" 使 key 形如 "/hd/items/x.webp"）
//     --vite-manifest Vite .vite/manifest.json 路径（默认 <dir>/.vite/manifest.json；
//                     APK 基线清单场景传 frontend/dist 的产物，使包内同样携带 HD 别名）
// ==============================================================================

import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import { fileURLToPath } from 'node:url'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

const EXCLUDED_FILES = new Set([
  'index.html',
  'privacy.html',
  'app-version.json',
  'asset-manifest.json',
])
const EXCLUDED_DIRS = new Set(['assets', '.vite'])
const EXCLUDED_EXT = new Set(['.map'])

function parseArgs(argv) {
  const args = { dir: null, out: null, prefix: '', viteManifest: null }
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]
    if (a === '--out') args.out = argv[++i]
    else if (a === '--prefix') args.prefix = argv[++i]
    else if (a === '--vite-manifest') args.viteManifest = argv[++i]
    else if (!a.startsWith('-') && !args.dir) args.dir = a
    else {
      console.error(`[gen-asset-manifest] 未知参数: ${a}`)
      process.exit(2)
    }
  }
  if (!args.dir) {
    console.error('用法: node scripts/gen-asset-manifest.mjs <dir> [--out <file>] [--prefix </hd>] [--vite-manifest <file>]')
    process.exit(2)
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

/**
 * 从 Vite .vite/manifest.json 提取 HD 图标别名。
 * 仅覆盖 assets/items 与 assets/buildings 两类已被 HD 画质包接管的目录；
 * glyphs/scenes 无 HD 母模，不产生别名（继续走 Web hash 资源）。
 */
function buildHdAliases(viteManifestPath) {
  if (!viteManifestPath || !fs.existsSync(viteManifestPath)) return null
  let manifest
  try {
    manifest = JSON.parse(fs.readFileSync(viteManifestPath, 'utf8'))
  } catch (err) {
    console.warn(`[gen-asset-manifest] 警告: 无法解析 ${viteManifestPath}: ${err.message}`)
    return null
  }
  const aliases = {}
  for (const [key, info] of Object.entries(manifest)) {
    if (!info || typeof info.file !== 'string') continue
    const src = typeof info.src === 'string' ? info.src : key
    const m = src.replace(/\\/g, '/').match(/(?:^|\/)assets\/(items|buildings)\/([^/]+)$/)
    if (!m) continue
    const kind = m[1]
    const itemId = m[2].replace(/\.(webp|png|jpe?g|gif|svg)$/i, '')
    if (!itemId) continue
    aliases[`/${info.file}`] = `/hd/${kind}/${itemId}.webp`
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

  const viteManifestPath = args.viteManifest
    ? path.resolve(args.viteManifest)
    : path.join(dir, '.vite', 'manifest.json')
  const hdAliases = buildHdAliases(viteManifestPath)
  if (hdAliases) manifest.hdAliases = hdAliases

  const outFile = args.out ? path.resolve(args.out) : path.join(dir, 'asset-manifest.json')
  fs.mkdirSync(path.dirname(outFile), { recursive: true })
  fs.writeFileSync(outFile, JSON.stringify(manifest, null, 2) + '\n')

  const aliasInfo = hdAliases ? `，hdAliases ${Object.keys(hdAliases).length} 条` : ''
  console.log(`[gen-asset-manifest] ${dir} → ${outFile}（${absFiles.length} 个资源${aliasInfo}）`)
}

main()
