import { memo, useEffect, useId, useMemo, useRef, useState } from 'react'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { findEquipmentInCatalog, findItemInCatalog, resolveCatalogItemMetadata } from '../game/ids'
import { filterMailItems, useMailStore, type MailFilter } from '../game/mailStore'
import { useT, type TFunction } from '../i18n'
import type { MailAttachmentDto, MailListItemDto } from '../network/protocol'
import { ConfirmDialog } from './ConfirmDialog'
import { RichTemplateText } from './EntityReference'
import { Icon } from './Icon'
import { ItemIcon, ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { Modal } from './Modal'

export interface MailModalProps {
  /** 弹窗开启状态 */
  open: boolean
  /** 关闭弹窗回调 */
  onClose: () => void
}

function formatMailTimestamp(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  const now = new Date()
  const sameYear = date.getFullYear() === now.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  const hours = String(date.getHours()).padStart(2, '0')
  const minutes = String(date.getMinutes()).padStart(2, '0')
  return sameYear
    ? `${month}-${day} ${hours}:${minutes}`
    : `${date.getFullYear()}-${month}-${day} ${hours}:${minutes}`
}

function formatExpiryLabel(iso: string, t: TFunction): string {
  const target = new Date(iso).getTime()
  if (!Number.isFinite(target)) return iso
  const diffMs = target - Date.now()
  if (diffMs <= 0) return t('app.mail.expired')
  const hours = Math.floor(diffMs / 3_600_000)
  if (hours < 24) {
    return t('app.mail.expiresInHours', { hours: Math.max(1, hours) })
  }
  const days = Math.floor(hours / 24)
  return t('app.mail.expiresInDays', { days })
}

/** 邮件附件紧凑横向卡：左侧复用 ItemTile + ItemTooltipTarget 展示品质与悬浮详情，右侧单行命名与数量。 */
const MailAttachmentCard = memo(function MailAttachmentCard({
  attachment,
  isClaimed,
}: {
  attachment: MailAttachmentDto
  isClaimed: boolean
}): React.JSX.Element {
  const t = useT()
  const itemsCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const itemDef = findItemInCatalog(itemsCatalog, attachment.itemId) ?? null
  const equipmentDef = findEquipmentInCatalog(equipmentCatalog, attachment.itemId) ?? null
  const meta = resolveCatalogItemMetadata(attachment.itemId, {
    itemDefinition: itemDef,
    equipmentDefinition: equipmentDef,
    t,
  })
  const formattedQty = formatNumber(attachment.quantity, { maxDecimals: 0 })

  return (
    <div className={`mail-attachment-card${isClaimed ? ' is-claimed' : ''}`}>
      <ItemTooltipTarget
        amount={attachment.quantity}
        className="mail-attachment-anchor"
        definition={itemDef ?? undefined}
        equipmentDefinition={equipmentDef ?? undefined}
        itemId={attachment.itemId}
      >
        <ItemTile
          amount={attachment.quantity}
          category={meta.category}
          className="mail-attachment-tile"
          enhancementLevel={0}
          itemId={attachment.itemId}
          level={meta.level}
          name={meta.label}
          rarity={meta.rarity}
        />
      </ItemTooltipTarget>
      <div className="mail-attachment-info">
        <span className="mail-attachment-name" title={meta.label}>{meta.label}</span>
        <div className="mail-attachment-sub">
          <span className="mail-attachment-qty">×{formattedQty}</span>
          {isClaimed && <span className="mail-attachment-claimed-tag">{t('app.mail.claimedBadge')}</span>}
        </div>
      </div>
    </div>
  )
})

/** 左侧/竖屏邮件摘要行：两行高密度排布（首行红点+类型+标题+时刻；次行发件人+领奖状态+内联附件图标）。 */
const MailListRow = memo(function MailListRow({
  mail,
  selected,
  onSelect,
}: {
  mail: MailListItemDto
  selected: boolean
  onSelect: (mailKey: string) => void
}): React.JSX.Element {
  const t = useT()
  const itemsCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const previewAttachments = mail.attachments.slice(0, 2)
  const extraAttachmentCount = mail.attachments.length - previewAttachments.length
  const unclaimed = mail.hasAttachments && !mail.isClaimed

  return (
    <button
      aria-current={selected ? 'true' : undefined}
      className={`mail-list-card${selected ? ' is-selected' : ''}${mail.isRead ? ' is-read' : ' is-unread'}${unclaimed ? ' has-unclaimed' : ''}`}
      onClick={() => onSelect(mail.mailKey)}
      type="button"
    >
      <div className="mail-list-card-top">
        {!mail.isRead && <span aria-label={t('app.mail.unreadBadge')} className="mail-unread-dot" />}
        <span className={`mail-kind-pill mail-kind-pill--${mail.kind}`}>
          {mail.kind === 'broadcast' ? t('app.mail.kindBroadcast') : t('app.mail.kindPersonal')}
        </span>
        <span className="mail-list-title" title={mail.title}>{mail.title}</span>
        <time className="mail-list-time" dateTime={mail.createdAt}>
          {formatMailTimestamp(mail.createdAt)}
        </time>
      </div>
      <div className="mail-list-card-bottom">
        <span className="mail-list-sender">{mail.senderName}</span>
        <div className="mail-list-card-status">
          {unclaimed ? (
            <span className="mail-status-pill mail-status-pill--claimable">{t('app.mail.claimableBadge')}</span>
          ) : mail.hasAttachments ? (
            <span className="mail-status-pill mail-status-pill--claimed">{t('app.mail.claimedBadge')}</span>
          ) : null}
          {previewAttachments.length > 0 && (
            <div className="mail-list-attach-preview" aria-label={t('app.mail.attachmentsAria')}>
              {previewAttachments.map(att => {
                const meta = resolveCatalogItemMetadata(att.itemId, {
                  itemDefinition: findItemInCatalog(itemsCatalog, att.itemId) ?? null,
                  equipmentDefinition: findEquipmentInCatalog(equipmentCatalog, att.itemId) ?? null,
                  t,
                })
                return (
                  <span className="mail-list-attach-chip" key={att.itemId} title={`${meta.label} ×${att.quantity}`}>
                    <ItemIcon className="mail-list-attach-icon" itemId={att.itemId} />
                    <span className="mail-list-attach-qty">×{formatNumber(att.quantity, { maxDecimals: 0 })}</span>
                  </span>
                )
              })}
              {extraAttachmentCount > 0 && (
                <span className="mail-list-attach-more">+{extraAttachmentCount}</span>
              )}
            </div>
          )}
        </div>
      </div>
    </button>
  )
})

/**
 * 仙门传书独立高密度响应式弹窗：
 * - 基于公共 Modal 挂载于顶层弹窗层，不替换底层主界面或导航高亮；
 * - 横屏（含手机矮横屏）固定外框，左列表 + 右详情各自独立滚动；
 * - 竖屏窄屏铺满可用视口并切换为「列表 ↔ 详情」全高钻取；
 * - 加载或筛选结果为空且无展开详情时渲染单幅居中空态，避免嵌套双空框。
 */
export const MailModal = memo(function MailModal({ open, onClose }: MailModalProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  const detailScrollRef = useRef<HTMLDivElement>(null)

  const summary = useMailStore(state => state.summary)
  const mails = useMailStore(state => state.mails)
  const detailsByKey = useMailStore(state => state.detailsByKey)
  const selectedMailKey = useMailStore(state => state.selectedMailKey)
  const filter = useMailStore(state => state.filter)
  const loadingList = useMailStore(state => state.loadingList)
  const loadingDetailKey = useMailStore(state => state.loadingDetailKey)
  const actionBusyKey = useMailStore(state => state.actionBusyKey)
  const error = useMailStore(state => state.error)

  const setFilter = useMailStore(state => state.setFilter)
  const refreshList = useMailStore(state => state.refreshList)
  const selectMail = useMailStore(state => state.selectMail)
  const clearSelection = useMailStore(state => state.clearSelection)
  const claimMail = useMailStore(state => state.claimMail)
  const claimAllMails = useMailStore(state => state.claimAllMails)
  const deleteOneMail = useMailStore(state => state.deleteOneMail)
  const deleteAllReadMails = useMailStore(state => state.deleteAllReadMails)

  const [mobileShowDetail, setMobileShowDetail] = useState(false)
  const [confirmDeleteReadOpen, setConfirmDeleteReadOpen] = useState(false)

  useEffect(() => {
    if (!open) {
      setMobileShowDetail(false)
      setConfirmDeleteReadOpen(false)
      return
    }
    void useMailStore.getState().refreshList()
  }, [open])

  const visibleMails = useMemo(() => filterMailItems(mails, filter), [mails, filter])
  const selectedListMeta = useMemo(
    () => mails.find(m => m.mailKey === selectedMailKey) ?? null,
    [mails, selectedMailKey],
  )
  const selectedDetail = selectedMailKey ? (detailsByKey[selectedMailKey] ?? null) : null
  const activeMail = selectedDetail ?? selectedListMeta

  // 当列表清空或选中目标消失时自动回退移动端列表页
  useEffect(() => {
    if (!selectedMailKey || !selectedListMeta) {
      setMobileShowDetail(false)
    }
  }, [selectedMailKey, selectedListMeta])

  // 切换阅读信件时重置正文滚动条位置
  useEffect(() => {
    if (detailScrollRef.current) {
      detailScrollRef.current.scrollTop = 0
    }
  }, [activeMail?.mailKey])

  if (!open) return null

  const hasClaimable = summary.claimableCount > 0 || mails.some(m => m.hasAttachments && !m.isClaimed)
  const hasDeletableRead = mails.some(m => m.isRead && (!m.hasAttachments || m.isClaimed))
  const isBusy = actionBusyKey !== null

  const activeHasUnclaimed = Boolean(activeMail && activeMail.hasAttachments && !activeMail.isClaimed)
  const showUnifiedEmpty = visibleMails.length === 0 && !activeMail

  const detailBadges = activeMail ? (
    <>
      <span className={`mail-kind-pill mail-kind-pill--${activeMail.kind}`}>
        {activeMail.kind === 'broadcast' ? t('app.mail.kindBroadcast') : t('app.mail.kindPersonal')}
      </span>
      {activeMail.kind === 'broadcast' && (
        <span className="mail-scope-pill">
          {activeMail.onlyExistingPlayers ? t('app.mail.scopeExisting') : t('app.mail.scopeAll')}
        </span>
      )}
      <span className="mail-expiry-pill">{formatExpiryLabel(activeMail.expiresAt, t)}</span>
    </>
  ) : null

  const handleSelectMail = (mailKey: string) => {
    setMobileShowDetail(true)
    void selectMail(mailKey)
  }

  const handleBackToList = () => {
    setMobileShowDetail(false)
    // 若阅读完当前信件后所在筛选列表已无其他条目，同步清选以免残留无效窄屏页头
    if (visibleMails.length === 0) {
      clearSelection()
    }
  }

  return (
    <>
      <Modal
        className="mail-modal"
        labelledBy={titleId}
        onClose={onClose}
        open={open}
        size="large"
      >
        <div className="mail-modal-inner">
          <header className="mail-modal-header">
            <div className="mail-header-brand">
              <span aria-hidden="true" className="mail-title-icon">
                <Icon name="mail" size={15} />
              </span>
              <h2 className="mail-title" id={titleId}>{t('app.mail.title')}</h2>
              <span className="mail-count-chip">{t('app.mail.countBadge', { total: mails.length })}</span>
            </div>

            <div className="mail-header-controls">
              <div aria-label={t('app.mail.filterAria')} className="mail-seg-tabs" role="tablist">
                {(['all', 'unread', 'claimable'] as readonly MailFilter[]).map(tab => {
                  const badgeCount = tab === 'unread'
                    ? summary.unreadCount
                    : tab === 'claimable'
                      ? summary.claimableCount
                      : 0
                  return (
                    <button
                      aria-selected={filter === tab}
                      className={`mail-seg-tab${filter === tab ? ' is-active' : ''}`}
                      key={tab}
                      onClick={() => setFilter(tab)}
                      role="tab"
                      type="button"
                    >
                      <span>{t(`app.mail.filter.${tab}`)}</span>
                      {badgeCount > 0 && (
                        <span className={`mail-seg-badge mail-seg-badge--${tab}`}>
                          {badgeCount > 99 ? '99+' : badgeCount}
                        </span>
                      )}
                    </button>
                  )
                })}
              </div>

              <div className="mail-header-actions">
                <button
                  className="mail-btn mail-btn--primary"
                  disabled={isBusy || !hasClaimable}
                  onClick={() => void claimAllMails()}
                  type="button"
                >
                  {actionBusyKey === 'claim-all' ? t('app.mail.claiming') : t('app.mail.claimAll')}
                </button>
                <button
                  className="mail-btn mail-btn--secondary"
                  disabled={isBusy || !hasDeletableRead}
                  onClick={() => setConfirmDeleteReadOpen(true)}
                  type="button"
                >
                  {actionBusyKey === 'delete-read' ? t('app.mail.deleting') : t('app.mail.deleteRead')}
                </button>
                <button
                  aria-label={t('app.mail.refresh')}
                  className={`mail-btn mail-btn--ghost mail-btn--icon${loadingList ? ' is-spinning' : ''}`}
                  disabled={loadingList}
                  onClick={() => void refreshList()}
                  title={loadingList ? t('app.mail.refreshing') : t('app.mail.refresh')}
                  type="button"
                >
                  <Icon name="loop" size={14} />
                </button>
              </div>
            </div>
          </header>

          {error && (
            <div className="mail-error-banner" role="alert">
              <span>{error}</span>
              <button className="mail-btn mail-btn--ghost" onClick={() => void refreshList()} type="button">
                {t('app.mail.retry')}
              </button>
            </div>
          )}

          <div className="mail-modal-body">
            {showUnifiedEmpty ? (
              <div className="mail-unified-empty">
                <span aria-hidden="true" className="mail-unified-empty-icon">
                  <Icon name="mail" size={22} />
                </span>
                <p className="mail-unified-empty-text">
                  {loadingList && mails.length === 0
                    ? t('app.mail.loading')
                    : filter === 'all'
                      ? t('app.mail.emptyAll')
                      : t('app.mail.emptyFiltered')}
                </p>
                {!loadingList && filter !== 'all' && mails.length > 0 && (
                  <button
                    className="mail-btn mail-btn--secondary"
                    onClick={() => setFilter('all')}
                    type="button"
                  >
                    {t('app.mail.showAllBtn')}
                  </button>
                )}
              </div>
            ) : (
              <div className={`mail-split-view${mobileShowDetail && activeMail ? ' is-mobile-detail' : ''}`}>
                <div className="mail-list-pane" role="list">
                  {visibleMails.length === 0 ? (
                    <div className="mail-list-empty-inline">
                      <span>{t('app.mail.emptyFiltered')}</span>
                      <button
                        className="mail-btn mail-btn--secondary"
                        onClick={() => setFilter('all')}
                        type="button"
                      >
                        {t('app.mail.showAllBtn')}
                      </button>
                    </div>
                  ) : (
                    visibleMails.map(item => (
                      <MailListRow
                        key={item.mailKey}
                        mail={item}
                        onSelect={handleSelectMail}
                        selected={item.mailKey === selectedMailKey}
                      />
                    ))
                  )}
                </div>

                <article className="mail-detail-pane">
                  {!activeMail ? (
                    <div className="mail-detail-placeholder">
                      <span aria-hidden="true" className="mail-placeholder-icon">
                        <Icon name="mail" size={20} />
                      </span>
                      <span>{t('app.mail.selectPrompt')}</span>
                    </div>
                  ) : (
                    <>
                      <div className="mail-detail-mobile-bar">
                        <button
                          className="mail-btn mail-btn--ghost mail-mobile-back-btn"
                          onClick={handleBackToList}
                          type="button"
                        >
                          ← {t('app.mail.backToList')}
                        </button>
                        <div className="mail-detail-mobile-badges">
                          {detailBadges}
                        </div>
                      </div>

                      <header className="mail-detail-head">
                        <div className="mail-detail-title-row">
                          <h3 className="mail-detail-title">{activeMail.title}</h3>
                          <div className="mail-detail-badges">
                            {detailBadges}
                          </div>
                        </div>
                        <div className="mail-detail-meta">
                          <span>{t('app.mail.senderLabel', { sender: activeMail.senderName })}</span>
                          <time dateTime={activeMail.createdAt}>
                            {t('app.mail.sentAtLabel', { time: formatMailTimestamp(activeMail.createdAt) })}
                          </time>
                        </div>
                      </header>

                      <div className="mail-detail-scroll" ref={detailScrollRef}>
                        <div className="mail-detail-body">
                          {loadingDetailKey === activeMail.mailKey && !selectedDetail ? (
                            <span className="mail-detail-loading">{t('app.mail.loadingDetail')}</span>
                          ) : selectedDetail ? (
                            <div className="mail-body-text">
                              <RichTemplateText
                                onNavigate={onClose}
                                template={selectedDetail.body || ''}
                              />
                            </div>
                          ) : (
                            <button
                              className="mail-btn mail-btn--secondary"
                              onClick={() => void selectMail(activeMail.mailKey)}
                              type="button"
                            >
                              {t('app.mail.retryDetail')}
                            </button>
                          )}
                        </div>

                        {activeMail.attachments.length > 0 && (
                          <section aria-label={t('app.mail.attachmentsAria')} className="mail-attachments-section">
                            <div className="mail-attachments-head">
                              <h4>{t('app.mail.attachmentsTitle', { count: activeMail.attachments.length })}</h4>
                              {activeMail.isClaimed ? (
                                <span className="mail-status-pill mail-status-pill--claimed">{t('app.mail.claimedBadge')}</span>
                              ) : (
                                <span className="mail-status-pill mail-status-pill--claimable">{t('app.mail.claimableBadge')}</span>
                              )}
                            </div>
                            <div className="mail-attachment-grid">
                              {activeMail.attachments.map(att => (
                                <MailAttachmentCard
                                  attachment={att}
                                  isClaimed={activeMail.isClaimed}
                                  key={att.itemId}
                                />
                              ))}
                            </div>
                          </section>
                        )}
                      </div>

                      <footer className="mail-detail-footer">
                        {activeHasUnclaimed && (
                          <span className="mail-delete-hint">{t('app.mail.deleteBlockedUnclaimed')}</span>
                        )}
                        <div className="mail-detail-footer-actions">
                          <button
                            className="mail-btn mail-btn--danger"
                            disabled={isBusy || activeHasUnclaimed}
                            onClick={() => void deleteOneMail(activeMail.mailKey)}
                            title={activeHasUnclaimed ? t('app.mail.deleteBlockedUnclaimed') : undefined}
                            type="button"
                          >
                            {actionBusyKey === `delete:${activeMail.mailKey}`
                              ? t('app.mail.deleting')
                              : t('app.mail.deleteBtn')}
                          </button>
                          {activeMail.hasAttachments && (
                            <button
                              className="mail-btn mail-btn--primary"
                              disabled={isBusy || activeMail.isClaimed}
                              onClick={() => void claimMail(activeMail.mailKey)}
                              type="button"
                            >
                              {actionBusyKey === `claim:${activeMail.mailKey}`
                                ? t('app.mail.claiming')
                                : activeMail.isClaimed
                                  ? t('app.mail.claimedBadge')
                                  : t('app.mail.claimBtn')}
                            </button>
                          )}
                        </div>
                      </footer>
                    </>
                  )}
                </article>
              </div>
            )}
          </div>
        </div>
      </Modal>

      <ConfirmDialog
        cancelLabel={t('app.mail.cancelBtn')}
        confirmLabel={t('app.mail.deleteReadConfirmBtn')}
        danger
        description={t('app.mail.deleteReadConfirmDesc')}
        onClose={() => setConfirmDeleteReadOpen(false)}
        onConfirm={() => {
          setConfirmDeleteReadOpen(false)
          void deleteAllReadMails()
        }}
        open={confirmDeleteReadOpen}
        title={t('app.mail.deleteReadConfirmTitle')}
      />
    </>
  )
})
