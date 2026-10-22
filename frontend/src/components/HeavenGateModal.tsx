import { memo, useCallback, useId, useMemo, useRef, useState } from 'react'
import { Modal } from './Modal'
import { useT } from '../i18n'
import type { LinggenValues } from '../game/types'
import {
  LINGGEN_AXIS_CAP,
  LINGGEN_ELEMENT_KEYS,
  linggenBonusPercent,
  linggenReducePercent,
  type LinggenElementKey,
} from '../game/linggenCalculations'

export { LINGGEN_AXIS_CAP, LINGGEN_ELEMENT_KEYS }
export type { LinggenElementKey }

/**
 * 天门阵盘操作入参：全部由内视面板按服务端权威状态驱动。
 */
interface HeavenGateModalProps {
  /** 弹窗是否开启 */
  open: boolean
  /** 开天门抽取结果；未抽取为 undefined */
  rolled?: LinggenValues
  /** 已确认灵根；未入天门为 undefined */
  confirmed?: LinggenValues
  /** 是否已入天门 */
  entered: boolean
  /** 逆天改命累计次数 */
  rerollCount: number
  /** 当前逆天改命需消耗的底蕴（默认 25% 档） */
  rerollCostPlain: number
  /** 使用逆命丹后需消耗的底蕴（5% 档） */
  rerollCostPill: number
  /** 背包内逆命丹数量 */
  reverseFatePillCount: number
  /** 抽出灵根 */
  onRoll: () => void
  /** 提交五行分布（总值必须与抽取结果一致） */
  onAllocate: (values: LinggenValues) => void
  /** 确认入天门 */
  onEnter: () => void
  /** 逆天改命（可选消耗逆命丹） */
  onReroll: (usePill: boolean) => void
  /** 关闭弹窗 */
  onClose: () => void
}

/** 五系点数和 */
function totalOf(values: LinggenValues): number {
  return values.metal + values.wood + values.water + values.fire + values.earth
}

/** 空灵根：未抽取时的编辑基线 */
const EMPTY_LINGGEN: LinggenValues = { metal: 0, wood: 0, water: 0, fire: 0, earth: 0 }

/** 把点数字符串解析为合法整数点数（越界夹取到 [0, 100]）。 */
function clampPoints(raw: string): number {
  const parsed = Number.parseInt(raw, 10)
  if (!Number.isFinite(parsed)) return 0
  return Math.max(0, Math.min(LINGGEN_AXIS_CAP, parsed))
}

/**
 * 开天门阵盘弹窗 (HeavenGateModal)
 *
 * 交互结构：
 * 1. 未抽取：展示「抽取灵根」入口（叩仙门满修后由 HUD/内视突破按钮进入）；
 * 2. 已抽取未入天门：五行拖拽/输入重分配，总值守恒，确认后入天门；
 * 3. 已入天门：展示生效灵根，仍可逆天改命（可选消耗逆命丹把底蕴成本从 25% 降到 5%）。
 */
export const HeavenGateModal = memo(function HeavenGateModal({
  open,
  rolled,
  confirmed,
  entered,
  rerollCount,
  rerollCostPlain,
  rerollCostPill,
  reverseFatePillCount,
  onRoll,
  onAllocate,
  onEnter,
  onReroll,
  onClose,
}: HeavenGateModalProps): React.JSX.Element {
  const t = useT()
  const titleId = useId()
  const confirmRef = useRef<HTMLButtonElement>(null)
  const [usePill, setUsePill] = useState(false)

  // 编辑基线：已入天门展示生效灵根；未入天门以抽取结果为准。
  const baseline = useMemo<LinggenValues>(
    () => (entered ? confirmed ?? rolled ?? EMPTY_LINGGEN : rolled ?? EMPTY_LINGGEN),
    [entered, confirmed, rolled],
  )
  const baselineTotal = totalOf(baseline)
  const hasRolled = rolled !== undefined && totalOf(rolled) > 0
  const canReroll = rerollCostPlain > 0
  const hasPill = reverseFatePillCount > 0

  const handleAllocate = useCallback((element: LinggenElementKey, raw: string) => {
    if (!hasRolled) return
    const next: LinggenValues = { ...baseline, [element]: clampPoints(raw) }
    // 总值守恒是领域不变量：服务端会拒绝不守恒的提交，这里提前阻止无效请求。
    if (totalOf(next) !== totalOf(rolled ?? EMPTY_LINGGEN)) return
    onAllocate(next)
  }, [baseline, hasRolled, onAllocate, rolled])

  const surfaceText = entered
    ? t('insight.heavenGate.entered')
    : hasRolled
      ? t('insight.heavenGate.rolledReady')
      : t('insight.heavenGate.rolledPending')

  return (
    <Modal open={open} labelledBy={titleId} onClose={onClose} size="large" initialFocusRef={confirmRef}>
      <div className="heaven-gate-modal">
        <h2 id={titleId} className="heaven-gate-modal-title">{t('insight.heavenGate.title')}</h2>
        <p className="heaven-gate-modal-subtitle">{t('insight.heavenGate.subtitle')}</p>
        <p className="heaven-gate-modal-state">{surfaceText}</p>

        <ul className="heaven-gate-axis-list">
          {LINGGEN_ELEMENT_KEYS.map(key => {
            const points = baseline[key]
            const bonus = linggenBonusPercent(points)
            const reduce = linggenReducePercent(points)
            return (
              <li className="heaven-gate-axis-row" key={key}>
                <span className="heaven-gate-axis-name">
                  {t(`insight.heavenGate.element.${key}`)}
                </span>
                <input
                  className="heaven-gate-axis-input"
                  type="number"
                  min={0}
                  max={LINGGEN_AXIS_CAP}
                  step={1}
                  value={points}
                  disabled={entered || !hasRolled}
                  aria-label={t(`insight.heavenGate.element.${key}`)}
                  onChange={event => handleAllocate(key, event.currentTarget.value)}
                />
                <span className="heaven-gate-axis-stat">
                  {t('insight.heavenGate.statBonus')} {bonus}%
                </span>
                <span className="heaven-gate-axis-stat">
                  {t('insight.heavenGate.statReduce')} {reduce}%
                </span>
              </li>
            )
          })}
        </ul>

        <div className="heaven-gate-summary">
          <span>{t('insight.heavenGate.totalPoints')} {baselineTotal}</span>
          <span>{t('insight.heavenGate.rerollCount', { count: rerollCount })}</span>
        </div>

        {canReroll && (
          <div className="heaven-gate-reroll">
            <span className="heaven-gate-reroll-cost">
              {usePill && hasPill
                ? t('insight.heavenGate.costPill', { cost: rerollCostPill })
                : t('insight.heavenGate.costPlain', { cost: rerollCostPlain })}
            </span>
            <label className="heaven-gate-reroll-toggle">
              <input
                type="checkbox"
                checked={usePill && hasPill}
                disabled={!hasPill}
                onChange={event => setUsePill(event.currentTarget.checked)}
              />
              <span>{t('insight.heavenGate.usePillLabel')}</span>
            </label>
          </div>
        )}

        <div className="heaven-gate-actions">
          {!hasRolled && (
            <button className="insight-breakthrough-btn" type="button" onClick={onRoll}>
              {t('insight.heavenGate.rollBtn')}
            </button>
          )}
          {hasRolled && !entered && (
            <button ref={confirmRef} className="insight-breakthrough-btn" type="button" onClick={onEnter}>
              {t('insight.heavenGate.enterBtn')}
            </button>
          )}
          {canReroll && (
            <button className="insight-breakthrough-btn" type="button" onClick={() => onReroll(usePill && hasPill)}>
              {usePill && hasPill
                ? t('insight.heavenGate.rerollWithPillBtn')
                : t('insight.heavenGate.rerollBtn')}
            </button>
          )}
          <button className="breakthrough-modal-cancel" type="button" onClick={onClose}>
            {t('insight.heavenGate.closeBtn')}
          </button>
        </div>
      </div>
    </Modal>
  )
})
