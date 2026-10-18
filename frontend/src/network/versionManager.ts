/**
 * ==============================================================================
 * 文件名称: versionManager.ts
 * 主要职责: 道化太初 客户端版本更新感知、平滑自愈与防死循环刷新管理中心。
 *
 * 核心机制：
 * 1. 运行基准建立与版本对齐：
 *    - 读取构建期注入的 VITE_APP_VERSION 作为客户端静态包版本；
 *    - 维护当前会话与服务端的握手版本基线 baselineServerVersion；
 * 2. 多重感知通道：
 *    - WebSocket 握手 (HelloAck)：重连后收到新版 serverVersion 立即触发自动重载；
 *    - 后台轮询与唤醒探针：45 秒周期探针，并在 visibilitychange / focus 时主动拉取 /api/v1/version；
 *    - 动态分包异常自愈：监听 vite:preloadError 与模块 404 异常，自动重载冲刷旧资源；
 * 3. 防无限重载死循环熔断保护 (Anti-Loop Guard)：
 *    - 基于 sessionStorage 记录短期内的重载目标与频次；
 *    - 20 秒内针对同一目标版本重试刷新超限 (>=2 次) 判定为持久强缓存阻断，立即熔断自动刷新，
 *      降级为展示引导修士按 Ctrl+F5 强制刷新的友好提示，杜绝死循环白屏或闪烁风暴。
 * ==============================================================================
 */
import { useGameStore } from '../game/gameStore'
import { getT } from '../i18n'

/** 客户端构建注入的版本号，缺省为 'dev' */
export const CLIENT_VERSION = String(import.meta.env.VITE_APP_VERSION ?? 'dev')

const RELOAD_GUARD_KEY = 'daohua.version.reload_guard'
const GUARD_WINDOW_MS = 20_000
const MAX_RELOAD_ATTEMPTS = 2

interface ReloadGuardRecord {
  targetVersion: string
  count: number
  timestamp: number
}

function readGuard(): ReloadGuardRecord | null {
  try {
    const storage = typeof window !== 'undefined' ? window.sessionStorage : globalThis.sessionStorage
    const raw = storage?.getItem(RELOAD_GUARD_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as ReloadGuardRecord
    if (!parsed || typeof parsed.targetVersion !== 'string' || typeof parsed.count !== 'number' || typeof parsed.timestamp !== 'number') {
      return null
    }
    return parsed
  } catch {
    return null
  }
}

function writeGuard(targetVersion: string, count: number): void {
  try {
    const storage = typeof window !== 'undefined' ? window.sessionStorage : globalThis.sessionStorage
    storage?.setItem(
      RELOAD_GUARD_KEY,
      JSON.stringify({
        targetVersion,
        count,
        timestamp: Date.now(),
      }),
    )
  } catch {
    // 忽略部分无痕隐私环境对 sessionStorage 的访问受限
  }
}


/** 是否处于死循环熔断态 */
export function isReloadCircuitBroken(targetVersion: string): boolean {
  const guard = readGuard()
  if (!guard) return false
  const elapsed = Date.now() - guard.timestamp
  return elapsed < GUARD_WINDOW_MS && guard.targetVersion === targetVersion && guard.count >= MAX_RELOAD_ATTEMPTS
}

let baselineServerVersion: string | null = null
let isReloading = false
/** 仅供测试环境重置会话内版本状态 */
export function _resetVersionStateForTest(): void {
  baselineServerVersion = null
  isReloading = false
  lastApiCheckAt = 0
}


/** 触发版本更新与页面自动重载 */
export function triggerVersionReload(newVersion: string, reason: string): void {
  if (isReloading) return

  // 1. 检查防死循环熔断保护
  if (isReloadCircuitBroken(newVersion)) {
    console.warn(`[VersionManager] 目标版本 ${newVersion} 短期内重载超限，已自动熔断防止死循环，原因: ${reason}`)
    useGameStore.getState().setNotice(getT()('common.net.versionCircuitBroken'))
    window.dispatchEvent(
      new CustomEvent('daohua:version-circuit-broken', {
        detail: { targetVersion: newVersion, reason },
      }),
    )
    return
  }

  isReloading = true
  const guard = readGuard()
  const nextCount =
    guard && guard.targetVersion === newVersion && Date.now() - guard.timestamp < GUARD_WINDOW_MS
      ? guard.count + 1
      : 1
  writeGuard(newVersion, nextCount)

  console.log(`[VersionManager] 检测到仙途版本更新 (${reason}): ${baselineServerVersion ?? CLIENT_VERSION} -> ${newVersion}，正在自动刷新...`)
  useGameStore.getState().setNotice(getT()('common.net.versionUpdating'))
  window.dispatchEvent(
    new CustomEvent('daohua:version-update', {
      detail: { currentVersion: baselineServerVersion ?? CLIENT_VERSION, newVersion, reason },
    }),
  )

  window.setTimeout(() => {
    window.location.reload()
  }, 300)
}

/**
 * 接收来自 WebSocket 握手或 API 探针的服务端版本更新通知
 */
export function notifyServerVersion(serverVersion: string): void {
  if (!serverVersion || serverVersion === 'unknown') return

  // 1. 首次建立服务端版本基线
  if (baselineServerVersion === null) {
    baselineServerVersion = serverVersion

    // 检查生产环境下是否从旧浏览器缓存拉起：
    // 非开发态且客户端版本有真实值且服务端也有真实值时，若静态代码版本直接落后于服务端当前版本，判定缓存过期
    if (!import.meta.env.DEV && CLIENT_VERSION !== 'dev' && serverVersion !== 'dev') {
      if (CLIENT_VERSION !== serverVersion) {
        triggerVersionReload(serverVersion, '静态包版本落后于服务端最新版本')
        return
      }
    }

    // 若当前版本已成功追赶到历史熔断目标，解除熔断限制
    const guard = readGuard()
    if (guard && guard.targetVersion === serverVersion) {
      try { sessionStorage.removeItem(RELOAD_GUARD_KEY) } catch {}
    }
    return
  }

  // 2. 会话生命周期内服务端版本发生漂移（如服务发布新镜像后 WebSocket 重连成功）
  if (serverVersion !== baselineServerVersion) {
    triggerVersionReload(serverVersion, '会话内服务端版本变更')
  }
}

let lastApiCheckAt = 0

/** 通过服务端轻量公开版本端点主动校验版本 */
export async function checkVersionFromApi(): Promise<void> {
  const now = Date.now()
  if (now - lastApiCheckAt < 10_000) return
  lastApiCheckAt = now

  try {
    const response = await fetch(`/api/v1/version?_t=${now}`, {
      method: 'GET',
      headers: {
        'Cache-Control': 'no-cache, no-store',
        Pragma: 'no-cache',
      },
    })
    if (!response.ok) return
    const data = (await response.json()) as { version?: string }
    if (data && typeof data.version === 'string' && data.version.length > 0) {
      notifyServerVersion(data.version)
    }
  } catch {
    // 离线态静默放过，等待下次心跳或重连
  }
}

/**
 * 启动后台版本探针守护（45s 定时轮询 + 切前台唤醒检查）
 */
export function startVersionWatcher(): () => void {
  // 初次启动后先静默检测一次
  void checkVersionFromApi()

  const pollTimer = window.setInterval(() => {
    void checkVersionFromApi()
  }, 45_000)

  const onVisibilityChange = () => {
    if (document.visibilityState === 'visible') void checkVersionFromApi()
  }
  const onFocus = () => void checkVersionFromApi()
  document.addEventListener('visibilitychange', onVisibilityChange)
  window.addEventListener('focus', onFocus)

  return () => {
    window.clearInterval(pollTimer)
    document.removeEventListener('visibilitychange', onVisibilityChange)
    window.removeEventListener('focus', onFocus)
  }
}

/**
 * 安装动态模块 (Chunk) 加载失败异常自愈监听器
 */
export function installChunkLoadErrorWatcher(): void {
  // Vite 原生分包加载失败事件
  window.addEventListener('vite:preloadError', event => {
    event.preventDefault()
    console.warn('[VersionManager] vite:preloadError 捕获到动态模块加载失败，执行自愈刷新。')
    triggerVersionReload('chunk-preload-failed', '动态分包资源404')
  })

  // 全局未捕获异常检查
  window.addEventListener('error', event => {
    const message = event?.message || ''
    if (
      typeof message === 'string' &&
      (message.includes('Failed to fetch dynamically imported module') ||
        message.includes('error loading dynamically imported module') ||
        message.includes('Importing a module script failed'))
    ) {
      console.warn('[VersionManager] 捕获到动态模块加载异常，执行自愈刷新:', message)
      triggerVersionReload('dynamic-import-failed', '动态模块引入失败')
    }
  })
}
