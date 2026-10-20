import React, { memo } from 'react'
import {
  formatTraitDescription,
  getExpForNextTraitLevel,
  getTraitLevelScale,
  type TraitMeta,
} from '../game/combatConstants'
import { FloatingInfoCard, FloatingPopover, FloatingTooltip, type FloatingTone } from './FloatingPopover'
import { useT } from '../i18n'

export interface TraitBarItemProps {
  /** 特质定义 */
  def: TraitMeta
  /** 特质当前等级 (>= 1) */
  level?: number
  /** 当前累积经验 (用于升级进度细条) */
  exp?: number
  /** 觉醒抽取获得数量 (awaken 模式使用) */
  quantity?: number
  /** 是否为本次新觉醒解锁 (展示 NEW 斜角标) */
  isNew?: boolean
  /** 展示模式：awaken(觉醒抽卡) | insight(内视配置) | codex(万象图鉴) */
  mode?: 'awaken' | 'insight' | 'codex'
  /** 是否已被装配 */
  isEquipped?: boolean
  /** 是否允许点击交互/装配 */
  disabled?: boolean
  /** 快捷操作标签，如 "卸下" 或 "装备" */
  actionLabel?: string
  /** 点击整条回调 */
  onClick?: () => void
  /** 点击右侧快捷操作按钮回调 */
  onAction?: () => void
  /** 自定义类名 */
  className?: string
  /** 动画延迟样式（觉醒抽卡逐条弹入使用） */
  style?: React.CSSProperties
}

const QUALITY_TONE_MAP: Record<number, FloatingTone> = {
  1: 'muted',
  2: 'jade',
  3: 'qi',
  4: 'purple',
  5: 'gold',
}

/**
 * 特质长条展示单元 (TraitBarItem)
 * 统一用于：觉醒石抽取展示、内视特质面板配置、万象图鉴特质浏览
 */
export const TraitBarItem = memo(function TraitBarItem({
  def,
  level = 1,
  exp = 0,
  quantity,
  isNew = false,
  mode = 'insight',
  isEquipped = false,
  disabled = false,
  actionLabel,
  onClick,
  onAction,
  className = '',
  style,
}: TraitBarItemProps): React.JSX.Element {
  const t = useT()
  const quality = def.quality ?? 1
  const tone = QUALITY_TONE_MAP[quality] ?? 'muted'
  const qualityName = t(`insight.traits.qualityQ${quality}`)
  const reqExp = getExpForNextTraitLevel(level)
  const expProgress = Math.min(100, Math.max(0, (exp / reqExp) * 100))
  const desc = formatTraitDescription(def.id, level)

  // 浮层内容构建
  const popoverContent = (
    <FloatingInfoCard
      badge={qualityName}
      badgeTone={tone}
      description={desc}
      footer={
        <div className="trait-popover-footer">
          {level > 1 && (
            <div className="trait-popover-scale-hint">
              {t('insight.traits.levelGrowHint', { scale: ((getTraitLevelScale(level) - 1) * 100).toFixed(0) })}
            </div>
          )}
          {actionLabel && (
            <button
              className={`trait-popover-action-btn ${isEquipped ? 'is-unequip' : 'is-equip'}`}
              disabled={disabled}
              onClick={e => {
                e.stopPropagation()
                onAction?.()
              }}
              type="button"
            >
              {actionLabel}
            </button>
          )}
        </div>
      }
      subtitle={`${t('insight.traits.capacityLabel')}：${def.cost} ${t('insight.traits.pointUnit')}`}
      title={def.name}
    />
  )

  const handleActionClick = (e: React.MouseEvent) => {
    e.stopPropagation()
    onAction?.()
  }

  return (
    <FloatingPopover content={popoverContent} disabled={false} displayContents interactive size="small">
      <div
        className={`trait-bar-item trait-quality--q${quality} ${mode === 'awaken' ? 'is-awaken-mode' : ''} ${
          isEquipped ? 'is-equipped' : ''
        } ${isNew ? 'has-new-ribbon' : ''} ${disabled ? 'is-disabled' : ''} ${onClick ? 'is-clickable' : ''} ${className}`.trim()}
        onClick={disabled ? undefined : onClick}
        style={style}
      >
        {/* NEW 斜角标 */}
        {isNew && (
          <div className="trait-bar-ribbon-wrap" aria-hidden="true">
            <span className="trait-bar-ribbon-badge">NEW</span>
          </div>
        )}

        {/* 品阶标签 */}
        <span className="trait-bar-quality-tag">{qualityName}</span>

        {/* 特质名称 */}
        <strong className="trait-bar-name">{def.name}</strong>

        {/* 消耗点数（内视和图鉴中展示） */}
        {mode !== 'awaken' && <span className="trait-bar-cost">{def.cost}点</span>}

        {/* 抽取模式展示数量 */}
        {mode === 'awaken' && quantity !== undefined && (
          <span className="trait-bar-quantity tone-gold">×{quantity}</span>
        )}

        {/* 内视模式展示 Lv 与操作状态 */}
        {mode === 'insight' && (
          <div className="trait-bar-insight-right">
            <span className="trait-bar-level">Lv.{level}</span>
            {actionLabel && (
              <button
                className={`trait-bar-action-btn ${isEquipped ? 'is-unequip' : 'is-equip'}`}
                disabled={disabled}
                onClick={handleActionClick}
                type="button"
              >
                {actionLabel}
              </button>
            )}
          </div>
        )}

        {/* 图鉴模式 */}
        {mode === 'codex' && (
          <div className="trait-bar-codex-right">
            {level > 0 ? (
              <span className="trait-bar-level">Lv.{level}</span>
            ) : (
              <span className="trait-bar-unlocked-state text-dim">{t('insight.traits.unequipped')}</span>
            )}
          </div>
        )}

        {/* 底部细横条：升级进度（仅内视模式且非禁用时渲染） */}
        {mode === 'insight' && (
          <FloatingTooltip
            displayContents
            title={`${t('insight.traits.expLabel')}：${exp} / ${reqExp} (${expProgress.toFixed(0)}%)`}
          >
            <div className="trait-bar-exp-track" aria-hidden="true">
              <div
                className="trait-bar-exp-fill"
                style={{ width: `${expProgress}%` }}
              />
            </div>
          </FloatingTooltip>
        )}
      </div>
    </FloatingPopover>
  )
})
