import { useEffect, useRef, type RefObject } from 'react'
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
  /** 百分比文字元素的 DOM 引用 (例如 <strong>{percent}%</strong>) */
  percentRef?: RefObject<HTMLElement | null>
  /** 附加元数据元素的 DOM 引用 (例如 <span>路程 x / y 里</span>) */
  metaRef?: RefObject<HTMLElement | null>
  /** 自定义元数据文字格式化函数 */
  metaFormat?: (currentSec: number, duration: number, ratio: number, percent: number) => string
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
  percentRef,
  metaRef,
  metaFormat,
}: UseSmoothProgressOptions): void {
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)
  // 调用方通常在渲染中创建格式化闭包；用 ref 读取最新回调，避免每次父组件更新都重启 RAF。
  const textFormatRef = useRef(textFormat)
  const metaFormatRef = useRef(metaFormat)
  textFormatRef.current = textFormat
  metaFormatRef.current = metaFormat

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
        // 无限循环模式下进度取模（消除负数取模异常）
        currentSec = ((baseProgress + elapsed) % duration + duration) % duration
      } else {
        currentSec = Math.min(duration, Math.max(0, baseProgress + elapsed))
      }

      const ratio = duration > 0 ? Math.min(1, Math.max(0, currentSec / duration)) : 0
      const percent = Math.round(ratio * 100)

      if (fillRef.current) {
        fillRef.current.style.transform = `scaleX(${ratio})`
      }
      if (textRef?.current) {
        if (textFormatRef.current) {
          textRef.current.textContent = textFormatRef.current(currentSec, duration, percent)
        } else {
          textRef.current.textContent = `${Math.floor(currentSec)}s / ${duration}s`
        }
      }
      if (percentRef?.current) {
        percentRef.current.textContent = `${percent}%`
      }
      if (metaRef?.current && metaFormatRef.current) {
        metaRef.current.textContent = metaFormatRef.current(currentSec, duration, ratio, percent)
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
    percentRef,
    metaRef,
  ])
}
