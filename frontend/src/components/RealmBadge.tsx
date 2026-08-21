import React from 'react'
import type { RealmInfo } from '../game/combatConstants'
import { FloatingInfoCard, FloatingPopover } from './FloatingPopover'
import { formatNumber } from '../game/format'
import { useT } from '../i18n'

interface RealmBadgeProps {
  realmInfo: RealmInfo
  className?: string
}

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
          subtitle={t('insight.badge.phase', { phase: realmInfo.phaseName, rank: realmInfo.rankChar })}
          description={t('insight.badge.description')}
          stats={[
            { label: t('insight.badge.stage'), value: realmInfo.stageName, tone: 'gold' },
            { label: t('insight.badge.minor'), value: realmInfo.minorName, tone: 'qi' },
            { label: t('insight.badge.totalLevel'), value: `Lv. ${lv}`, tone: 'gold' },
            { label: t('insight.badge.layerProgress'), value: `${realmInfo.layerProgress.toFixed(1)}%`, tone: 'jade' },
          ]}
        />
      }
    >
      <div
        className={`insight-realm-master-frame ${className}`}
        style={{
          borderColor: `${realmInfo.stageColor}66`,
          boxShadow: `0 0 14px ${realmInfo.stageColor}20, inset 0 0 6px rgba(0,0,0,0.5)`,
        }}
      >
      {/* 1. 大境界牌匾框 */}
      <div
        className="insight-realm-major-box"
        style={{
          borderLeftColor: realmInfo.stageColor,
          borderRightColor: realmInfo.stageColor,
          backgroundColor: `${realmInfo.stageColor}18`,
        }}
      >
        <span className="insight-realm-major-text">{realmInfo.stageName}</span>
      </div>

      {/* 2. 位阶棱形徽章框 (武/玄/天/初/中/后/极) */}
      <div className="insight-realm-diamond-container">
        <div
          className="insight-realm-diamond-badge"
          style={{
            borderColor: realmInfo.stageColor,
            backgroundColor: `${realmInfo.stageColor}28`,
            boxShadow: `0 0 8px ${realmInfo.stageColor}44`,
          }}
        >
          <span className="insight-realm-diamond-text" style={{ color: realmInfo.stageColor }}>
            {realmInfo.rankChar}
          </span>
        </div>
      </div>

      {/* 3. 小境界灵气胶囊框 */}
      <div
        className="insight-realm-minor-box"
        style={{
          borderColor: `${realmInfo.stageColor}55`,
          backgroundColor: 'rgba(0, 0, 0, 0.45)',
        }}
      >
        <strong className="insight-realm-minor-text" style={{ color: realmInfo.stageColor }}>
          {realmInfo.minorName}
        </strong>
      </div>
    </div>
    </FloatingPopover>
  )
}
