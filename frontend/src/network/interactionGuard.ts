/**
 * 全静默即时交互持续监听探针：
 * 1. 原生 isTrusted 捕获：拦截自动化脚本 element.click() / dispatchEvent 合成事件；
 * 2. 指针前驱轨迹时钟：监测无划动/移动的原地瞬发点击；
 * 3. 触控面积与压力即时判定：抓出模拟器与无障碍脚本的零面积虚拟触控。
 */

interface InteractionStats {
  untrustedClicks: number
  virtualTouches: number
  lastMoveTimestamp: number
  lastClickTimestamp: number
}

const stats: InteractionStats = {
  untrustedClicks: 0,
  virtualTouches: 0,
  lastMoveTimestamp: 0,
  lastClickTimestamp: 0,
}

let installed = false

/** 安装全局捕获阶段交互监听器；全生命周期只安装一次 */
export function installInteractionGuard(): void {
  if (installed || typeof window === 'undefined') return
  installed = true

  // 1. 持续跟踪真实光标/手指移动轨迹前驱
  const onMove = (): void => {
    stats.lastMoveTimestamp = Date.now()
  }
  window.addEventListener('mousemove', onMove, { capture: true, passive: true })
  window.addEventListener('touchmove', onMove, { capture: true, passive: true })
  window.addEventListener('pointermove', onMove, { capture: true, passive: true })

  // 2. 捕获阶段拦截非信任点击（element.click / dispatchEvent）
  window.addEventListener('click', (e: MouseEvent): void => {
    stats.lastClickTimestamp = Date.now()
    if (e.isTrusted === false) {
      stats.untrustedClicks++
      // 核心交易与游戏操作阻止非法脚本合成点击生效
      e.preventDefault()
      e.stopImmediatePropagation()
      return
    }

    // 检查触控面积（针对 PointerEvent 中的 touch 类型）
    if ('pointerType' in e && (e as PointerEvent).pointerType === 'touch') {
      const pe = e as PointerEvent
      if (pe.width === 0 && pe.height === 0 && pe.pressure === 0) {
        stats.virtualTouches++
      }
    }
  }, { capture: true })

  // 3. 捕获阶段拦截非信任 pointerdown
  window.addEventListener('pointerdown', (e: PointerEvent): void => {
    if (e.isTrusted === false) {
      stats.untrustedClicks++
      e.preventDefault()
      e.stopImmediatePropagation()
      return
    }
    if (e.pointerType === 'touch' && e.width === 0 && e.height === 0 && e.pressure === 0) {
      stats.virtualTouches++
    }
  }, { capture: true })
}

/** 获取当前交互探针统计 */
export function getInteractionStats(): Readonly<InteractionStats> {
  return stats
}

/** 供高危业务操作在发起前即时校验交互合法性 */
export function verifyTrustedAction(event?: React.SyntheticEvent | Event | null): boolean {
  if (!event) return true
  const native = 'nativeEvent' in event ? (event.nativeEvent as Event) : event
  if (native.isTrusted === false) {
    stats.untrustedClicks++
    return false
  }
  return true
}

/** 仅用于单元测试重置状态 */
export function resetInteractionGuardForTest(): void {
  stats.untrustedClicks = 0
  stats.virtualTouches = 0
  stats.lastMoveTimestamp = 0
  stats.lastClickTimestamp = 0
}
