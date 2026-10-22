import { memo, useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { Modal } from './Modal'
import { ConfirmDialog } from './ConfirmDialog'
import { useT } from '../i18n'
import {
  applyAxisDrag,
  linggenCultivationMultiplier,
  projectPointerToAxis,
  remainingPoints,
} from '../game/linggenCalculations'
import { evaluateSpiritualRoots } from '../game/linggenEvaluation'
import {
  BOARD_CENTER,
  LINGGEN_AXES,
  LINGGEN_AXIS_CAP,
  axisTier,
  polygonPoints,
  vertexPoint,
  type LinggenElementKey,
  type LinggenPoints,
} from '../game/linggenBoard'

/** 阵盘视觉态。 */
type BoardState = 'unrolled' | 'allocating' | 'finalized'

/** 拖拽会话。 */
interface DragSession {
  active: boolean
  pointerId: number
  axisIndex: number
  startValue: number
}

/** 天门阵盘属性。 */
interface HeavenGateModalProps {
  /** 弹窗是否开启 */
  open: boolean
  /** 开天门抽取的先天分布；未抽取为 undefined */
  rolled?: LinggenPoints
  /** 已确认灵根；未入天门为 undefined */
  confirmed?: LinggenPoints
  /** 是否已入天门 */
  entered: boolean
  /** 逆天改命累计次数 */
  rerollCount: number
  /** 当前玩家底蕴池储量 */
  foundation?: number,
  /** 当前升级所需修为（用于折算改命成本与乘区上限） */
  expToNext: number
  /** 逆天改命默认底蕴成本（25% 档） */
  rerollCostPlain: number
  /** 使用逆命丹后的底蕴成本（5% 档） */
  rerollCostPill: number
  /** 背包内逆命丹数量 */
  reverseFatePillCount: number
  /** 抽取灵根 */
  onRoll: () => void
  /** 提交五行分布 */
  onAllocate: (values: LinggenPoints) => void
  /** 确认入天门（提交最终五行分布） */
  onEnter: (values: LinggenPoints) => void
  /** 逆天改命（可选消耗逆命丹） */
  onReroll: (usePill: boolean) => void
  /** 关闭弹窗 */
  onClose: () => void
}

/**
 * 五行灵根阵盘 (HeavenGateModal)
 *
 * 一次叩开天门的完整仪式：
 * 1. 未抽取：中心丹田显示「开天门」，点击抽取五行灵根；
 * 2. 已抽取未定型：拖拽五轴灵株在点数池内重分配（散功 2 返 1、灌注 1:1），确认后入天门；
 * 3. 已入天门：灵根定型只读，仍可逆天改命（可选消耗逆命丹把底蕴成本从 25% 降到 5%）。
 *
 * 深浅色完全跟随游戏主题（`document.documentElement[data-theme]`），组件自身不提供主题开关。
 */
export const HeavenGateModal = memo(function HeavenGateModal({
  open,
  rolled,
  confirmed,
  entered,
  rerollCount,
  foundation = 0,
  expToNext: _expToNext,
  rerollCostPlain,
  rerollCostPill,
  reverseFatePillCount,
  onRoll,
  onAllocate: _onAllocate,
  onEnter,
  onReroll,
  onClose,
}: HeavenGateModalProps): React.JSX.Element {
  const t = useT()
  const titleId = useId()
  const confirmRef = useRef<HTMLButtonElement>(null)
  const svgRef = useRef<SVGSVGElement>(null)
  const [usePill, setUsePill] = useState(false)
  const [roots, setRoots] = useState<LinggenPoints>(rolled ?? confirmed ?? emptyPoints())
  const [drag, setDrag] = useState<DragSession>({ active: false, pointerId: 0, axisIndex: -1, startValue: 0 })
  const [toast, setToast] = useState<string | null>(null)
  const dragOriginRef = useRef<{ x: number; y: number; moved: boolean } | null>(null)

  // 生成服务端真源的纯值指纹：防止后台高频推流（对象引用变化但数值未变）冲刷玩家正在拖拽的草稿。
  const sourceFingerprint = useMemo(() => {
    if (entered) {
      const c = confirmed ?? emptyPoints()
      return `entered:${c.metal},${c.wood},${c.water},${c.fire},${c.earth}`
    }
    if (rolled) {
      return `rolled:${rolled.metal},${rolled.wood},${rolled.water},${rolled.fire},${rolled.earth}:${rerollCount}`
    }
    return 'unrolled'
  }, [confirmed, entered, rerollCount, rolled])

  const lastSyncedRef = useRef<string | null>(null)

  // 只有服务端核心数值（抽取结果、确认生效或改命次数）真正改变时才同步；
  // 常规状态推流（挂机/战斗等秒级快照更新）绝不覆盖玩家正在调整的本地草稿。
  useEffect(() => {
    if (lastSyncedRef.current === sourceFingerprint) return
    lastSyncedRef.current = sourceFingerprint
    setRoots(entered ? confirmed ?? emptyPoints() : rolled ?? emptyPoints())
  }, [entered, confirmed, rolled, sourceFingerprint])

  const innate = rolled ?? emptyPoints()
  const hasRolled = useMemo(
    () => innate.metal + innate.wood + innate.water + innate.fire + innate.earth > 0,
    [innate],
  )
  const boardState: BoardState = entered ? 'finalized' : hasRolled ? 'allocating' : 'unrolled'
  const remaining = remainingPoints(innate, roots)
  const currentTotal = roots.metal + roots.wood + roots.water + roots.fire + roots.earth
  const immutableTotal = innate.metal + innate.wood + innate.water + innate.fire + innate.earth
  const isAtInnate = LINGGEN_AXES.every(axis => roots[axis.key] === innate[axis.key])
  const hasActiveRoot = roots.metal + roots.wood + roots.water + roots.fire + roots.earth > 0
  const multiplier = linggenCultivationMultiplier(roots, entered)
  const hasPill = reverseFatePillCount > 0
  const evaluation = useMemo(
    () => evaluateSpiritualRoots(roots, hasRolled),
    [hasRolled, roots],
  )

  const showToast = useCallback((message: string) => {
    setToast(message)
    window.setTimeout(() => setToast(current => (current === message ? null : current)), 1200)
  }, [])

  /** 客户端坐标换算为阵盘 400×400 坐标。 */
  const toBoardPoint = useCallback((clientX: number, clientY: number) => {
    const rect = svgRef.current?.getBoundingClientRect()
    if (!rect || rect.width <= 0 || rect.height <= 0) return null
    return {
      x: ((clientX - rect.left) * 400) / rect.width,
      y: ((clientY - rect.top) * 400) / rect.height,
    }
  }, [])

  const handlePointerMove = useCallback((clientX: number, clientY: number) => {
    if (!drag.active) return
    const origin = dragOriginRef.current
    if (origin && !origin.moved) {
      // 位移阈值：小于 3px 视为点击而非拖拽。
      if (Math.hypot(clientX - origin.x, clientY - origin.y) < 3) return
      origin.moved = true
    }
    const point = toBoardPoint(clientX, clientY)
    if (!point) return
    const targetValue = projectPointerToAxis(point, drag.axisIndex)
    const axis = LINGGEN_AXES[drag.axisIndex]
    if (!axis) return
    setRoots(current => {
      const result = applyAxisDrag(innate, current, axis.key, targetValue)
      if (result.hitInsufficientBalance) showToast(t('insight.heavenGate.toast.insufficient'))
      else if (result.hitBound === 'max') showToast(t('insight.heavenGate.toast.max'))
      else if (result.hitBound === 'min') showToast(t('insight.heavenGate.toast.min'))
      return result.roots
    })
  }, [drag, innate, showToast, t, toBoardPoint])

  const endDrag = useCallback(() => {
    dragOriginRef.current = null
    setDrag({ active: false, pointerId: 0, axisIndex: -1, startValue: 0 })
  }, [])

  useEffect(() => {
    if (!drag.active) return undefined
    const onMove = (event: PointerEvent) => handlePointerMove(event.clientX, event.clientY)
    const onUp = () => endDrag()
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
    window.addEventListener('pointercancel', onUp)
    return () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      window.removeEventListener('pointercancel', onUp)
    }
  }, [drag.active, endDrag, handlePointerMove])

  /**
   * 拖拽起始：按下即锁定该轴，但不立刻改写数值。
   * 只有真正移动（位移超过阈值）才进入投影改写，避免单击就把灵根拽离原位。
   */
  const beginDrag = useCallback((axisIndex: number, clientX: number, clientY: number, pointerId: number) => {
    if (boardState !== 'allocating') return
    const axis = LINGGEN_AXES[axisIndex]
    if (!axis) return
    setDrag({ active: true, pointerId, axisIndex, startValue: roots[axis.key] })
    dragOriginRef.current = { x: clientX, y: clientY, moved: false }
  }, [boardState, roots])

  /** 键盘可达：方向键微调单轴。 */
  const nudgeAxis = useCallback((axisKey: LinggenElementKey, delta: number) => {
    if (boardState !== 'allocating') return
    setRoots(current => applyAxisDrag(innate, current, axisKey, current[axisKey] + delta).roots)
  }, [boardState, innate])

  const [confirmAction, setConfirmAction] = useState<'enter' | 'reroll' | null>(null)

  const handleEnterClick = useCallback(() => {
    if (!hasActiveRoot) {
      showToast(t('insight.heavenGate.toast.allZero'))
      return
    }
    if (remaining < 0) {
      showToast(t('insight.heavenGate.toast.insufficient'))
      return
    }
    setConfirmAction('enter')
  }, [hasActiveRoot, remaining, showToast, t])

  const executeEnter = useCallback(() => {
    setConfirmAction(null)
    onEnter(roots)
    onClose()
  }, [onClose, onEnter, roots])

  const handleRerollClick = useCallback(() => {
    setConfirmAction('reroll')
  }, [])

  const executeReroll = useCallback(() => {
    setConfirmAction(null)
    onReroll(usePill && hasPill)
  }, [hasPill, onReroll, usePill])

  const handleReset = useCallback(() => setRoots(innate), [innate])

  // 五轴顶点（当前分布）与先天基准分布
  const vertices = useMemo(
    () => LINGGEN_AXES.map(axis => vertexPoint(axis, roots[axis.key])),
    [roots],
  )
  const innatePoints = useMemo(() => polygonPoints(innate), [innate])
  const currentPoints = useMemo(() => polygonPoints(roots), [roots])
  const innerPoints = useMemo(() => polygonPoints(roots, 4), [roots])

  const boardClass = `hg-panel` +
    ` hg-panel--${boardState}` +
    (remaining > 0 ? ' hg-panel--balance-positive' : '')

  return (
    <>
    <Modal open={open} labelledBy={titleId} onClose={onClose} size="large" initialFocusRef={confirmRef}>
      <section className={boardClass} data-state={boardState}>
        {/* A 判语区 */}
        <header className="hg-header">
          <div className="hg-header__top">
            <h2 id={titleId} className="hg-header__title">{t('insight.heavenGate.title')}</h2>
            <p className="hg-header__subtitle">叩开天门别凡骨 · 五气朝元定道根</p>
          </div>
          <div className="hg-verdict">
            <div className="hg-verdict__title-wrap">
              <span className="hg-verdict__name">{evaluation.name}</span>
              <span className="hg-verdict__seal">{evaluation.seal}</span>
            </div>
            <p className="hg-verdict__desc">{evaluation.desc}</p>
          </div>
        </header>

        {/* B 阵盘区 */}
        <div className="hg-board">
          <div className="hg-board__stage">
            <svg
              ref={svgRef}
              className="hg-board__svg"
              viewBox="0 0 400 400"
              role="application"
              aria-label={t('insight.heavenGate.boardAria')}
            >
              <defs>
                {LINGGEN_AXES.map((axis, index) => {
                  const from = vertexPoint(axis, LINGGEN_AXIS_CAP)
                  const nextAxis = LINGGEN_AXES[(index + 1) % LINGGEN_AXES.length]
                  const to = vertexPoint(nextAxis, LINGGEN_AXIS_CAP)
                  return (
                    <linearGradient
                      key={axis.key}
                      id={`hg-edge-grad-${index}`}
                      gradientUnits="userSpaceOnUse"
                      x1={from.x}
                      y1={from.y}
                      x2={to.x}
                      y2={to.y}
                    >
                      <stop offset="0%" style={{ stopColor: `var(--hg-${axis.key})` }} />
                      <stop offset="100%" style={{ stopColor: `var(--hg-${nextAxis.key})` }} />
                    </linearGradient>
                  )
                })}
              </defs>

              {/* 视觉层 1：底纹 + 周天阵环 + 聚灵微粒 + 仪式灵波 */}
              <g className="hg-board__bg-layer">
                <circle cx={BOARD_CENTER.x} cy={BOARD_CENTER.y} r="168" className="hg-board__bg-disc" />
                {[158, 130, 92, 54].map(r => (
                  <circle key={r} cx={BOARD_CENTER.x} cy={BOARD_CENTER.y} r={r} className="hg-board__tex-ring" />
                ))}
                <g className="hg-board__bagua-ring">
                  <circle
                    cx={BOARD_CENTER.x}
                    cy={BOARD_CENTER.y}
                    r="162"
                    fill="none"
                    stroke="var(--hg-ink-faint)"
                    strokeWidth="1"
                    strokeDasharray="6 12 2 12"
                  />
                  {[[200, 34, 200, 41], [200, 359, 200, 366], [34, 200, 41, 200], [359, 200, 366, 200],
                    [83, 83, 88, 88], [317, 83, 312, 88], [83, 317, 88, 312], [317, 317, 312, 312]]
                    .map(([x1, y1, x2, y2], index) => (
                      <line key={index} x1={x1} y1={y1} x2={x2} y2={y2} className="hg-board__bagua-tick" />
                    ))}
                </g>
                <g className="hg-board__motes">
                  {[[200, 70, 'metal'], [315, 162, 'wood'], [272, 300, 'water'], [128, 300, 'fire'], [85, 162, 'earth']]
                    .map(([cx, cy, key], index) => (
                      <circle
                        key={index}
                        cx={cx as number}
                        cy={cy as number}
                        r="2.2"
                        className={`hg-mote hg-mote--${index + 1}`}
                        style={{ fill: `var(--hg-${key})` }}
                      />
                    ))}
                </g>
                <circle cx={BOARD_CENTER.x} cy={BOARD_CENTER.y} r="28" className="hg-board__ritual-wave" />
              </g>

              {/* 视觉层 2：刻度环 */}
              <g className="hg-board__rings">
                {[37.5, 75, 112.5, 150].map((r, index) => (
                  <circle
                    key={r}
                    cx={BOARD_CENTER.x}
                    cy={BOARD_CENTER.y}
                    r={r}
                    className={`hg-board__ring hg-board__ring--${[25, 50, 75, 100][index]}`}
                  />
                ))}
                {[[205, 165, '25'], [205, 128, '50'], [205, 90, '75'], [205, 54, '100']].map(([x, y, label], index) => (
                  <text
                    key={label}
                    x={x as number}
                    y={y as number}
                    className={`hg-board__scale-text${index === 3 ? ' hg-board__scale-text--100' : ''}`}
                  >
                    {label as string}
                  </text>
                ))}
              </g>

              {/* 视觉层 3&4：五条主轴线与灵光流 */}
              <g className="hg-board__axes">
                {LINGGEN_AXES.map((axis, index) => {
                  const tip = vertexPoint(axis, LINGGEN_AXIS_CAP)
                  const edge = vertexPoint(axis, roots[axis.key])
                  return (
                    <g
                      key={axis.key}
                      className={`hg-axis hg-axis--${axis.key}`}
                      data-axis={axis.key}
                      data-index={index}
                      data-zero={roots[axis.key] === 0 ? 'true' : 'false'}
                      data-active={drag.active && drag.axisIndex === index ? 'true' : 'false'}
                    >
                      <line
                        x1={BOARD_CENTER.x}
                        y1={BOARD_CENTER.y}
                        x2={tip.x}
                        y2={tip.y}
                        className="hg-axis__hit"
                        onPointerDown={event => beginDrag(index, event.clientX, event.clientY, event.pointerId)}
                      />
                      <line x1={BOARD_CENTER.x} y1={BOARD_CENTER.y} x2={tip.x} y2={tip.y} className="hg-axis__line" />
                      <line
                        x1={edge.x}
                        y1={edge.y}
                        x2={tip.x}
                        y2={tip.y}
                        className="hg-axis__locked-ext"
                      />
                      <line x1={BOARD_CENTER.x} y1={BOARD_CENTER.y} x2={tip.x} y2={tip.y} className="hg-axis__aura" />
                    </g>
                  )
                })}
              </g>

              {/* 视觉层 5：多边形 + 先天基准 + 五条灵脉边 */}
              <g className="hg-board__polygon-group">
                <polygon className="hg-polygon--innate" points={innatePoints} />
                <polygon className="hg-polygon hg-polygon--inner" points={innerPoints} />
                <polygon className="hg-polygon" points={currentPoints} />
                <g className="hg-board__edges">
                  {LINGGEN_AXES.map((axis, index) => {
                    const nextAxis = LINGGEN_AXES[(index + 1) % LINGGEN_AXES.length]
                    const a = vertices[index]
                    const b = vertices[(index + 1) % LINGGEN_AXES.length]
                    const valA = roots[axis.key]
                    const valB = roots[nextAxis.key]
                    const maxEnd = Math.max(valA, valB)
                    const boost = maxEnd >= 90 ? 'heaven' : maxEnd >= 60 ? 'earth' : 'none'
                    return (
                      <g
                        key={axis.key}
                        className="hg-edge"
                        data-boost={boost}
                        data-active={drag.active && (drag.axisIndex === index || drag.axisIndex === (index + 1) % LINGGEN_AXES.length) ? 'true' : 'false'}
                      >
                        <line x1={a.x} y1={a.y} x2={b.x} y2={b.y} className="hg-edge__base" stroke={`url(#hg-edge-grad-${index})`} style={{ strokeWidth: `${(1.4 + ((valA + valB) / 2 / 100) * 2.2).toFixed(2)}px`, opacity: valA === 0 && valB === 0 ? 0.2 : 0.88 }} />
                        <line x1={a.x} y1={a.y} x2={b.x} y2={b.y} className="hg-edge__flow-a" style={{ stroke: `var(--hg-${axis.key})`, strokeWidth: `${(1.2 + (valA / 100) * 2.6).toFixed(2)}px`, opacity: valA > 0 ? (0.2 + (valA / 100) * 0.65).toFixed(2) : 0 }} />
                        <line x1={a.x} y1={a.y} x2={b.x} y2={b.y} className="hg-edge__flow-b" style={{ stroke: `var(--hg-${nextAxis.key})`, strokeWidth: `${(1.2 + (valB / 100) * 2.6).toFixed(2)}px`, opacity: valB > 0 ? (0.2 + (valB / 100) * 0.65).toFixed(2) : 0 }} />
                      </g>
                    )
                  })}
                </g>
              </g>



              {/* 视觉层 7：五轴灵根仙株滑块 */}
              <g className="hg-board__handles">
                {LINGGEN_AXES.map((axis, index) => {
                  const point = vertices[index]
                  const value = roots[axis.key]
                  const tier = axisTier(value)
                  return (
                    <g
                      key={axis.key}
                      className={`hg-handle hg-handle--${axis.key}`}
                      data-axis={axis.key}
                      transform={`translate(${point.x}, ${point.y})`}
                      role="slider"
                      tabIndex={boardState === 'allocating' ? 0 : -1}
                      aria-label={t(`insight.heavenGate.element.${axis.key}`)}
                      aria-valuemin={0}
                      aria-valuemax={LINGGEN_AXIS_CAP}
                      aria-valuenow={value}
                      data-zero={value === 0 ? 'true' : 'false'}
                      data-max={value === LINGGEN_AXIS_CAP ? 'true' : 'false'}
                      data-tier={tier}
                      data-dragging={drag.active && drag.axisIndex === index ? 'true' : 'false'}
                      onPointerDown={event => beginDrag(index, event.clientX, event.clientY, event.pointerId)}
                      onKeyDown={event => {
                        if (event.key === 'ArrowUp' || event.key === 'ArrowRight') {
                          event.preventDefault()
                          nudgeAxis(axis.key, 1)
                        } else if (event.key === 'ArrowDown' || event.key === 'ArrowLeft') {
                          event.preventDefault()
                          nudgeAxis(axis.key, -1)
                        }
                      }}
                    >
                      <circle cx="0" cy="0" r="24" className="hg-handle__hit" />
                      <g className="hg-handle__scale">
                        <circle cx="0" cy="0" r="15" className="hg-handle__tier-ring" />
                        <g transform={`rotate(${axis.handleRotation})`}>
                          <path d="M 0,8 L 0,19 M -3,7 C -7,12 -2,15 -5,19 M 3,7 C 7,12 2,15 5,19" className="hg-handle__roots" />
                          <path d="M -4,2 C -12,-1 -13,-8 -6,-6 Z M 4,2 C 12,-1 13,-8 6,-6 Z" className="hg-handle__leaves" />
                          <path d="M 0,-13 L 8,-2 L 5,7 L 0,10 L -5,7 L -8,-2 Z" className="hg-handle__diamond" />
                          <path d="M 0,-13 L 8,-2 L 0,0 L -8,-2 Z" className="hg-handle__outward-cap" />
                          <path d="M 0,-9 L 0,6 M -4,-2 L 0,2 L 4,-2" className="hg-handle__vein" />
                        </g>
                        <circle cx="0" cy="-1" r="2.2" className="hg-handle__pip" />
                        <g className="hg-handle__badge">
                          <rect x="-17" y="-9" width="34" height="17" rx="3" className="hg-handle__badge-bg" />
                          <text x="0" y="0.5" textAnchor="middle" dominantBaseline="central" className="hg-handle__badge-text">
                            {tier === 'heaven' ? '天灵' : '地灵'}
                          </text>
                        </g>
                      </g>
                    </g>
                  )
                })}
              </g>

              {/* 视觉层 8：顶点标签（固定贴在阵盘外圈，纵向三行依次为五行字、当前值、先天值） */}
              <g className="hg-board__labels">
                {LINGGEN_AXES.map(axis => {
                  const anchor = vertexPoint(axis, LINGGEN_AXIS_CAP)
                  const dx = anchor.x - BOARD_CENTER.x
                  const dy = anchor.y - BOARD_CENTER.y
                  // 顶部金轴需越过刻度文字（100 标签在 y=54），其余轴取 1.12 倍外圈即可。
                  const outward = dy < -0.5 ? 1.19 : 1.13
                  const labelX = BOARD_CENTER.x + dx * outward
                  const labelY = BOARD_CENTER.y + dy * outward
                  const isRight = dx > 0.01
                  const isLeft = dx < -0.01
                  const textAnchor = isRight ? 'start' : isLeft ? 'end' : 'middle'
                  const value = hasRolled ? String(roots[axis.key]) : t('insight.heavenGate.undetermined')
                  return (
                    <g key={axis.key} className={`hg-label hg-label--${axis.key}`} data-axis={axis.key}>
                      <text x={labelX} y={labelY - 14} textAnchor={textAnchor} className="hg-label__name">
                        {axis.label}
                      </text>
                      <text x={labelX} y={labelY + 4} textAnchor={textAnchor} className="hg-label__val">
                        {value}
                      </text>
                      {hasRolled && (
                        <text x={labelX} y={labelY + 18} textAnchor={textAnchor} className="hg-label__base">
                          {`(初${innate[axis.key]})`}
                        </text>
                      )}
                    </g>
                  )
                })}
              </g>
              {/* 视觉层 8.5：中心内环大印按钮（开天门 / 入天门操作核心） */}
              <g
                className={`hg-dantian ${boardState === 'finalized' ? 'hg-dantian--disabled' : ''}`}
                role={boardState === 'finalized' ? 'img' : 'button'}
                tabIndex={boardState === 'finalized' ? -1 : 0}
                aria-label={boardState === 'unrolled' ? '开天门' : boardState === 'allocating' ? '叩入天门' : '道根已定'}
                onClick={() => {
                  if (boardState === 'unrolled') onRoll()
                  else if (boardState === 'allocating') handleEnterClick()
                }}
                onKeyDown={event => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault()
                    if (boardState === 'unrolled') onRoll()
                    else if (boardState === 'allocating') handleEnterClick()
                  }
                }}
              >
                <circle cx={BOARD_CENTER.x} cy={BOARD_CENTER.y} r="26" className="hg-dantian__halo" />
                <circle cx={BOARD_CENTER.x} cy={BOARD_CENTER.y} r="20" className="hg-dantian__core" />
                <text x={BOARD_CENTER.x} y={BOARD_CENTER.y + 1} className="hg-dantian__text">
                  {boardState === 'unrolled' ? '开天门' : '灵'}
                </text>
              </g>
            </svg>

            {toast && (
              <div className="hg-board__toast" role="status" aria-live="polite">{toast}</div>
            )}
          </div>

          {boardState === 'allocating' && !hasActiveRoot && (
            <div className="hg-board__single-notice">{t('insight.heavenGate.toast.allZero')}</div>
          )}
        </div>

        {/* C 底部状态区（精简为未分配、总、后续破镜难度三栏） */}
        <div className="hg-stats">
          <div className="hg-stat hg-stat--remaining">
            <div className="hg-stat__head">
              <span className="hg-stat__label">未分配</span>
            </div>
            <div className="hg-stat__main">
              <strong className="hg-stat__value">
                {boardState === 'allocating' && hasRolled ? formatPoints(remaining) : '--'}
              </strong>
            </div>
          </div>

          <div className="hg-stat hg-stat--total">
            <div className="hg-stat__head">
              <span className="hg-stat__label">总</span>
            </div>
            <div className="hg-stat__main">
              <strong className="hg-stat__value">
                {hasRolled ? `${currentTotal} (初${immutableTotal})` : '--'}
              </strong>
            </div>
          </div>

          <div className="hg-stat hg-stat--cultivation">
            <div className="hg-stat__head">
              <span className="hg-stat__label">后续破镜难度</span>
            </div>
            <div className="hg-stat__main">
              <strong className="hg-stat__value">
                {`×${multiplier.toFixed(1)}`}
              </strong>
            </div>
          </div>
        </div>

        {/* D 底部操作区（居中排布：重置与逆天改命；入天门与开天门由内环中心印章按钮承担） */}
        <footer className="hg-actions">
          {hasRolled && hasPill && (
            <label className="hg-reroll-toggle">
              <input
                type="checkbox"
                checked={usePill}
                onChange={event => setUsePill(event.currentTarget.checked)}
              />
              <span>{t('insight.heavenGate.usePillLabel')}</span>
            </label>
          )}

          <div className="hg-actions__group">
            {boardState === 'allocating' && (
              <>
                <button
                  type="button"
                  className="hg-btn hg-btn--ghost"
                  disabled={isAtInnate}
                  onClick={handleReset}
                >
                  重置
                </button>
                <button
                  type="button"
                  className="hg-btn hg-btn--ochre"
                  onClick={handleRerollClick}
                >
                  {usePill && hasPill
                    ? t('insight.heavenGate.rerollWithPillBtn')
                    : t('insight.heavenGate.rerollBtn')}
                </button>
                <button
                  ref={confirmRef}
                  type="button"
                  className="hg-btn hg-btn--primary"
                  disabled={!hasActiveRoot || remaining < 0}
                  onClick={handleEnterClick}
                >
                  {t('insight.heavenGate.enterBtn')}
                </button>
              </>
            )}

            {boardState === 'finalized' && (
              <>
                <button
                  type="button"
                  className="hg-btn hg-btn--ochre"
                  onClick={handleRerollClick}
                >
                  {usePill && hasPill
                    ? t('insight.heavenGate.rerollWithPillBtn')
                    : t('insight.heavenGate.rerollBtn')}
                </button>
                <button type="button" className="hg-btn hg-btn--ghost" onClick={onClose}>
                  {t('insight.heavenGate.closeBtn')}
                </button>
              </>
            )}

            {boardState === 'unrolled' && (
              <button type="button" className="hg-btn hg-btn--ghost" onClick={onClose}>
                {t('insight.heavenGate.closeBtn')}
              </button>
            )}
          </div>
        </footer>

        {hasRolled && (
          <p className="hg-cost-line">
            {usePill && hasPill
              ? t('insight.heavenGate.costPill', { cost: rerollCostPill })
              : t('insight.heavenGate.costPlain', { cost: rerollCostPlain })}
          </p>
        )}
      </section>
    </Modal>
    {open && confirmAction === 'enter' && (
      <ConfirmDialog
        open={true}
        title={t('insight.heavenGate.enterConfirmTitle')}
        description={t('insight.heavenGate.enterConfirmBody')}
        confirmLabel={t('insight.heavenGate.confirmEnter')}
        onConfirm={executeEnter}
        onClose={() => setConfirmAction(null)}
      />
    )}
    {open && confirmAction === 'reroll' && (
      <ConfirmDialog
        open={true}
        title={t('insight.heavenGate.rerollConfirmTitle')}
        description={
          foundation < (usePill && hasPill ? rerollCostPill : rerollCostPlain)
            ? t('insight.heavenGate.rerollConfirmInsufficient', {
                foundation: Math.floor(foundation).toLocaleString(),
                cost: (usePill && hasPill ? rerollCostPill : rerollCostPlain).toLocaleString(),
              })
            : usePill && hasPill
            ? t('insight.heavenGate.rerollConfirmWithPillCostWithFoundation', {
                foundation: Math.floor(foundation).toLocaleString(),
                cost: rerollCostPill.toLocaleString(),
              })
            : t('insight.heavenGate.rerollConfirmCostWithFoundation', {
                foundation: Math.floor(foundation).toLocaleString(),
                cost: rerollCostPlain.toLocaleString(),
              })
        }
        confirmLabel={t('insight.heavenGate.rerollBtn')}
        danger={true}
        onConfirm={executeReroll}
        onClose={() => setConfirmAction(null)}
      />
    )}
  </>
  )
})

/** 空灵根。 */
function emptyPoints(): LinggenPoints {
  return { metal: 0, wood: 0, water: 0, fire: 0, earth: 0 }
}

/** 半点点数直显，整数不带小数。 */
function formatPoints(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1)
}

/** 判语标题：按最高灵根数量与品阶给出定性评级。 */
