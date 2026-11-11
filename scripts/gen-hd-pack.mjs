#!/usr/bin/env node
// ==============================================================================
// 脚本名称: gen-hd-pack.mjs
// 主要职责: 生成 Android HD 画质资源包（art/ HD 母模优先，src 低清资源兜底）
//
// 背景:
//   Android 壳改为 Web 优先 + 云端热更后，HD 画质不再走 Vite hash 注入，
//   而是作为独立「画质包」由 /hd/* 静态路由分发，原生层经 asset-manifest
//   别名把 /assets/*-HASH 图标请求透明改写到本包。
//
// 包内容:
//   items/     物品/装备/功法图标：art/items + art/legacy_128 (HD) 优先，
//              frontend/src/assets/items (Web 48px) 兜底，按 itemId 去重
//   buildings/ 建筑图标：art/buildings (HD) 优先，src/assets/buildings 兜底
//   avatars/   角色/怪物立绘：art/avatars/*.webp（1024px 母模优化版）
//   asset-manifest.json  由 gen-asset-manifest.mjs 以 --prefix /hd 生成
//
// 用法:
//   node scripts/gen-hd-pack.mjs [outDir]     # 默认 artifacts/hd-pack
// 产物上传由 scripts/upload-hd-pack.sh 负责（/opt/daohua/static/hd）。
// ==============================================================================

import fs from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)
const rootDir = path.resolve(__dirname, '..')

const outDir = path.resolve(process.argv[2] || path.join(rootDir, 'artifacts', 'hd-pack'))

const SRC_ITEMS = path.join(rootDir, 'frontend', 'src', 'assets', 'items')
const SRC_BUILDINGS = path.join(rootDir, 'frontend', 'src', 'assets', 'buildings')
const ART_ITEMS = path.join(rootDir, 'art', 'items')
const ART_LEGACY = path.join(rootDir, 'art', 'legacy_128')
const ART_BUILDINGS = path.join(rootDir, 'art', 'buildings')
const ART_AVATARS = path.join(rootDir, 'art', 'avatars')

const IMAGE_EXTS = new Set(['.webp', '.png', '.jpg', '.jpeg'])

/** 资源 ID 即去扩展名的文件名（与 itemIcons.ts / buildingIcons.ts 注册约定一致）。 */
function itemIdOf(fileName) {
  return fileName.replace(/\.(webp|png|jpe?g|gif|svg)$/i, '')
}

function extRank(ext) {
  if (ext === '.webp') return 3
  if (ext === '.png') return 2
  return 1
}

function listImages(dir) {
  if (!fs.existsSync(dir)) return []
  return fs.readdirSync(dir, { withFileTypes: true })
    .filter(e => e.isFile() && IMAGE_EXTS.has(path.extname(e.name).toLowerCase()))
    .map(e => e.name)
}

/**
 * 汇总某资源类别的全部候选文件并按 itemId 去重：
 * 同一 itemId 可能同时存在 HD 母模与 Web 低清版本，按 (tier, ext) 取最优。
 * tier: art 母模目录(3/2) > src 运行时目录(1)；ext: webp > png > 其他位图。
 */
function collectPackFiles(buckets) {
  // buckets: [{dir, tier}]，tier 越大越优先
  const best = new Map() // itemId -> {file, tier, extRank, abs}
  for (const { dir, tier } of buckets) {
    for (const name of listImages(dir)) {
      const id = itemIdOf(name)
      if (!id) continue
      const ext = path.extname(name).toLowerCase()
      const abs = path.join(dir, name)
      const score = extRank(ext)
      const prev = best.get(id)
      if (!prev || tier > prev.tier || (tier === prev.tier && score > prev.score)) {
        best.set(id, { file: name, tier, score, abs })
      }
    }
  }
  return [...best.values()]
}

function emitDir(files, subDir) {
  const target = path.join(outDir, subDir)
  fs.mkdirSync(target, { recursive: true })
  for (const f of files) {
    fs.copyFileSync(f.abs, path.join(target, f.file))
  }
  return files.length
}

function main() {
  fs.rmSync(outDir, { recursive: true, force: true })
  fs.mkdirSync(outDir, { recursive: true })

  const items = collectPackFiles([
    { dir: ART_ITEMS, tier: 3 },
    { dir: ART_LEGACY, tier: 2 },
    { dir: SRC_ITEMS, tier: 1 },
  ])
  const buildings = collectPackFiles([
    { dir: ART_BUILDINGS, tier: 3 },
    { dir: SRC_BUILDINGS, tier: 1 },
  ])

  // 立绘只取 art/avatars HD 母模（Web 128px 版本由标准资源链兜底，不进 HD 包）
  const avatars = listImages(ART_AVATARS)
    .filter(name => path.extname(name).toLowerCase() === '.webp')
    .map(name => ({ file: name, abs: path.join(ART_AVATARS, name) }))

  const itemCount = emitDir(items, 'items')
  const buildingCount = emitDir(buildings, 'buildings')
  const avatarCount = emitDir(avatars, 'avatars')

  // 复用清单生成器产出 /hd 前缀清单（键形如 "/hd/items/foo.webp"）
  const manifestScript = path.join(__dirname, 'gen-asset-manifest.mjs')
  execFileSync(process.execPath, [manifestScript, outDir, '--prefix', '/hd'], { stdio: 'inherit' })

  const totalSize = duDir(outDir)
  console.log(`[gen-hd-pack] HD 资源包生成完成 → ${outDir}`)
  console.log(`  • items: ${itemCount} | buildings: ${buildingCount} | avatars: ${avatarCount} | 总大小: ${formatMb(totalSize)}`)
}

function duDir(dir) {
  let total = 0
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name)
    if (entry.isDirectory()) total += duDir(full)
    else if (entry.isFile()) total += fs.statSync(full).size
  }
  return total
}

function formatMb(bytes) {
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`
}

main()
