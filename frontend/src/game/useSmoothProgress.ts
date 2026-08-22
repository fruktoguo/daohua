import { useEffect, type RefObject } from 'react'
import { useGameStore } from './gameStore'
import type { QueueEntry } from './types'

/**
 * 平滑进度动画驱动 Hook 的配置参数
 */
export interface UseSmoothProgressOptions {
  /** 目标行动条目对象（包含总时长、已执行时长、状态等） */
  entry?: QueueEntry | null
  /** 进度条填充元素的 DOM 引用（通过直接修改 scaleX 避免 React 频繁 re-render） */
  fillRef: RefObject<HTMLElement | null>
  /** 进度文字元素的 DOM 引用 */
  textRef?: RefObject<HTMLElement | null>
  /** 自定义进度文字格式化函数 */
  textFormat?: (currentSec: number, duration: number, percent: number) => string
}

/**
 * 统一的平滑行动进度驱动 Hook
 * 
 * 【设计机制】：
 * 1. 使用全局统一的 lastSyncPerformanceTime 作为基准时间，确保所有面板（顶部顶栏、侧边栏、详情总目标、角色动作条等）100% 绝对同步；
 * 2. 通过 requestAnimationFrame 高频逐帧插值计算微小流逝时间；
 * 3. 直接通过 Ref 操作 DOM 样式 (style.transform) 和文本 (textContent)，零触发 React 组件树重渲染，保障 60+ FPS 流畅度。
 * 
 * @param options Hook 参数选项
 */
export function useSmoothProgressRef({
  entry,
  fillRef,
  textRef,
  textFormat,
}: UseSmoothProgressOptions): void {
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)

  useEffect(() => {
    if (!entry) return
    const isRunning = entry.status === 'running'
    const duration = Math.max(1, entry.durationSeconds)
    const baseProgress = entry.progressSeconds
    const isInfinite = entry.remainingRepeats < 0

    let frameId: number
    const tick = () => {
      const now = performance.now()
      // 计算自上次服务端时间戳同步以来经过的秒数
      const elapsed = isRunning ? Math.max(0, (now - lastSyncPerformanceTime) / 1000) : 0

      let currentSec: number
      if (!isRunning) {
        currentSec = baseProgress
      } else if (isInfinite && duration > 0) {
        // 无限循环模式下进度取模
        currentSec = (baseProgress + elapsed) % duration
      } else {
        currentSec = Math.min(duration, Math.max(0, baseProgress + elapsed))
      }

      const ratio = duration > 0 ? Math.min(1, Math.max(0, currentSec / duration)) : 0
      const percent = Math.round(ratio * 100)

      if (fillRef.current) {
        fillRef.current.style.transform = `scaleX(${ratio})`
      }
      if (textRef?.current) {
        if (textFormat) {
          textRef.current.textContent = textFormat(currentSec, duration, percent)
        } else {
          textRef.current.textContent = `${Math.floor(currentSec)}s / ${duration}s`
        }
      }

      if (isRunning) {
        frameId = requestAnimationFrame(tick)
      }
    }

    frameId = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(frameId)
  }, [
    entry?.entryId,
    entry?.status,
    entry?.durationSeconds,
    entry?.progressSeconds,
    entry?.remainingRepeats,
    lastSyncPerformanceTime,
    fillRef,
    textRef,
    textFormat,
  ])
}
