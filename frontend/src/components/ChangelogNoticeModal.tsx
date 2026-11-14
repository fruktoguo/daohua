import { useId } from 'react'
import type { ChangelogEntry } from '../data/changelog'
import { useT } from '../i18n'
import { ChangelogEntrySections } from './ChangelogPage'
import { Icon } from './Icon'
import { Modal } from './Modal'

export interface ChangelogNoticeModalProps {
  /** 控制是否打开首次更新公告弹窗 */
  open: boolean
  /** 当前需要播报的带 noticeId 更新日志条目 */
  entry: ChangelogEntry | null
  /** 确认/关闭回调（会持久化已读 noticeId，后续相同 noticeId 不再自动弹出） */
  onDismiss: () => void
  /** 点击「查看全部日志」时调用（关闭弹窗、持久化已读并跳转至更新日志页面） */
  onViewAll: () => void
}

/**
 * 首次更新公告弹窗（仅当存在玩家尚未确认的 noticeId 时触发一次）。
 * 与客户端版本热更判定完全解耦：后续无新 noticeId 的部署更新不会打扰玩家。
 */
export function ChangelogNoticeModal({
  open,
  entry,
  onDismiss,
  onViewAll,
}: ChangelogNoticeModalProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()

  if (!open || !entry) return null

  return (
    <Modal
      className="changelog-notice-modal"
      labelledBy={titleId}
      onClose={onDismiss}
      open={open}
      size="large"
    >
      <div className="changelog-notice-body">
        <header className="changelog-notice-header">
          <div className="changelog-notice-title-row">
            <span className="changelog-notice-icon">
              <Icon name="changelog" size={18} />
            </span>
            <div>
              <h2 className="changelog-notice-heading" id={titleId}>
                {t('app.changelog.noticeModalTitle')}
              </h2>
              <p className="changelog-notice-meta">
                <span className="changelog-notice-pill">{t('app.changelog.noticeBadge')}</span>
                <span>{t('app.changelog.noticeModalSubtitle', { version: entry.versionLabel, date: entry.date })}</span>
              </p>
            </div>
          </div>
        </header>

        <div className="changelog-hover-banner changelog-hover-banner--compact">
          <Icon name="codex" size={14} />
          <span>{t('app.changelog.hoverHint')}</span>
        </div>

        <div className="changelog-notice-scroll">
          <div className="changelog-notice-entry-title">
            <h3>{entry.title}</h3>
            {entry.summary && <p>{entry.summary}</p>}
          </div>
          <ChangelogEntrySections entry={entry} onNavigate={onDismiss} />
        </div>

        <footer className="changelog-notice-footer">
          <button className="changelog-btn changelog-btn--secondary" onClick={onViewAll} type="button">
            {t('app.changelog.noticeViewAll')}
          </button>
          <button className="changelog-btn changelog-btn--primary" onClick={onDismiss} type="button">
            {t('app.changelog.noticeDismiss')}
          </button>
        </footer>
      </div>
    </Modal>
  )
}
