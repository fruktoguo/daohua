import { useId, useMemo, useRef } from 'react'
import { Modal } from './Modal'
import { type PillTone } from './PillLabel'
import { useT } from '../i18n'
import { formatNumber } from '../game/format'
import {
  calculateBaseAttributes,
  calculateRealmInfo,
  type BaseAttributePool,
} from '../game/combatConstants'

/** 基础属性对比行定义：标签复用 insight.base.* 词典，取值走 BaseAttributePool */
interface BreakthroughRow {
  /** insight.base.* 词典键 */
  key: string
  /** 属性专属修仙色调（与内视「基础属性」子页 PillStatLabel 的 tone 一致） */
  tone: PillTone
  get: (pool: BaseAttributePool) => number
}

/** 突破确认弹窗展示的基础属性对比行（顺序与色调对齐内视「基础属性」子页） */
const COMPARISON_ROWS: readonly BreakthroughRow[] = [
  { key: 'maxHp', tone: 'emerald', get: pool => pool.baseHp },
  { key: 'maxMp', tone: 'cyan', get: pool => pool.baseMp },
  { key: 'physAtk', tone: 'crimson', get: pool => pool.baseAd },
  { key: 'spellAtk', tone: 'indigo', get: pool => pool.baseAp },
  { key: 'armor', tone: 'bronze', get: pool => pool.baseArmor },
  { key: 'magicRes', tone: 'violet', get: pool => pool.baseMagicRes },
  { key: 'accuracy', tone: 'sky', get: pool => pool.baseAccuracy },
  { key: 'evasion', tone: 'teal', get: pool => pool.baseEvasion },
  { key: 'crit', tone: 'gold', get: pool => pool.baseCrit },
  { key: 'critAvoid', tone: 'slate', get: pool => pool.baseCritAvoid },
  { key: 'parryBreak', tone: 'amber', get: pool => pool.baseBreak },
  { key: 'parryResolve', tone: 'silver', get: pool => pool.baseParry },
  { key: 'hpRegen', tone: 'emerald', get: pool => pool.baseHpRegen },
  { key: 'mpRegen', tone: 'cyan', get: pool => pool.baseMpRegen },
]

/**
 * 破境突破确认弹窗 Props
 */
interface BreakthroughConfirmModalProps {
  /** 弹窗是否开启 */
  open: boolean
  /** 当前境界等级（服务端权威 stats.realmLevel，缺省按 1 起步） */
  currentRealmLevel?: number
  /** 确认破境回调（发送 progression.breakthrough 命令） */
  onConfirm: () => void
  /** 关闭弹窗回调 */
  onClose: () => void
}

/**
 * 破境突破确认弹窗 (BreakthroughConfirmModal)
 *
 * 突破前二次确认：展示境界跃迁（当前小境界 → 突破后小境界）与基础属性底座
 * 逐项增量对比（当前值 → +增量 → 突破后值）。
 *
 * 对比口径对齐服务端权威属性解析（PlayerAttributeResolver 按整数境界等级取
 * BaseAttributes.At(level) 锚点）：突破前取 At(N)、突破后取 At(N+1)。
 * 不用当前修为参与插值——修为圆满时连续插值 totalLevel 已达 N+1，会误显示零增益。
 */
export function BreakthroughConfirmModal({
  open,
  currentRealmLevel,
  onConfirm,
  onClose,
}: BreakthroughConfirmModalProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  const confirmRef = useRef<HTMLButtonElement>(null)

  // 服务端权威整级口径：突破 = 整级跳变 N → N+1（本层修为清零由突破语义保证）。
  const realmLevel = Math.max(1, Math.floor(currentRealmLevel ?? 1))
  const pools = useMemo(() => ({
    before: calculateBaseAttributes(0, realmLevel),
    after: calculateBaseAttributes(0, realmLevel + 1),
  }), [realmLevel])

  const realmBefore = useMemo(() => calculateRealmInfo(0, realmLevel).minorName, [realmLevel])
  const realmAfter = useMemo(() => calculateRealmInfo(0, realmLevel + 1).minorName, [realmLevel])

  // 只展示有增益的属性行（如最高境界无跳变时列表为空，走空态提示）。
  const changedRows = useMemo(() => COMPARISON_ROWS
    .map(row => {
      const before = Math.floor(row.get(pools.before))
      const after = Math.floor(row.get(pools.after))
      return { key: row.key, tone: row.tone, before, after, delta: after - before }
    })
    .filter(row => row.delta > 0), [pools])

  return (
    <Modal initialFocusRef={confirmRef} labelledBy={titleId} onClose={onClose} open={open} size="small">
      <div className="breakthrough-modal">
        <h2 id={titleId} className="breakthrough-modal-title">{t('insight.breakthroughModal.title')}</h2>
        <div className="breakthrough-modal-realm">
          <span>{realmBefore}</span>
          <span aria-hidden="true" className="breakthrough-modal-realm-arrow">→</span>
          <strong>{realmAfter}</strong>
        </div>
        {changedRows.length > 0 ? (
          <ul className="breakthrough-modal-attrs">
            {changedRows.map(row => (
              <li className={`breakthrough-modal-row pill-tone--${row.tone}`} key={row.key}>
                <span className="breakthrough-modal-row-label">{t(`insight.base.${row.key}`)}</span>
                <span className="breakthrough-modal-row-before">{formatNumber(row.before)}</span>
                <span aria-hidden="true" className="breakthrough-modal-row-arrow underlined-value">→</span>
                <span className="breakthrough-modal-row-after underlined-value">{formatNumber(row.after)}</span>
                <span className="breakthrough-modal-row-gain underlined-value">+{formatNumber(row.delta)}</span>
              </li>
            ))}
          </ul>
        ) : (
          <p className="breakthrough-modal-empty">{t('insight.breakthroughModal.noChange')}</p>
        )}
        <div className="breakthrough-modal-actions">
          <button onClick={onClose} type="button">{t('common.cancel')}</button>
          <button className="breakthrough-modal-confirm" onClick={onConfirm} ref={confirmRef} type="button">
            {t('insight.breakthroughModal.confirmBtn')}
          </button>
        </div>
      </div>
    </Modal>
  )
}
