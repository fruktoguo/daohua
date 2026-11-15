import { memo } from 'react'
import { hasMailRedDot, useMailStore } from '../game/mailStore'
import { useT } from '../i18n'
import { useUiStore } from '../uiStore'
import { FloatingTooltip } from './FloatingPopover'
import { Icon } from './Icon'

/**
 * 顶栏右上角传书邮件入口按钮：
 * - 位于主题太极切换按钮紧邻左侧（桌面与竖屏全尺寸可见）；
 * - 存在未读邮件、未领取附件或服务端广播冷推标记时展示红点徽记；
 * - 点击唤起/收起仙门传书独立弹窗，保持底层主视图与导航状态不中断。
 */
export const TopbarMailButton = memo(function TopbarMailButton(): React.JSX.Element {
  const t = useT()
  const summary = useMailStore(state => state.summary)
  const mailOpen = useUiStore(state => state.mailOpen)
  const toggleMailOpen = useUiStore(state => state.toggleMailOpen)

  const showRedDot = hasMailRedDot(summary)
  const hasCounts = summary.unreadCount > 0 || summary.claimableCount > 0
  const tooltipTitle = hasCounts
    ? t('app.topbar.mailTipCounts', {
        unread: summary.unreadCount,
        claimable: summary.claimableCount,
      })
    : showRedDot
      ? t('app.topbar.mailTipNew')
      : t('app.topbar.mailTip')

  const ariaLabel = hasCounts
    ? t('app.topbar.mailAriaCounts', {
        unread: summary.unreadCount,
        claimable: summary.claimableCount,
      })
    : t('app.topbar.mailAria')

  return (
    <FloatingTooltip placement="bottom" title={tooltipTitle} tone="gold">
      <button
        aria-label={ariaLabel}
        aria-pressed={mailOpen}
        className={`topbar-mail-btn${mailOpen ? ' is-open' : ''}${showRedDot ? ' has-red-dot' : ''}`}
        data-tour="topbar-mail"
        onClick={toggleMailOpen}
        type="button"
      >
        <Icon name="mail" />
        {showRedDot && (
          <span aria-label={t('app.topbar.mailRedDotAria')} className="topbar-mail-dot" />
        )}
      </button>
    </FloatingTooltip>
  )
})
