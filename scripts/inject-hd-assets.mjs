#!/usr/bin/env node
// ==============================================================================
// 脚本名称: inject-hd-assets.mjs
// 主要职责: 为 Android APK 注入高清母模资源 (立绘 1024px, 建筑 128px, 道具 128px)
//
// 模式:
//   apply        备份低清源码资源，并将 art/ 中的高清母模注入前端源码目录
//   restore      从备份中 100% 原样还原前端低清资源，保持 Git 工作区干净
//   overlay-dist 编译后将 1024px 高清立绘覆盖到目标 assets/www/avatars
// ==============================================================================

import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)
const rootDir = path.resolve(__dirname, '..')

const backupDir = path.join(rootDir, 'artifacts', '.hd-backup')
const buildingsSrc = path.join(rootDir, 'frontend', 'src', 'assets', 'buildings')
const itemsSrc = path.join(rootDir, 'frontend', 'src', 'assets', 'items')

const artBuildings = path.join(rootDir, 'art', 'buildings')
const artItems = path.join(rootDir, 'art', 'items')
const artLegacy128 = path.join(rootDir, 'art', 'legacy_128')
const artAvatars = path.join(rootDir, 'art', 'avatars')

function copyDirRecursive(src, dest) {
  if (!fs.existsSync(dest)) fs.mkdirSync(dest, { recursive: true })
  const entries = fs.readdirSync(src, { withFileTypes: true })
  for (const entry of entries) {
    const srcPath = path.join(src, entry.name)
    const destPath = path.join(dest, entry.name)
    if (entry.isDirectory()) {
      copyDirRecursive(srcPath, destPath)
    } else {
      fs.copyFileSync(srcPath, destPath)
    }
  }
}

const command = process.argv[2]

if (command === 'apply') {
  console.log('[HD Assets] 开始备份 Web 低清资源...')
  if (fs.existsSync(backupDir)) fs.rmSync(backupDir, { recursive: true, force: true })
  fs.mkdirSync(backupDir, { recursive: true })

  copyDirRecursive(buildingsSrc, path.join(backupDir, 'buildings'))
  copyDirRecursive(itemsSrc, path.join(backupDir, 'items'))

  console.log('[HD Assets] 注入高清建筑图标 (128x128)...')
  let buildingCount = 0
  if (fs.existsSync(artBuildings)) {
    const files = fs.readdirSync(artBuildings).filter(f => f.endsWith('.webp'))
    for (const f of files) {
      fs.copyFileSync(path.join(artBuildings, f), path.join(buildingsSrc, f))
      buildingCount++
    }
  }

  console.log('[HD Assets] 注入高清道具与装备图标 (128x128 ~ 256x256)...')
  let itemCount = 0
  if (fs.existsSync(artItems)) {
    const files = fs.readdirSync(artItems)
    for (const f of files) {
      fs.copyFileSync(path.join(artItems, f), path.join(itemsSrc, f))
      itemCount++
    }
  }

  if (fs.existsSync(artLegacy128)) {
    const files = fs.readdirSync(artLegacy128).filter(f => f.endsWith('.png'))
    for (const f of files) {
      const destPng = path.join(itemsSrc, f)
      fs.copyFileSync(path.join(artLegacy128, f), destPng)
      // 若存在同名的旧 48px .webp，删除它以便命中 128px .png
      const baseName = f.replace(/\.png$/i, '')
      const staleWebp = path.join(itemsSrc, `${baseName}.webp`)
      if (fs.existsSync(staleWebp)) {
        fs.unlinkSync(staleWebp)
      }
      itemCount++
    }
  }

  console.log(`[HD Assets] 高清资源注入完成！(建筑: ${buildingCount} 栋, 道具/装备: ${itemCount} 个)`)
} else if (command === 'restore') {
  console.log('[HD Assets] 开始还原前端源码资源...')
  const backupBuildings = path.join(backupDir, 'buildings')
  const backupItems = path.join(backupDir, 'items')

  if (fs.existsSync(backupBuildings)) {
    fs.rmSync(buildingsSrc, { recursive: true, force: true })
    copyDirRecursive(backupBuildings, buildingsSrc)
  }
  if (fs.existsSync(backupItems)) {
    fs.rmSync(itemsSrc, { recursive: true, force: true })
    copyDirRecursive(backupItems, itemsSrc)
  }

  if (fs.existsSync(backupDir)) {
    fs.rmSync(backupDir, { recursive: true, force: true })
  }
  console.log('[HD Assets] 前端源码已 100% 恢复如初。')
} else if (command === 'overlay-dist') {
  const targetDir = process.argv[3]
  if (!targetDir || !fs.existsSync(targetDir)) {
    console.error('[HD Assets] 错误: 目标目录不存在:', targetDir)
    process.exit(1)
  }

  const avatarsTarget = path.join(targetDir, 'avatars')
  if (!fs.existsSync(avatarsTarget)) fs.mkdirSync(avatarsTarget, { recursive: true })

  console.log('[HD Assets] 注入超清立绘母模 (1024x1024) 到目标目录...')
  let avatarCount = 0
  if (fs.existsSync(artAvatars)) {
    const files = fs.readdirSync(artAvatars).filter(f => f.endsWith('.webp'))
    for (const f of files) {
      fs.copyFileSync(path.join(artAvatars, f), path.join(avatarsTarget, f))
      avatarCount++
    }
  }
  console.log(`[HD Assets] 超清立绘覆盖完成 (共 ${avatarCount} 个立绘)`)
} else {
  console.log('Usage: node scripts/inject-hd-assets.mjs [apply|restore|overlay-dist <targetDir>]')
}
