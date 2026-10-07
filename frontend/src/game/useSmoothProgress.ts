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
 * 进度位置模型：sec(t) = loop ? (s0 + (t-t0)/1000*rate) % dur : min(dur, s0 + (t-t0)/1000*rate)。
 * stopped=true 时恒为 s0（静态帧）。供低频文本刷新与动画重建时读取当前显示位置。
 */
interface ProgressModel {
  t0: number
  s0: number
  rate: number
  dur: number
  loop: boolean
  stopped: boolean
}

/**
 * 统一的平滑行动进度驱动 Hook
 *
 * 【设计机制】（能耗优化版：合成器动画 + 低频文本，不再持有 rAF 循环）：
 * 1. 进度条填充由 WAAPI `fill.animate(scaleX)` 驱动——动画在合成器时间轴上播放，
 *    主线程仅在服务端同步到达时重建一次动画，零逐帧 JS 唤醒；
 * 2. 单段线性动画的时长被压缩为「当前显示位置 → 服务端预期完成时刻」：
 *    本地预测领先服务端时自动减速对齐（单调不后退，绝不往回抽搐），
 *    落后时自动加速追赶（速率封顶 2.2x，替代原逐帧 dt 滤波的同等语义）；
 * 3. 换轮与行动切换检测沿用原口径：新 entryId / remainingRepeats 变化 /
 *    baseProgress 显著回落即重置到服务端权威进度；
 * 4. 无限循环（remainingRepeats < 0）：首段跑「当前位置→1」的追赶段，
 *    onfinish 后切换为 iterations:Infinity 的整圈动画，帧间无缝衔接；
 * 5. 文本/百分比/元数据由 100ms interval 驱动并做内容比对后才写 DOM——
 *    与 0.1s 显示精度匹配，把重排频率从 60Hz 压到 ≤10Hz；
 * 6. 暂停/非 running 时只落静态帧并写一次文本，不产生任何周期任务；
 * 7. prefers-reduced-motion 下跳过 WAAPI 平滑动画，直接落静态帧。
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
  // 队列手动暂停时冻结一切进度插值（计时/旅行/远征相位共用此 Hook，服务端权威暂停期间本地不再推进）。
  const paused = useGameStore(state => state.queue.paused)
  // 调用方通常在渲染中创建格式化闭包；用 ref 读取最新回调，避免每次父组件更新都重启动画。
  const textFormatRef = useRef(textFormat)
  const metaFormatRef = useRef(metaFormat)
  textFormatRef.current = textFormat
  metaFormatRef.current = metaFormat

  // 跨重建状态：条目/轮次识别 + 当前显示位置模型
  const stateRef = useRef<{
    entryId: string
    remainingRepeats: number
    lastBaseProgress: number
    model: ProgressModel
  }>({
    entryId: '',
    remainingRepeats: 0,
    lastBaseProgress: 0,
    model: { t0: 0, s0: 0, rate: 1, dur: 1, loop: false, stopped: true },
  })

  useEffect(() => {
    if (!entry) return
    const duration = Math.max(1, entry.durationSeconds)
    const baseProgress = entry.progressSeconds
    const isInfinite = entry.remainingRepeats < 0
    const isRunning = entry.status === 'running' && !paused
    const now = performance.now()

    const modelSec = (t: number): number => {
      const m = stateRef.current.model
      if (m.stopped) return m.s0
      const raw = m.s0 + ((t - m.t0) / 1000) * m.rate
      return m.loop
        ? ((raw % m.dur) + m.dur) % m.dur
        : Math.min(m.dur, raw)
    }

    // 1. 当前显示位置：从上一次动画模型推出；新条目/新轮次则直接取服务端权威值
    let displayed = modelSec(now)
    const isNewEntry = entry.entryId !== stateRef.current.entryId
    const isNewRound = !isNewEntry && (
      entry.remainingRepeats !== stateRef.current.remainingRepeats ||
      baseProgress < stateRef.current.lastBaseProgress - 0.5
    )
    if (isNewEntry || isNewRound) {
      displayed = baseProgress
    }
    stateRef.current.entryId = entry.entryId
    stateRef.current.remainingRepeats = entry.remainingRepeats
    stateRef.current.lastBaseProgress = baseProgress

    // 2. 服务端权威目标秒数
    const elapsed = isRunning ? Math.max(0, (now - lastQueueSyncPerformanceTime) / 1000) : 0
    let targetSec: number
    if (!isRunning) {
      targetSec = baseProgress
    } else if (isInfinite) {
      targetSec = (((baseProgress + elapsed) % duration) + duration) % duration
    } else {
      targetSec = Math.min(duration, Math.max(0, baseProgress + elapsed))
    }

    // 3. 动画起点：无限模式跨边界回绕归零；目标滞后于显示位置时保持单调不后退；
    //    正向大步（>0.3s）从当前显示位置起追（时长压缩承载加速），小步直接吸附目标值。
    let fromSec: number
    if (!isRunning) {
      fromSec = targetSec
    } else {
      if (isInfinite && displayed > duration * 0.8 && targetSec < duration * 0.2) {
        displayed = 0
      }
      if (targetSec < displayed) {
        fromSec = Math.min(duration, displayed)
      } else {
        fromSec = targetSec - displayed > 0.3 ? displayed : targetSec
      }
    }

    // 4. 段时长：默认恰好命中服务端预期完成时刻；追赶速率封顶 2.2x（此时可能晚于预期，下一次同步自愈）。
    const segMs = isRunning
      ? Math.max(30, (duration - targetSec) * 1000, ((duration - fromSec) / 2.2) * 1000)
      : 0
    const segRate = isRunning && segMs > 0 ? (duration - fromSec) / (segMs / 1000) : 0

    const ratioOf = (sec: number) => (duration > 0 ? Math.min(1, Math.max(0, sec / duration)) : 0)

    // 5. 文本渲染：内容比对后写 DOM，避免无效 textContent 触发逐帧重排
    const lastWritten = { text: '', percent: '', meta: '' }
    const renderTexts = (sec: number) => {
      const ratio = ratioOf(sec)
      const percent = Math.round(ratio * 100)
      if (textRef?.current) {
        const s = textFormatRef.current
          ? textFormatRef.current(sec, duration, percent)
          : `${Math.floor(sec)}s / ${formatNumber(duration, { maxDecimals: 1, roundMode: 'round' })}s`
        if (s !== lastWritten.text) {
          lastWritten.text = s
          textRef.current.textContent = s
        }
      }
      if (percentRef?.current) {
        const s = `${percent}%`
        if (s !== lastWritten.percent) {
          lastWritten.percent = s
          percentRef.current.textContent = s
        }
      }
      if (metaRef?.current && metaFormatRef.current) {
        const s = metaFormatRef.current(sec, duration, ratio, percent)
        if (s !== lastWritten.meta) {
          lastWritten.meta = s
          metaRef.current.textContent = s
        }
      }
    }

    const fill = fillRef.current

    // 暂停/非 running：静态帧 + 单次文本，无周期任务
    if (!isRunning) {
      stateRef.current.model = { t0: now, s0: fromSec, rate: 0, dur: duration, loop: false, stopped: true }
      if (fill) fill.style.transform = `scaleX(${ratioOf(fromSec)})`
      renderTexts(fromSec)
      return undefined
    }

    // 6. 启动合成器动画；无限循环在首段收尾后切换为整圈无限动画
    let anim: Animation | undefined
    let loopAnim: Animation | undefined
    stateRef.current.model = { t0: now, s0: fromSec, rate: segRate, dur: duration, loop: false, stopped: false }
    if (fill) {
      if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
        fill.style.transform = `scaleX(${ratioOf(fromSec)})`
      } else {
        anim = fill.animate(
          [{ transform: `scaleX(${ratioOf(fromSec)})` }, { transform: 'scaleX(1)' }],
          { duration: segMs, easing: 'linear', fill: 'forwards' },
        )
        if (isInfinite) {
          anim.onfinish = () => {
            loopAnim = fill.animate(
              [{ transform: 'scaleX(0)' }, { transform: 'scaleX(1)' }],
              { duration: duration * 1000, easing: 'linear', iterations: Infinity },
            )
            stateRef.current.model = { t0: performance.now(), s0: 0, rate: 1, dur: duration, loop: true, stopped: false }
          }
        }
      }
    }

    // 7. 低频文本驱动：0.1s 显示精度对应 100ms 唤醒，未变化不落 DOM
    renderTexts(modelSec(performance.now()))
    const interval = window.setInterval(() => {
      renderTexts(modelSec(performance.now()))
    }, 100)

    return () => {
      window.clearInterval(interval)
      anim?.cancel()
      loopAnim?.cancel()
    }
  }, [
    entry?.entryId,
    entry?.status,
    entry?.durationSeconds,
    entry?.progressSeconds,
    entry?.remainingRepeats,
    lastQueueSyncPerformanceTime,
    paused,
    fillRef,
    textRef,
    percentRef,
    metaRef,
  ])
}
