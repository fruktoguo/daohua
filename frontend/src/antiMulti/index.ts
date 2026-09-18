/**
 * 防多开模块统一出口。
 *
 * 三条防线各自独立开关（服务端 AntiMultiOpen:* 配置节）：
 * 1. 标签页互斥：installAntiMultiOpen 时按本地缓存开关安装，服务端经 HelloAck 下发最新值；
 * 2. 单账号互踢：纯服务端裁决，客户端只负责按关闭码停止自动重连并提示；
 * 3. 设备配额：本模块计算指纹并经 Hello 上报，配额判定完全在服务端。
 *
 * 与游戏主逻辑的唯一接缝：gameClient 握手时读取指纹/应用下发开关、连接关闭时按码提示，
 * 以及 App 挂载时安装守卫并渲染阻断页。
 */
import { getDeviceFingerprint } from './deviceFingerprint'
import { installTabGuard, type TabGuardHandle } from './tabGuard'
import { useAntiMultiOpenStore } from './store'

export { useAntiMultiOpenStore } from './store'
export { ANTI_MULTI_CLOSE_CODES, ANTI_MULTI_CLOSE_NOTICE_KEYS } from './codes'
export type { AntiMultiOpenConfigDto } from '../network/protocol'

let installed = false
let guardHandle: TabGuardHandle | null = null
let blockedTeardown: (() => void) | null = null

/** 由网络层注册：标签页被阻断时需要执行的拆除动作（断开 socket、停止心跳与自动重连）。 */
export function registerBlockedTeardown(teardown: () => void): void {
  blockedTeardown = teardown
}

function handleBlocked(): void {
  useAntiMultiOpenStore.getState().setTabBlocked(true)
  try {
    blockedTeardown?.()
  } catch {
    // 拆除动作失败不影响阻断态展示。
  }
}

/** 应用启动时安装一次防多开守卫；重复调用幂等。 */
export function installAntiMultiOpen(): void {
  if (installed || typeof window === 'undefined') return
  installed = true
  // 指纹采集提前预热，避免首次握手时阻塞网络链路。
  void getDeviceFingerprint()
  if (!useAntiMultiOpenStore.getState().tabGuardEnabled) return
  guardHandle = installTabGuard(handleBlocked)
}

/** 当前标签页是否已被阻断（供网络层阻止重连尝试）。 */
export function isTabBlocked(): boolean {
  return useAntiMultiOpenStore.getState().tabBlocked
}

/** 握手载荷携带的设备指纹；未采集完成或失败时返回 null，由服务端按开关决定是否放行。 */
export async function getHelloDeviceFingerprint(): Promise<string | null> {
  return getDeviceFingerprint()
}

/** 被阻断页请求接管当前运行页（原页让位，本页刷新后重新竞逐）。 */
export function requestTabTakeover(): void {
  guardHandle?.requestTakeover()
}

/** 应用服务端经 HelloAck 下发的防多开开关，并持久化到本地供下次连接建立前读取。 */
export function applyAntiMultiOpenConfig(config: { tabGuardEnabled?: boolean } | null | undefined): void {
  if (!config || typeof config.tabGuardEnabled !== 'boolean') return
  useAntiMultiOpenStore.getState().setTabGuardEnabled(config.tabGuardEnabled)
}
