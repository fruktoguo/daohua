import { memo, useId, useRef } from 'react'
import { RichTemplateText } from './EntityReference'
import { Icon } from './Icon'
import { Modal } from './Modal'
import { useT } from '../i18n'
import { LINGGEN_AXES } from '../game/linggenBoard'

/** 开天门规则说明独立弹窗属性。 */
export interface HeavenGateGuideModalProps {
  /** 是否显示说明弹窗 */
  open: boolean
  /** 关闭回调（关闭后保留底层开天门当前页签与状态） */
  onClose: () => void
}

/**
 * 开天门与灵根独立规则说明弹窗：
 * 1. 首次打开开天门时由系统自动在最上层弹出（不改变底层「天门」页签焦点）；
 * 2. 横屏与竖屏顶部「说明」按钮均复用本弹窗，保证说明文本与排版单一真源。
 */
export const HeavenGateGuideModal = memo(function HeavenGateGuideModal({
  open,
  onClose,
}: HeavenGateGuideModalProps): React.JSX.Element | null {
  // Hooks 必须在任何提前返回前无条件调用，保证挂载时序稳定。
  const t = useT()
  const titleId = useId()
  const dismissBtnRef = useRef<HTMLButtonElement>(null)

  if (!open) return null

  return (
    <Modal
      className="heaven-gate-guide-modal"
      initialFocusRef={dismissBtnRef}
      labelledBy={titleId}
      onClose={onClose}
      open={open}
      size="medium"
    >
      <section className="hg-guide">
        <header className="hg-guide__header">
          <div className="hg-guide__title-row">
            <span className="hg-guide__icon" aria-hidden="true">
              <Icon name="codex" size={18} />
            </span>
            <div className="hg-guide__heading-group">
              <h2 className="hg-guide__title" id={titleId}>
                {t('insight.heavenGate.guide.title')}
              </h2>
              <p className="hg-guide__subtitle">{t('insight.heavenGate.guide.subtitle')}</p>
            </div>
          </div>
        </header>

        <div className="hg-guide__scroll">
          {/* 一、五行灵根加成与适合路线 */}
          <section className="hg-guide__section">
            <h3 className="hg-guide__section-title">{t('insight.heavenGate.guide.elementsHeading')}</h3>
            <p className="hg-guide__text">{t('insight.heavenGate.guide.elementsIntro')}</p>
            <div className="hg-guide__elements">
              {LINGGEN_AXES.map(axis => (
                <article
                  className="hg-guide__element-card"
                  data-element={axis.key}
                  key={axis.key}
                >
                  <div className="hg-guide__element-head">
                    <span className={`hg-bonus-card__badge hg-bonus-card__badge--${axis.key}`}>
                      {t(`insight.heavenGate.element.${axis.key}`)}
                    </span>
                    <span className="hg-guide__element-route">
                      {t(`insight.heavenGate.guide.route.${axis.key}`)}
                    </span>
                  </div>
                  <p className="hg-guide__element-desc">
                    {t(`insight.heavenGate.guide.desc.${axis.key}`)}
                  </p>
                </article>
              ))}
            </div>
          </section>

          {/* 二、手动拖动调整灵根点数 */}
          <section className="hg-guide__section">
            <h3 className="hg-guide__section-title">{t('insight.heavenGate.guide.dragHeading')}</h3>
            <p className="hg-guide__text">{t('insight.heavenGate.guide.dragIntro')}</p>
            <ul className="hg-guide__list">
              <li>{t('insight.heavenGate.guide.dragRuleRefund')}</li>
              <li>{t('insight.heavenGate.guide.dragRuleSpend')}</li>
            </ul>
          </section>

          {/* 三、灵根数量与突破速度规划 */}
          <section className="hg-guide__section">
            <h3 className="hg-guide__section-title">{t('insight.heavenGate.guide.penaltyHeading')}</h3>
            <p className="hg-guide__text">{t('insight.heavenGate.guide.penaltyBody1')}</p>
            <p className="hg-guide__text">{t('insight.heavenGate.guide.penaltyBody2')}</p>
          </section>

          {/* 四、实用建议：提前准备逆命丹 */}
          <section className="hg-guide__section hg-guide__tip-box">
            <h3 className="hg-guide__section-title">{t('insight.heavenGate.guide.pillHeading')}</h3>
            <p className="hg-guide__text">
              <RichTemplateText template={t('insight.heavenGate.guide.pillBody')} />
            </p>
          </section>
        </div>

        <footer className="hg-guide__footer">
          <button
            className="hg-btn hg-btn--primary"
            onClick={onClose}
            ref={dismissBtnRef}
            type="button"
          >
            {t('insight.heavenGate.guide.dismissBtn')}
          </button>
        </footer>
      </section>
    </Modal>
  )
})
