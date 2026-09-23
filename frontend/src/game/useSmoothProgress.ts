import { useEffect, useRef, type RefObject } from 'react'
import { formatNumber } from './format'
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
 * 1. 采用队列专属的 lastQueueSyncPerformanceTime 基准时间，彻底隔离非队列 patch（如打坐灵气、掉落资产等）的干扰；
 * 2. 具备单调不后退保护（Monotonic Protection）：在同一轮次中若服务端同步由于网络或量化滞后导致预测值偏小，绝对不向后抽搐；
 * 3. 具备换轮检测（Round Transition）：精确识别多轮循环与无限循环重置（-1 或 baseProgress 显著回落），即刻开启新一轮；
 * 4. 具备平滑追赶（Catch-up Smoothing）：当遇到跨秒跳步时以平滑加速过渡，消除生硬硬跳；
 * 5. 首帧即时同步：挂载与依赖变更时立即同步 DOM，零渲染延迟。
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
  const lastQueueSyncPerformanceTime = useGameStore(state => state.lastQueueSyncPerformanceTime ?? state.lastSyncPerformanceTime)
  // 调用方通常在渲染中创建格式化闭包；用 ref 读取最新回调，避免每次父组件更新都重启 RAF。
  const textFormatRef = useRef(textFormat)
  const metaFormatRef = useRef(metaFormat)
  textFormatRef.current = textFormat
  metaFormatRef.current = metaFormat

  // 跨帧状态追踪：保持单调递增并识别换轮
  const stateRef = useRef<{
    entryId: string
    remainingRepeats: number
    lastRenderedSec: number
    lastBaseProgress: number
    lastTickTime: number
  }>({
    entryId: '',
    remainingRepeats: 0,
    lastRenderedSec: 0,
    lastBaseProgress: 0,
    lastTickTime: 0,
  })

  useEffect(() => {
    if (!entry) return
    const isRunning = entry.status === 'running'
    const duration = Math.max(1, entry.durationSeconds)
    const baseProgress = entry.progressSeconds
    const isInfinite = entry.remainingRepeats < 0

    let frameId: number | undefined
    const tick = () => {
      const now = performance.now()

      // 1. 换轮与行动切换检测
      const isNewEntry = entry.entryId !== stateRef.current.entryId
      const isNewRound = !isNewEntry && (
        entry.remainingRepeats !== stateRef.current.remainingRepeats ||
        baseProgress < stateRef.current.lastBaseProgress - 0.5
      )

      if (isNewEntry || isNewRound) {
        stateRef.current.entryId = entry.entryId
        stateRef.current.remainingRepeats = entry.remainingRepeats
        stateRef.current.lastBaseProgress = baseProgress
        stateRef.current.lastRenderedSec = baseProgress
        stateRef.current.lastTickTime = now
      }

      // 2. 计算自上次队列同步以来的本地时间流逝（秒）
      const elapsed = isRunning ? Math.max(0, (now - lastQueueSyncPerformanceTime) / 1000) : 0

      // 3. 计算理论目标秒数
      let targetSec: number
      if (!isRunning) {
        targetSec = baseProgress
      } else if (isInfinite && duration > 0) {
        targetSec = ((baseProgress + elapsed) % duration + duration) % duration
      } else {
        targetSec = Math.min(duration, Math.max(0, baseProgress + elapsed))
      }

      // 4. 单调性保护与滤波平滑
      let currentSec: number
      if (!isRunning) {
        currentSec = targetSec
        stateRef.current.lastRenderedSec = currentSec
      } else {
        // 无限循环在周期性跨越边界时的处理（从接近 duration 跨回到 0）
        if (isInfinite && duration > 0) {
          if (stateRef.current.lastRenderedSec > duration * 0.8 && targetSec < duration * 0.2) {
            stateRef.current.lastRenderedSec = 0
          }
        }

        // 单调不后退保护：若服务端同步值由于网络延迟滞后，当前帧绝不后退
        if (targetSec < stateRef.current.lastRenderedSec) {
          currentSec = Math.min(duration, stateRef.current.lastRenderedSec)
        } else {
          // 步长滤波平滑：若服务端出现较大跳步（如整秒跳跃 > 0.3s），平滑加速追赶，杜绝突兀硬蹦
          const gap = targetSec - stateRef.current.lastRenderedSec
          if (gap > 0.3 && stateRef.current.lastTickTime > 0) {
            const dt = Math.max(0.001, Math.min(0.1, (now - stateRef.current.lastTickTime) / 1000))
            currentSec = Math.min(targetSec, stateRef.current.lastRenderedSec + dt * 2.2)
          } else {
            currentSec = targetSec
          }
          stateRef.current.lastRenderedSec = currentSec
        }
      }

      stateRef.current.lastTickTime = now
      stateRef.current.lastBaseProgress = baseProgress

      // 5. 更新 DOM 渲染
      const ratio = duration > 0 ? Math.min(1, Math.max(0, currentSec / duration)) : 0
      const percent = Math.round(ratio * 100)

      if (fillRef.current) {
        fillRef.current.style.transform = `scaleX(${ratio})`
      }
      if (textRef?.current) {
        if (textFormatRef.current) {
          textRef.current.textContent = textFormatRef.current(currentSec, duration, percent)
        } else {
          textRef.current.textContent = `${Math.floor(currentSec)}s / ${formatNumber(duration, { maxDecimals: 1, roundMode: 'round' })}s`
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

    // 首屏/更新时立即执行一次，确保 DOM 零等待
    tick()

    return () => {
      if (frameId !== undefined) {
        cancelAnimationFrame(frameId)
      }
    }
  }, [
    entry?.entryId,
    entry?.status,
    entry?.durationSeconds,
    entry?.progressSeconds,
    entry?.remainingRepeats,
    lastQueueSyncPerformanceTime,
    fillRef,
    textRef,
    percentRef,
    metaRef,
  ])
}
