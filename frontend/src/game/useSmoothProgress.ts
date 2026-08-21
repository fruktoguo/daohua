import { useEffect, type RefObject } from 'react'
import { useGameStore } from './gameStore'
import type { QueueEntry } from './types'

export interface UseSmoothProgressOptions {
  entry?: QueueEntry | null
  fillRef: RefObject<HTMLElement | null>
  textRef?: RefObject<HTMLElement | null>
  textFormat?: (currentSec: number, duration: number, percent: number) => string
}

/**
 * 统一的平滑行动进度驱动 Hook
 * 使用全局统一的 lastSyncPerformanceTime 基准，确保所有面板（顶部、侧边栏、详情总目标、角色动作条）100% 同步
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
      const elapsed = isRunning ? Math.max(0, (now - lastSyncPerformanceTime) / 1000) : 0

      let currentSec: number
      if (!isRunning) {
        currentSec = baseProgress
      } else if (isInfinite && duration > 0) {
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
