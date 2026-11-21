/**
 * 轻量即时交互拦截探针：
 * 在捕获阶段拦截自动化脚本 element.click() / dispatchEvent 的非信任点击与 pointerdown，
 * 并累计 untrustedClicks 供无头环境探测消费（不挂载高频指针移动监听）。
 */

interface InteractionStats {
  untrustedClicks: number
}

const stats: InteractionStats = {
  untrustedClicks: 0,
}

let installed = false

/** 安装全局捕获阶段交互监听器；全生命周期只安装一次 */
export function installInteractionGuard(): void {
  if (installed || typeof window === 'undefined') return
  installed = true

  const onUntrustedCheck = (e: Event): void => {
    if (e.isTrusted === false) {
      stats.untrustedClicks++
      e.preventDefault()
      e.stopImmediatePropagation()
    }
  }

  window.addEventListener('click', onUntrustedCheck, { capture: true })
  window.addEventListener('pointerdown', onUntrustedCheck, { capture: true })
}

/** 获取当前交互探针统计 */
export function getInteractionStats(): Readonly<InteractionStats> {
  return stats
}

/** 仅用于单元测试重置状态 */
export function resetInteractionGuardForTest(): void {
  stats.untrustedClicks = 0
}
