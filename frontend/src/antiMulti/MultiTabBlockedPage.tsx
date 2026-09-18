import { memo } from 'react'
import { useT } from '../i18n'
import { requestTabTakeover } from '.'

/**
 * 多标签页阻断页 (MultiTabBlockedPage)
 *
 * 当前标签页被互斥守卫判定为多余副本时展示；提供「在本页继续」接管入口，
 * 点击后原持有页让位并进入阻断态，本页刷新后重新竞逐为运行页。
 */
export const MultiTabBlockedPage = memo(function MultiTabBlockedPage(): React.JSX.Element {
  const t = useT()

  return (
    <div className="status-screen error-screen">
      <div className="motes" aria-hidden="true">
        <i /><i /><i /><i /><i /><i />
      </div>
      <div className="status-card panel">
        <div className="status-error-icon" aria-hidden="true">
          <span>⧉</span>
        </div>
        <h2 className="status-title">{t('app.connect.tabBlockedTitle')}</h2>
        <p className="status-subtitle">{t('app.connect.tabBlockedSubtitle')}</p>
        <div className="status-actions">
          <button type="button" className="status-btn primary" onClick={() => requestTabTakeover()}>
            {t('app.connect.tabTakeoverBtn')}
          </button>
        </div>
      </div>
    </div>
  )
})
