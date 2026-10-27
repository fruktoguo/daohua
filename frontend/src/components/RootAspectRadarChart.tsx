import React, { memo, useId } from 'react'
import { useT } from '../i18n'

export interface RootAspectRadarChartProps {
  /** 六维经验分配乘率表（key 为 stamina/meridians/attack/bone/agility/consciousness，默认 1.0） */
  multipliers?: Partial<Record<string, number>>
  /** 当前等级效率乘率（例如 1.0 或 1.02） */
  efficiency?: number
  /** 自定义样式类名 */
  className?: string
}

interface AspectConfig {
  key: string
  angle: number // radians
  lx: number
  ly: number
  anchor: 'middle' | 'start' | 'end'
}

const CX = 115
const CY = 84
const BASE_R = 30 // 100% 经验基准环半径
const MAX_R = 54  // 极化上限环半径 (400%)

// 六维顺序：顶部体魄，顺时针经脉、力道、根骨、身法、神识
const ANGLES = [-90, -30, 30, 90, 150, 210].map((deg) => (deg * Math.PI) / 180)

const ASPECTS: readonly AspectConfig[] = [
  // 六维标签复用 insight.root.* 既有词条（attack 对应根基页的「力道」strength）
  { key: 'stamina', angle: ANGLES[0], lx: CX, ly: 14, anchor: 'middle' },
  { key: 'meridians', angle: ANGLES[1], lx: 174, ly: 48, anchor: 'start' },
  { key: 'attack', angle: ANGLES[2], lx: 174, ly: 118, anchor: 'start' },
  { key: 'bone', angle: ANGLES[3], lx: CX, ly: 154, anchor: 'middle' },
  { key: 'agility', angle: ANGLES[4], lx: 56, ly: 118, anchor: 'end' },
  { key: 'consciousness', angle: ANGLES[5], lx: 56, ly: 48, anchor: 'end' },
]

/** 根据百分比计算顶点半径（0%~100% 对应 4~30px，100%~400% 对应 30~54px） */
function getRadius(pct: number): number {
  if (pct <= 0) return 4
  if (pct <= 100) return (pct / 100) * BASE_R
  const overflow = Math.min(pct, 400) - 100
  return BASE_R + (overflow / 300) * (MAX_R - BASE_R)
}

function polygonPoints(radiusMap: (idx: number) => number): string {
  return ANGLES.map((angle, i) => {
    const r = radiusMap(i)
    const x = CX + r * Math.cos(angle)
    const y = CY + r * Math.sin(angle)
    return `${x.toFixed(1)},${y.toFixed(1)}`
  }).join(' ')
}

const BASE_HEX_POINTS = polygonPoints(() => BASE_R)
const MAX_HEX_POINTS = polygonPoints(() => MAX_R)
const HALF_HEX_POINTS = polygonPoints(() => BASE_R * 0.5)

/**
 * 六维根基经验获取雷达图组件 (RootAspectRadarChart)
 *
 * 紧凑展示内功心法的六维（体魄/经脉/力道/根骨/身法/神识）资质倾斜与经验分配：
 * - 虚线金环代表 100% 标准基准；
 * - 青蓝渐变多边形实时呈现心法在各维度的特化与代价；
 * - 顶点百分比随心法等级实时动态放大，特化高亮，代价暗化。
 */
export const RootAspectRadarChart = memo(function RootAspectRadarChart({
  multipliers,
  efficiency = 1.0,
  className = '',
}: RootAspectRadarChartProps): React.JSX.Element {
  const gradientId = useId()
  const filterId = useId()
  const t = useT()

  const eff = Math.max(0, efficiency)

  // 计算六维顶点百分比与半径
  const stats = ASPECTS.map((aspect) => {
    const baseMult = multipliers?.[aspect.key] ?? 1.0
    const pct = baseMult * eff * 100
    const r = getRadius(pct)
    const vx = CX + r * Math.cos(aspect.angle)
    const vy = CY + r * Math.sin(aspect.angle)

    let valText = '0%'
    if (pct > 0) {
      const rounded = Math.round(pct * 10) / 10
      valText = `${Number.isInteger(rounded) ? rounded : rounded.toFixed(1)}%`
    }

    // 颜色交由主题变量决定：深色沿用灵光青系，浅色切换为同色相深色，避免浅底浅字。
    let valColor = 'var(--text, #e9e3d2)'
    if (pct >= 200) {
      valColor = 'var(--radar-value-super)' // 超常特化：灵光青
    } else if (pct > 100) {
      valColor = 'var(--radar-value-growth)' // 强化成长：浅天蓝
    } else if (pct === 0) {
      valColor = 'var(--radar-value-decay)' // 绝弃代价：危红
    } else if (pct < 100) {
      valColor = 'var(--text-dim, rgba(233, 227, 210, 0.48))' // 削减维度：暗淡
    }

    return {
      ...aspect,
      pct,
      r,
      vx,
      vy,
      valText,
      valColor,
    }
  })

  const dataPoints = stats.map((s) => `${s.vx.toFixed(1)},${s.vy.toFixed(1)}`).join(' ')

  return (
    <div className={`root-aspect-radar-shell ${className}`.trim()}>
      <svg
        aria-label={t('game.abilityAside.radarAria')}
        className="root-aspect-radar-svg"
        viewBox="0 0 230 172"
      >
        <defs>
          <radialGradient id={gradientId} cx="50%" cy="50%" r="50%">
            <stop offset="0%" style={{ stopColor: 'var(--radar-shape)' }} stopOpacity="0.38" />
            <stop offset="85%" style={{ stopColor: 'var(--radar-shape-deep)' }} stopOpacity="0.18" />
            <stop offset="100%" style={{ stopColor: 'var(--radar-shape-deep)' }} stopOpacity="0.06" />
          </radialGradient>
          <filter id={filterId} x="-20%" y="-20%" width="140%" height="140%">
            <feDropShadow dx="0" dy="0" style={{ floodColor: 'var(--radar-shape)' }} floodOpacity="0.45" stdDeviation="1.5" />
          </filter>
        </defs>

        {/* 背景蛛网层：50% 环、100% 基准环、400% 上限环 */}
        <polygon
          points={HALF_HEX_POINTS}
          style={{ fill: 'none', stroke: 'var(--radar-web-inner)' }}
          strokeWidth="0.8"
        />
        <polygon
          points={BASE_HEX_POINTS}
          style={{ fill: 'var(--radar-base-fill)', stroke: 'var(--radar-base-stroke)' }}
          strokeDasharray="2.5 2.5"
          strokeWidth="1"
        />
        <polygon
          points={MAX_HEX_POINTS}
          style={{ fill: 'none', stroke: 'var(--radar-web)' }}
          strokeWidth="0.8"
        />

        {/* 6 条轴线 */}
        {ANGLES.map((angle, idx) => {
          const ex = CX + MAX_R * Math.cos(angle)
          const ey = CY + MAX_R * Math.sin(angle)
          return (
            <line
              key={idx}
              style={{ stroke: 'var(--radar-web)' }}
              strokeWidth="0.8"
              x1={CX}
              x2={ex}
              y1={CY}
              y2={ey}
            />
          )
        })}

        {/* 100% 基准微标记 */}
        <text
          style={{ fill: 'var(--radar-label)' }}
          fontSize="8"
          letterSpacing="0.5"
          textAnchor="middle"
          x={CX}
          y={CY - BASE_R + 8}
        >
          100%
        </text>

        {/* 心法六维实装覆盖区 */}
        <polygon
          filter={`url(#${filterId})`}
          points={dataPoints}
          style={{
            fill: `url(#${gradientId})`,
            stroke: 'var(--radar-shape)',
            strokeWidth: 1.6,
            strokeLinejoin: 'round',
          }}
        />

        {/* 六维数据顶点小圆圈 */}
        {stats.map((s, idx) => (
          <circle
            cx={s.vx}
            cy={s.vy}
            key={idx}
            r="2.2"
            style={{ fill: 'var(--radar-vertex-fill)', stroke: 'var(--radar-vertex-stroke)' }}
            strokeWidth="1"
          />
        ))}

        {/* 环周维度标签与百分比数值 */}
        {stats.map((s, idx) => (
          <text
            key={idx}
            style={{
              fontSize: '11px',
              fontFamily: 'inherit',
              userSelect: 'none',
            }}
            textAnchor={s.anchor}
            x={s.lx}
            y={s.ly}
          >
            <tspan
              style={{ fill: 'var(--text-dim, rgba(233, 227, 210, 0.72))' }}
              x={s.lx}
            >
              {t(`insight.root.${s.key === 'attack' ? 'strength' : s.key}.name`)}
            </tspan>
            <tspan
              dy="12"
              fontWeight={s.pct > 100 ? '600' : 'normal'}
              style={{ fill: s.valColor }}
              x={s.lx}
            >
              {s.valText}
            </tspan>
          </text>
        ))}
      </svg>
    </div>
  )
})
