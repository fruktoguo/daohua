#!/usr/bin/env node
/**
 * 私有客户端模块挂载同步：把兄弟私有仓库 DaoHua-Private/frontend/antiMulti 以
 * junction（Windows）/ symlink（POSIX）挂载到主仓库 frontend/src/private-impl/。
 *
 * 幂等语义：
 * - 私有仓库存在 → 确保挂载点指向它（陈旧链接重建；生产 staging 复制的真实目录优先，不覆盖）；
 * - 私有仓库缺失 → 移除残留链接，保证开源口径构建不被污染。
 *
 * 拼装机制真源：docs/backend/private-modules.md。
 */
import { existsSync, lstatSync, rmSync, symlinkSync } from 'node:fs'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const privateFrontend = resolve(repoRoot, '..', 'DaoHua-Private', 'frontend', 'antiMulti')
const mountPoint = resolve(repoRoot, 'frontend', 'src', 'private-impl')

function lstatSafe(path) {
  try { return lstatSync(path) } catch { return null }
}

const stat = lstatSafe(mountPoint)
if (stat?.isSymbolicLink()) rmSync(mountPoint, { recursive: true, force: true })

if (!existsSync(privateFrontend)) {
  console.log('[private-modules] 未发现兄弟私有仓库 ../DaoHua-Private，跳过挂载（开源口径）')
  process.exit(0)
}

if (lstatSafe(mountPoint)?.isDirectory()) {
  // 生产 staging 复制的真实目录优先于链接，由其创建方管理生命周期。
  console.log('[private-modules] 挂载点已存在（真实目录），跳过链接创建')
  process.exit(0)
}

try {
  symlinkSync(privateFrontend, mountPoint, 'junction')
  console.log('[private-modules] 已挂载私有前端实现 -> frontend/src/private-impl（junction）')
} catch (error) {
  console.error(`[private-modules] 挂载失败：${error.message}`)
  process.exit(1)
}
