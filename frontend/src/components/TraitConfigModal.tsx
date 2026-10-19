import { memo, useId, useMemo, useState } from 'react'
import { Modal } from './Modal'
import {
  TRAIT_DEFINITIONS,
  calculateTraitCapacity,
  formatTraitDescription,
  getExpForNextTraitLevel,
} from '../game/combatConstants'
import type { TraitProgress } from '../game/types'
import { gameClient } from '../network/gameClient'
import { useT } from '../i18n'

interface TraitConfigModalProps {
  open: boolean
  onClose: () => void
  realmLevel: number
  equippedTraitIds: string[]
  unlockedTraits: TraitProgress[]
}

const QUALITY_COLOR_MAP: Record<number, string> = {
  1: 'var(--text-dim, #a0a0a0)', // 凡品 白
  2: 'var(--jade, #48bb78)', // 黄品 绿
  3: 'var(--qi, #4299e1)', // 玄品 蓝
  4: 'var(--purple, #9f7aea)', // 地品 紫
  5: 'var(--gold-bright, #ecc94b)', // 天品 金
}

const QUALITY_NAME_MAP: Record<number, string> = {
  1: '凡品',
  2: '黄品',
  3: '玄品',
  4: '地品',
  5: '天品',
}

export const TraitConfigModal = memo(function TraitConfigModal({
  open,
  onClose,
  realmLevel,
  equippedTraitIds,
  unlockedTraits,
}: TraitConfigModalProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  const capacity = useMemo(() => calculateTraitCapacity(realmLevel), [realmLevel])
  const [selectedIds, setSelectedIds] = useState<Set<string>>(() => new Set(equippedTraitIds))

  // 计算当前选中的总点数消耗
  const totalCost = useMemo(() => {
    let sum = 0
    for (const id of selectedIds) {
      const def = TRAIT_DEFINITIONS[id]
      if (def) sum += def.cost
    }
    return sum
  }, [selectedIds])

  const remainingCapacity = capacity - totalCost

  // 已解锁特质按品质从高到低、然后按 ID 排序
  const sortedUnlockedTraits = useMemo(() => {
    const list = [...unlockedTraits]
    list.sort((a, b) => {
      const defA = TRAIT_DEFINITIONS[a.id]
      const defB = TRAIT_DEFINITIONS[b.id]
      const qA = defA?.quality ?? 1
      const qB = defB?.quality ?? 1
      if (qB !== qA) return qB - qA
      return a.id.localeCompare(b.id)
    })
    return list
  }, [unlockedTraits])

  const handleToggle = (id: string, cost: number) => {
    setSelectedIds(prev => {
      const next = new Set(prev)
      if (next.has(id)) {
        next.delete(id)
      } else {
        if (totalCost + cost > capacity) return prev
        next.add(id)
      }
      return next
    })
  }

  const handleSave = () => {
    gameClient.sendCommand('trait.set', { traitIds: Array.from(selectedIds) })
    onClose()
  }

  if (!open) return null

  return (
    <Modal labelledBy={titleId} onClose={onClose} open={open} size="medium">
      <div className="trait-config-dialog">
        <header className="trait-config-header">
          <h2 id={titleId} className="trait-config-title">{t('insight.traits.modalTitle')}</h2>
          <div className="trait-capacity-bar-wrap">
            <div className="trait-capacity-meta">
              <span className="trait-capacity-label">{t('insight.traits.capacityLabel')}</span>
              <strong className={remainingCapacity < 0 ? 'tone-danger' : 'tone-gold'}>
                {totalCost} / {capacity} {t('insight.traits.pointUnit')}
                {remainingCapacity >= 0 && (
                  <span className="trait-capacity-remain"> ({t('insight.traits.remainLabel', { count: remainingCapacity })})</span>
                )}
              </strong>
            </div>
            <div className="trait-capacity-track">
              <div
                className="trait-capacity-fill"
                style={{ width: `${Math.min(100, Math.max(0, (totalCost / Math.max(1, capacity)) * 100))}%` }}
              />
            </div>
          </div>
        </header>

        <div className="trait-config-body">
          {sortedUnlockedTraits.length === 0 ? (
            <div className="trait-empty-tip">
              <p>{t('insight.traits.noneUnlockedTip')}</p>
            </div>
          ) : (
            <div className="trait-card-grid">
              {sortedUnlockedTraits.map(progress => {
                const def = TRAIT_DEFINITIONS[progress.id]
                if (!def) return null
                const isSelected = selectedIds.has(progress.id)
                const canSelect = isSelected || totalCost + def.cost <= capacity
                const reqExp = getExpForNextTraitLevel(progress.level)
                const qualityColor = QUALITY_COLOR_MAP[def.quality] ?? 'var(--text-dim)'
                const qualityName = QUALITY_NAME_MAP[def.quality] ?? '凡品'
                const desc = formatTraitDescription(progress.id, progress.level)

                return (
                  <div
                    key={progress.id}
                    className={`trait-card-item ${isSelected ? 'is-selected' : ''} ${!canSelect ? 'is-disabled' : ''}`}
                    style={{ borderColor: isSelected ? 'var(--gold-bright)' : qualityColor }}
                    onClick={() => canSelect && handleToggle(progress.id, def.cost)}
                    role="button"
                    tabIndex={canSelect ? 0 : -1}
                    onKeyDown={e => {
                      if ((e.key === 'Enter' || e.key === ' ') && canSelect) {
                        e.preventDefault()
                        handleToggle(progress.id, def.cost)
                      }
                    }}
                  >
                    <div className="trait-card-top">
                      <div className="trait-card-name-group">
                        <span className="trait-quality-tag" style={{ color: qualityColor, borderColor: qualityColor }}>
                          {qualityName}
                        </span>
                        <strong className="trait-card-name" style={{ color: qualityColor }}>{def.name}</strong>
                        <span className="trait-card-level">Lv.{progress.level}</span>
                      </div>
                      <span className="trait-card-cost">
                        {def.cost} {t('insight.traits.pointUnit')}
                      </span>
                    </div>

                    <p className="trait-card-desc">{desc}</p>

                    <div className="trait-card-bottom">
                      <div className="trait-card-exp">
                        <span className="trait-card-exp-text">
                          {t('insight.traits.expLabel')}: {progress.exp} / {reqExp}
                        </span>
                        <div className="trait-card-exp-track">
                          <div
                            className="trait-card-exp-fill"
                            style={{ width: `${Math.min(100, (progress.exp / reqExp) * 100)}%` }}
                          />
                        </div>
                      </div>
                      <span className={`trait-card-checkbox ${isSelected ? 'is-checked' : ''}`}>
                        {isSelected ? '✓ ' + t('insight.traits.equipped') : t('insight.traits.unequipped')}
                      </span>
                    </div>
                  </div>
                )
              })}
            </div>
          )}
        </div>

        <footer className="trait-config-actions">
          <button className="trait-config-cancel" onClick={onClose} type="button">
            {t('common.cancel')}
          </button>
          <button className="trait-config-confirm" onClick={handleSave} type="button">
            {t('insight.traits.saveBtn')}
          </button>
        </footer>
      </div>
    </Modal>
  )
})
