import { memo, useEffect, useMemo, useState } from 'react'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { findEquipmentInCatalog, findItemInCatalog, resolveCatalogItemMetadata } from '../game/ids'
import { filterMailItems, useMailStore, type MailFilter } from '../game/mailStore'
import { useT, type TFunction } from '../i18n'
import type { MailAttachmentDto, MailListItemDto } from '../network/protocol'
import { useUiStore } from '../uiStore'
import { ConfirmDialog } from './ConfirmDialog'
import { RichTemplateText } from './EntityReference'
import { Icon } from './Icon'
import { ItemIcon, ItemTile, ItemTooltipTarget } from './ItemDisplay'

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

/** 邮件附件单格：复用系统统一 ItemTile 与 ItemTooltipTarget，提供完整的品质发光框与悬浮详情卡。 */
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
      <div className="mail-attachment-caption">
        <span className="mail-attachment-name" title={meta.label}>{meta.label}</span>
        <span className="mail-attachment-qty">×{formattedQty}</span>
      </div>
      {isClaimed && <span className="mail-attachment-claimed-tag">{t('app.mail.claimedBadge')}</span>}
    </div>
  )
})

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
  const previewAttachments = mail.attachments.slice(0, 3)
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
        <div className="mail-list-card-badges">
          {!mail.isRead && <span aria-label={t('app.mail.unreadBadge')} className="mail-unread-dot" />}
          <span className={`mail-kind-pill mail-kind-pill--${mail.kind}`}>
            {mail.kind === 'broadcast' ? t('app.mail.kindBroadcast') : t('app.mail.kindPersonal')}
          </span>
          {unclaimed ? (
            <span className="mail-status-pill mail-status-pill--claimable">{t('app.mail.claimableBadge')}</span>
          ) : mail.hasAttachments ? (
            <span className="mail-status-pill mail-status-pill--claimed">{t('app.mail.claimedBadge')}</span>
          ) : null}
        </div>
        <time className="mail-list-time" dateTime={mail.createdAt}>
          {formatMailTimestamp(mail.createdAt)}
        </time>
      </div>
      <div className="mail-list-title">{mail.title}</div>
      <div className="mail-list-card-bottom">
        <span className="mail-list-sender">{mail.senderName}</span>
        {previewAttachments.length > 0 && (
          <div className="mail-list-attach-preview" aria-label={t('app.mail.attachmentsAria')}>
            {previewAttachments.map(att => {
              const meta = resolveCatalogItemMetadata(att.itemId, {
                itemDefinition: findItemInCatalog(itemsCatalog, att.itemId) ?? null,
                equipmentDefinition: findEquipmentInCatalog(equipmentCatalog, att.itemId) ?? null,
                t,
              })
              return (
                <span className="mail-list-attach-chip" key={`${att.itemId}`} title={`${meta.label} ×${att.quantity}`}>
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
    </button>
  )
})

/**
 * 传书邮件主界面（替换 .main-content 呈现）：
 * - 桌面双栏并列（列表 + 正文/附件），竖屏 `<=960px` 单栏切换；
 * - 列表按全服共享广播与玩家私信双轨聚合（列表投影不含正文，点选即按需拉取）；
 * - 强制未领取附件删除保护；一键领取、删除已读均由服务端原子事务保障。
 */
export const MailPage = memo(function MailPage(): React.JSX.Element {
  const t = useT()
  const setMailOpen = useUiStore(state => state.setMailOpen)

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
  const claimMail = useMailStore(state => state.claimMail)
  const claimAllMails = useMailStore(state => state.claimAllMails)
  const deleteOneMail = useMailStore(state => state.deleteOneMail)
  const deleteAllReadMails = useMailStore(state => state.deleteAllReadMails)

  const [mobileShowDetail, setMobileShowDetail] = useState(false)
  const [confirmDeleteReadOpen, setConfirmDeleteReadOpen] = useState(false)

  useEffect(() => {
    void useMailStore.getState().refreshList()
  }, [])

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

  const hasClaimable = summary.claimableCount > 0 || mails.some(m => m.hasAttachments && !m.isClaimed)
  const hasDeletableRead = mails.some(m => m.isRead && (!m.hasAttachments || m.isClaimed))
  const isBusy = actionBusyKey !== null

  const activeHasUnclaimed = Boolean(activeMail && activeMail.hasAttachments && !activeMail.isClaimed)
  const activeIsBroadcast = activeMail?.kind === 'broadcast'

  const handleSelectMail = (mailKey: string) => {
    setMobileShowDetail(true)
    void selectMail(mailKey)
  }

  return (
    <section aria-label={t('app.mail.title')} className="mail-page">
      <header className="mail-page-header">
        <div className="mail-title-group">
          <div className="mail-title-row">
            <button
              aria-label={t('app.mail.backToGameAria')}
              className="mail-btn mail-btn--ghost mail-back-game-btn"
              onClick={() => setMailOpen(false)}
              type="button"
            >
              ← {t('app.mail.backToGame')}
            </button>
            <h2 className="mail-title">
              <Icon name="mail" />
              <span>{t('app.mail.title')}</span>
            </h2>
          </div>
          <p className="mail-subtitle">{t('app.mail.subtitle')}</p>
        </div>

        <div className="mail-header-actions">
          <span className="mail-summary-pill">
            {t('app.mail.summaryCounts', {
              total: mails.length,
              unread: summary.unreadCount,
              claimable: summary.claimableCount,
            })}
          </span>
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
            className="mail-btn mail-btn--ghost"
            disabled={loadingList}
            onClick={() => void refreshList()}
            type="button"
          >
            {loadingList ? t('app.mail.refreshing') : t('app.mail.refresh')}
          </button>
        </div>
      </header>

      <div aria-label={t('app.mail.filterAria')} className="mail-filter-bar" role="tablist">
        {(['all', 'unread', 'claimable'] as readonly MailFilter[]).map(tab => (
          <button
            aria-selected={filter === tab}
            className={`mail-filter-tab${filter === tab ? ' is-active' : ''}`}
            key={tab}
            onClick={() => setFilter(tab)}
            role="tab"
            type="button"
          >
            {t(`app.mail.filter.${tab}`)}
          </button>
        ))}
      </div>

      {error && (
        <div className="mail-error-banner" role="alert">
          <span>{error}</span>
          <button className="mail-btn mail-btn--ghost" onClick={() => void refreshList()} type="button">
            {t('app.mail.retry')}
          </button>
        </div>
      )}

      <div className={`mail-body${mobileShowDetail && activeMail ? ' mail-body--mobile-detail' : ''}`}>
        <div className="mail-list-pane" role="list">
          {loadingList && mails.length === 0 ? (
            <div className="mail-empty-state">{t('app.mail.loading')}</div>
          ) : visibleMails.length === 0 ? (
            <div className="mail-empty-state">
              {filter === 'all' ? t('app.mail.emptyAll') : t('app.mail.emptyFiltered')}
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
            <div className="mail-detail-placeholder">{t('app.mail.selectPrompt')}</div>
          ) : (
            <>
              <div className="mail-detail-mobile-bar">
                <button
                  className="mail-btn mail-btn--ghost"
                  onClick={() => setMobileShowDetail(false)}
                  type="button"
                >
                  ← {t('app.mail.backToList')}
                </button>
              </div>

              <header className="mail-detail-head">
                <div className="mail-detail-badges">
                  <span className={`mail-kind-pill mail-kind-pill--${activeMail.kind}`}>
                    {activeIsBroadcast ? t('app.mail.kindBroadcast') : t('app.mail.kindPersonal')}
                  </span>
                  {activeMail.kind === 'broadcast' && (
                    <span className="mail-scope-pill">
                      {activeMail.onlyExistingPlayers ? t('app.mail.scopeExisting') : t('app.mail.scopeAll')}
                    </span>
                  )}
                  <span className="mail-expiry-pill">{formatExpiryLabel(activeMail.expiresAt, t)}</span>
                </div>
                <h3 className="mail-detail-title">{activeMail.title}</h3>
                <div className="mail-detail-meta">
                  <span>{t('app.mail.senderLabel', { sender: activeMail.senderName })}</span>
                  <time dateTime={activeMail.createdAt}>
                    {t('app.mail.sentAtLabel', { time: formatMailTimestamp(activeMail.createdAt) })}
                  </time>
                </div>
              </header>

              <div className="mail-detail-scroll">
                <div className="mail-detail-body">
                  {loadingDetailKey === activeMail.mailKey && !selectedDetail ? (
                    <span className="mail-detail-loading">{t('app.mail.loadingDetail')}</span>
                  ) : selectedDetail ? (
                    <div className="mail-body-text">
                      <RichTemplateText
                        onNavigate={() => setMailOpen(false)}
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
                {activeHasUnclaimed && (
                  <span className="mail-delete-hint">{t('app.mail.deleteBlockedUnclaimed')}</span>
                )}
              </footer>
            </>
          )}
        </article>
      </div>

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
    </section>
  )
})
