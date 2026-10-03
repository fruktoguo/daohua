import React from 'react'
import type { RealmInfo } from '../game/combatConstants'
import { FloatingInfoCard, FloatingPopover } from './FloatingPopover'
import { formatNumber } from '../game/format'
import { UnitNumber } from './UnitNumber'
import { useT } from '../i18n'

/** 境界徽章组件 Props */
interface RealmBadgeProps {
  /** 境界计算数据结构 (大境界、境界阶段、小境界、阶段颜色与总等级) */
  realmInfo: RealmInfo
  /** 外部样式类名 */
  className?: string
}

/**
 * 修仙境界三段式复合牌匾徽章组件 (RealmBadge)
 *
 * 视觉构成与交互：
 * 1. 大境界牌匾框 (major-box)：显示“练气”、“筑基”、“金丹”等大境界名；
 * 2. 境界阶段棱形徽章 (diamond-badge)：居中悬浮“武/玄/天/初/中/后/极”境界阶段单字；
 * 3. 小境界灵气胶囊 (minor-box)：展示当前“前期/中期/后期/圆满/九重”小境界；
 * 4. 悬浮触发 FloatingPopover，展示大境界说明、当前总等级 Lv 与小境界突破进度百分比。
 */
export function RealmBadge({ realmInfo, className = '' }: RealmBadgeProps): React.JSX.Element {
  const t = useT()
  const lv = Math.floor(realmInfo.totalLevel)
  return (
    <FloatingPopover
      placement="bottom"
      content={
        <FloatingInfoCard
          title={`${realmInfo.stageName} · ${realmInfo.minorName}`}
          badge={t('insight.badge.levelBadge', { level: formatNumber(lv) })}
          badgeTone="gold"
          subtitle={t('insight.badge.phase', { phase: realmInfo.phaseName })}
          description={t('insight.badge.description')}
          stats={[
            { label: t('insight.badge.stage'), value: realmInfo.stageName, tone: 'gold' },
            { label: t('insight.badge.minor'), value: realmInfo.minorName, tone: 'qi' },
            { label: t('insight.badge.totalLevel'), value: <>Lv. <UnitNumber disableTooltip value={lv} /></>, tone: 'gold' },
            { label: t('insight.badge.layerProgress'), value: `${realmInfo.layerProgress.toFixed(1)}%`, tone: 'jade' },
          ]}
        />
      }
    >
      <div
        className={`insight-realm-master-frame ${className}`}
        style={{
          '--realm-accent': realmInfo.stageColor,
          borderColor: 'color-mix(in srgb, var(--realm-accent) 40%, transparent)',
          boxShadow: '0 0 14px color-mix(in srgb, var(--realm-accent) 12.5%, transparent), inset 0 0 6px rgba(0,0,0,0.5)',
        } as React.CSSProperties}
      >
      {/* 1. 大境界牌匾框 */}
      <div
        className="insight-realm-major-box"
        style={{
          borderLeftColor: realmInfo.stageColor,
          borderRightColor: realmInfo.stageColor,
          backgroundColor: 'color-mix(in srgb, var(--realm-accent) 9.4%, transparent)',
        }}
      >
        <span className="insight-realm-major-text">{realmInfo.stageName}</span>
      </div>

      {/* 2. 境界阶段棱形徽章框 (武/玄/天/初/中/后/极) */}
      <div className="insight-realm-diamond-container">
        <div
          className="insight-realm-diamond-badge"
          style={{
            borderColor: realmInfo.stageColor,
            backgroundColor: 'color-mix(in srgb, var(--realm-accent) 15.7%, transparent)',
            boxShadow: '0 0 8px color-mix(in srgb, var(--realm-accent) 26.7%, transparent)',
          }}
        >
          <span className="insight-realm-diamond-text" style={{ color: 'var(--realm-ink, var(--realm-accent))' }}>
            {realmInfo.rankChar}
          </span>
        </div>
      </div>

      {/* 3. 小境界灵气胶囊框 */}
      <div
        className="insight-realm-minor-box"
        style={{
          borderColor: 'color-mix(in srgb, var(--realm-accent) 33.3%, transparent)',
          backgroundColor: 'var(--realm-minor-bg, rgba(0, 0, 0, 0.45))',
        }}
      >
        <strong className="insight-realm-minor-text" style={{ color: 'var(--realm-ink, var(--realm-accent))' }}>
          {realmInfo.minorName}
        </strong>
      </div>
    </div>
    </FloatingPopover>
  )
}
