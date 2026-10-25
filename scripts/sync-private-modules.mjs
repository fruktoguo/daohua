#!/usr/bin/env node
/**
 * 私有客户端模块挂载同步：把兄弟私有仓库 DaoHua-Private/frontend/antiMulti 以
 * junction（Windows）/ symlink（POSIX）挂载到主仓库 frontend/src/private-impl/。
 *
 * 幂等语义：
 * - 私有仓库存在 → 确保挂载点可解析到私有实现入口（不可解析的陈旧链接重建）；
 * - 私有仓库缺失 → 移除残留链接，保证开源口径构建不被污染。
 *
 * 跨环境约束（关键）：挂载目标必须由「将来消费它的那套工具链」创建。
 * Windows 的 junction 在 WSL 里可正常解析，但 WSL（node 跑在 /mnt/d 上）创建的
 * symlink 其目标是 /mnt/d/... 这类 WSL 专属路径，Windows 侧完全无法解析——
 * 表现为 vite 的 existsSync 判定私有模块缺失而回退开源 stub，握手不带模块声明，
 * 服务端随即以 4010 拒绝连接。因此这里只重建「不可解析」的挂载点：
 * 能解析到 index.ts 就原样保留，谁创建的都不动。
 *
 * 拼装机制真源：docs/backend/private-modules.md。
 */
import { existsSync, lstatSync, rmSync, symlinkSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const privateFrontend = resolve(repoRoot, '..', 'DaoHua-Private', 'frontend', 'antiMulti')
const mountPoint = resolve(repoRoot, 'frontend', 'src', 'private-impl')
const mountEntry = resolve(mountPoint, 'index.ts')

function lstatSafe(path) {
  try { return lstatSync(path) } catch { return null }
}

/** 挂载点是否已能解析到私有实现入口（跨环境唯一可用性判据）。 */
function mountResolves() {
  try { return existsSync(mountEntry) } catch { return false }
}

const stat = lstatSafe(mountPoint)
// 真实目录（生产 staging 复制产物）由其创建方管理生命周期，不在此处替换。
if (stat && !stat.isSymbolicLink() && stat.isDirectory()) {
  console.log('[private-modules] 挂载点已存在（真实目录），跳过链接创建')
  process.exit(0)
}

if (!existsSync(privateFrontend)) {
  if (stat?.isSymbolicLink()) rmSync(mountPoint, { recursive: true, force: true })
  console.log('[private-modules] 未发现兄弟私有仓库 ../DaoHua-Private，跳过挂载（开源口径）')
  process.exit(0)
}

// 已可解析到入口：保留既有挂载（可能由另一套工具链创建，重建会破坏对端可见性）。
if (stat?.isSymbolicLink() && mountResolves()) {
  console.log('[private-modules] 挂载已生效，保持现状')
  process.exit(0)
}

if (stat?.isSymbolicLink()) rmSync(mountPoint, { recursive: true, force: true })

try {
  symlinkSync(privateFrontend, mountPoint, 'junction')
  if (!mountResolves()) {
    console.error('[private-modules] 挂载创建后仍无法解析到 index.ts，请检查私有仓库结构')
    process.exit(1)
  }
  console.log('[private-modules] 已挂载私有前端实现 -> frontend/src/private-impl（junction）')
} catch (error) {
  console.error(`[private-modules] 挂载失败：${error.message}`)
  process.exit(1)
}
