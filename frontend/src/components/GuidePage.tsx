import '../styles/guide.css'
import React, { memo, useCallback, useEffect, useMemo, useState } from 'react'
import {
  createGuidePost,
  deleteGuidePost,
  fetchGuideDetail,
  fetchGuideList,
  setGuidePostLike,
  tipGuidePost,
  unlockGuidePost,
  updateGuidePost,
} from '../network/apiClient'
import type {
  GuidePermissionsDto,
  GuidePostDetailDto,
  GuidePostSummaryDto,
  GuidePostUpsertPayload,
} from '../network/protocol'
import { resolvePlayerActorId } from '../game/avatarStore'
import { buildGuideChatMarkup } from '../game/chatMarkup'
import { shareToChat } from '../game/chatShare'
import { calculateRealmInfo } from '../game/combatConstants'
import { formatNumber } from '../game/format'
import { GUIDE_MAX_PRICE_STONES } from '../game/guideMarkup'
import { useGameStore } from '../game/gameStore'
import { useSocialStore } from '../game/socialStore'
import { useUiStore } from '../uiStore'
import { useT } from '../i18n'
import { CharacterAvatar } from './CharacterAvatar'
import { ConfirmDialog } from './ConfirmDialog'
import { clearGuideLocalDraft, GuideEditorModal } from './GuideEditorModal'
import { GuideRichContent } from './GuideRichContent'
import { Icon } from './Icon'
import { ItemIcon } from './ItemDisplay'
import { LeaderboardRankBadge } from './LeaderboardGlyph'
import { RealmBadge } from './RealmBadge'

const TIP_PRESETS: readonly number[] = [10, 50, 100, 520, 1000]

function formatShortTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getMonth() + 1}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

/** 单张攻略列表卡片（memo 隔离，高频状态更新不丢列表滚动条） */
const GuidePostCard = memo(function GuidePostCard({
  item,
  isSelfAuthor,
  onOpenDetail,
  onInspectAuthor,
}: {
  item: GuidePostSummaryDto
  isSelfAuthor: boolean
  onOpenDetail: (postId: string) => void
  onInspectAuthor: (authorId: string) => void
}): React.JSX.Element {
  const t = useT()
  const realmInfo = useMemo(() => calculateRealmInfo(0, item.authorRealmLevel), [item.authorRealmLevel])
  const isPaid = item.unlockPrice > 0

  return (
    <article
      className={`guide-card${isPaid && !item.isUnlocked ? ' is-locked' : ''}`}
      onClick={() => onOpenDetail(item.postId)}
      onKeyDown={e => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onOpenDetail(item.postId)
        }
      }}
      role="button"
      tabIndex={0}
    >
      <div className="guide-card-top">
        <h4 className="guide-card-title">{item.title}</h4>
        <div className="guide-card-badges">
          {!isPaid ? (
            <span className="guide-price-tag is-free">{t('social.guide.badgeFree')}</span>
          ) : item.isUnlocked || isSelfAuthor ? (
            <span className="guide-price-tag is-unlocked">
              <ItemIcon itemId="currency.spirit-stone" />
              <span>{formatNumber(item.unlockPrice)} · {t('social.guide.badgeUnlocked')}</span>
            </span>
          ) : (
            <span className="guide-price-tag is-locked">
              <Icon name="lock" size={11} />
              <ItemIcon itemId="currency.spirit-stone" />
              <span>{formatNumber(item.unlockPrice)}</span>
            </span>
          )}
        </div>
      </div>

      {item.excerpt ? (
        <p className="guide-card-excerpt">{item.excerpt}</p>
      ) : isPaid && !item.isUnlocked ? (
        <p className="guide-card-excerpt is-locked-hint">{t('social.guide.lockedNoSummaryHint')}</p>
      ) : null}

      <div className="guide-card-bottom">
        <div
          className="guide-author-chip"
          onClick={e => {
            e.stopPropagation()
            onInspectAuthor(item.authorId)
          }}
          onKeyDown={e => {
            if (e.key === 'Enter' || e.key === ' ') {
              e.stopPropagation()
              e.preventDefault()
              onInspectAuthor(item.authorId)
            }
          }}
          role="button"
          tabIndex={0}
          title={t('social.card.openCardHint')}
        >
          <span className="guide-author-avatar-box">
            <CharacterAvatar
              actorId={resolvePlayerActorId(item.authorAvatar)}
              className="guide-author-avatar"
              name={item.authorName}
              role="player"
            />
          </span>
          <span className="guide-author-name">
            {item.authorZongmenPrefix && <em className="guide-zongmen-prefix">[{item.authorZongmenPrefix}]</em>}
            {item.authorName}
          </span>
          <span className="guide-author-minor">{realmInfo.minorName}</span>
          {item.authorTopRank && (
            <LeaderboardRankBadge badge={item.authorTopRank} variant="guide" />
          )}
        </div>

        <div className="guide-card-stats">
          <span title={t('social.guide.statViews')}>
            {t('social.guide.viewsShort', { count: formatNumber(item.viewCount) })}
          </span>
          <span className={item.likedByMe ? 'is-liked' : ''} title={t('social.guide.statLikes')}>
            ♥ {formatNumber(item.likeCount)}
          </span>
          <span className="guide-stat-reward" title={t('social.guide.statRewards')}>
            <ItemIcon itemId="currency.spirit-stone" />
            <span>{formatNumber(item.totalRewardStones)}</span>
          </span>
          <span className="guide-card-time">{formatShortTime(item.createdAt)}</span>
        </div>
      </div>
    </article>
  )
})

/**
 * 道友 · 攻略板块主页面 (GuidePage)
 *
 * 承载热度/打赏双排序列表、排行榜上榜发帖准入、详情阅读、付费解锁、自由打赏及独立编辑器唤起。
 */
export const GuidePage = memo(function GuidePage(): React.JSX.Element {
  const t = useT()
  const selfPlayerId = useGameStore(state => state.playerId)
  const inventory = useGameStore(state => state.inventory)
  const requestPlayerCard = useSocialStore(state => state.requestPlayerCard)
  const setActiveTab = useUiStore(state => state.setActiveTab)
  const guideFocusPostId = useUiStore(state => state.guideFocusPostId)
  const setGuideFocusPostId = useUiStore(state => state.setGuideFocusPostId)

  const [sort, setSort] = useState<'hot' | 'reward'>('hot')
  const [mineOnly, setMineOnly] = useState(false)
  const [items, setItems] = useState<GuidePostSummaryDto[]>([])
  const [permissions, setPermissions] = useState<GuidePermissionsDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [refreshTick, setRefreshTick] = useState(0)

  const [selectedPostId, setSelectedPostId] = useState<string | null>(null)
  const [detail, setDetail] = useState<GuidePostDetailDto | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<string | null>(null)

  const [editorOpen, setEditorOpen] = useState(false)
  const [editingPost, setEditingPost] = useState<GuidePostDetailDto | null>(null)
  const [editorSubmitting, setEditorSubmitting] = useState(false)

  const [unlockConfirmOpen, setUnlockConfirmOpen] = useState(false)
  const [unlocking, setUnlocking] = useState(false)
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [liking, setLiking] = useState(false)

  const [tipInput, setTipInput] = useState('50')
  const [pendingTipAmount, setPendingTipAmount] = useState<number | null>(null)
  const [tipping, setTipping] = useState(false)

  const mySpiritStones = useMemo(
    () => Math.floor(inventory.find(entry => entry.itemId === 'currency.spirit-stone' && !entry.variant)?.amount ?? 0),
    [inventory],
  )

  // 拉取攻略列表
  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setLoadError(null)

    fetchGuideList(sort, mineOnly, 50, 0, controller.signal)
      .then(res => {
        if (controller.signal.aborted) return
        setItems(res.items)
        setPermissions(res.permissions)
        setLoading(false)
      })
      .catch(err => {
        if (controller.signal.aborted) return
        setLoadError(err instanceof Error ? err.message : t('social.guide.loadFailed'))
        setLoading(false)
      })

    return () => controller.abort()
  }, [sort, mineOnly, refreshTick, t])

  // 拉取选中攻略详情
  useEffect(() => {
    if (!selectedPostId) {
      setDetail(null)
      setDetailError(null)
      return undefined
    }
    const controller = new AbortController()
    setDetailLoading(true)
    setDetailError(null)

    fetchGuideDetail(selectedPostId, controller.signal)
      .then(dto => {
        if (controller.signal.aborted) return
        setDetail(dto)
        setDetailLoading(false)
        setItems(prev => prev.map(p => (p.postId === dto.postId
          ? {
              ...p,
              viewCount: dto.viewCount,
              likeCount: dto.likeCount,
              tipperCount: dto.tipperCount,
              totalRewardStones: dto.totalRewardStones,
              isUnlocked: dto.isUnlocked,
              likedByMe: dto.likedByMe,
            }
          : p)))
      })
      .catch(err => {
        if (controller.signal.aborted) return
        setDetailError(err instanceof Error ? err.message : t('social.guide.loadFailed'))
        setDetailLoading(false)
      })

    return () => controller.abort()
  }, [selectedPostId, t])

  // 响应外部（如传音攻略胶囊）直达指定攻略详情
  useEffect(() => {
    if (!guideFocusPostId) return
    setSelectedPostId(guideFocusPostId)
    setGuideFocusPostId(null)
  }, [guideFocusPostId, setGuideFocusPostId])

  const handleShareGuide = useCallback((post: Pick<GuidePostDetailDto, 'postId' | 'title' | 'unlockPrice'>) => {
    shareToChat(buildGuideChatMarkup({
      postId: post.postId,
      title: post.title,
      unlockPrice: post.unlockPrice,
    }))
  }, [])

  const handleOpenCreate = useCallback(() => {
    setEditingPost(null)
    setEditorOpen(true)
  }, [])

  const handleOpenEdit = useCallback(() => {
    if (!detail) return
    setEditingPost(detail)
    setEditorOpen(true)
  }, [detail])

  const handleEditorSubmit = useCallback(async (
    payload: GuidePostUpsertPayload,
    options?: { shareAfterSave?: boolean },
  ) => {
    setEditorSubmitting(true)
    try {
      let savedPost: GuidePostDetailDto
      if (editingPost) {
        savedPost = await updateGuidePost(editingPost.postId, payload)
        setDetail(savedPost)
        useGameStore.getState().setNotice(t('social.guide.noticeUpdated'))
      } else {
        savedPost = await createGuidePost(payload)
        clearGuideLocalDraft(selfPlayerId)
        setSelectedPostId(savedPost.postId)
        setDetail(savedPost)
        useGameStore.getState().setNotice(t('social.guide.noticePublished'))
      }
      setEditorOpen(false)
      setEditingPost(null)
      setRefreshTick(n => n + 1)
      if (options?.shareAfterSave) {
        // 等编辑 Modal 卸载完成并收尾其焦点还原后，再聚焦传音输入框写入草稿，避免焦点被抢回
        window.requestAnimationFrame(() => handleShareGuide(savedPost))
      }
    } finally {
      setEditorSubmitting(false)
    }
  }, [editingPost, handleShareGuide, selfPlayerId, t])

  const handleToggleLike = useCallback(async () => {
    if (!detail || liking) return
    setLiking(true)
    try {
      const res = await setGuidePostLike(detail.postId, !detail.likedByMe)
      setDetail(prev => (prev && prev.postId === res.postId
        ? { ...prev, likedByMe: res.liked, likeCount: res.likeCount, hotScore: res.hotScore }
        : prev))
      setItems(prev => prev.map(p => (p.postId === res.postId
        ? { ...p, likedByMe: res.liked, likeCount: res.likeCount, hotScore: res.hotScore }
        : p)))
    } catch (err) {
      useGameStore.getState().setNotice(err instanceof Error ? err.message : t('social.guide.actionFailed'))
    } finally {
      setLiking(false)
    }
  }, [detail, liking, t])

  const handleConfirmUnlock = useCallback(async () => {
    if (!detail || unlocking) return
    setUnlocking(true)
    try {
      const unlocked = await unlockGuidePost(detail.postId, detail.unlockPrice)
      setUnlockConfirmOpen(false)
      setDetail(unlocked)
      setItems(prev => prev.map(p => (p.postId === unlocked.postId
        ? {
            ...p,
            isUnlocked: true,
            viewCount: unlocked.viewCount,
            tipperCount: unlocked.tipperCount,
            totalRewardStones: unlocked.totalRewardStones,
          }
        : p)))
      useGameStore.getState().setNotice(t('social.guide.noticeUnlocked'))
    } catch (err) {
      setUnlockConfirmOpen(false)
      useGameStore.getState().setNotice(err instanceof Error ? err.message : t('social.guide.actionFailed'))
      // 若价格变动则重拉详情刷新展示价格
      setSelectedPostId(id => (id ? `${id}` : null))
    } finally {
      setUnlocking(false)
    }
  }, [detail, t, unlocking])

  const handleRequestTip = useCallback(() => {
    const parsed = Number(tipInput)
    if (!Number.isFinite(parsed) || !Number.isInteger(parsed) || parsed < 1 || parsed > GUIDE_MAX_PRICE_STONES) {
      useGameStore.getState().setNotice(t('social.guide.errorTipRange', { max: GUIDE_MAX_PRICE_STONES }))
      return
    }
    if (mySpiritStones < parsed) {
      useGameStore.getState().setNotice(t('social.guide.insufficientStones'))
      return
    }
    setPendingTipAmount(parsed)
  }, [mySpiritStones, t, tipInput])

  const handleConfirmTip = useCallback(async () => {
    if (!detail || pendingTipAmount === null || tipping) return
    setTipping(true)
    try {
      const res = await tipGuidePost(detail.postId, pendingTipAmount)
      setPendingTipAmount(null)
      setDetail(prev => (prev && prev.postId === res.postId
        ? {
            ...prev,
            myTippedStones: res.myTippedStones,
            tipperCount: res.tipperCount,
            totalRewardStones: res.totalRewardStones,
            hotScore: res.hotScore,
          }
        : prev))
      setItems(prev => prev.map(p => (p.postId === res.postId
        ? {
            ...p,
            tipperCount: res.tipperCount,
            totalRewardStones: res.totalRewardStones,
            hotScore: res.hotScore,
          }
        : p)))
      useGameStore.getState().setNotice(t('social.guide.noticeTipped', { amount: res.paidStones }))
    } catch (err) {
      setPendingTipAmount(null)
      useGameStore.getState().setNotice(err instanceof Error ? err.message : t('social.guide.actionFailed'))
    } finally {
      setTipping(false)
    }
  }, [detail, pendingTipAmount, t, tipping])

  const handleConfirmDelete = useCallback(async () => {
    if (!detail || deleting) return
    setDeleting(true)
    try {
      await deleteGuidePost(detail.postId)
      setDeleteConfirmOpen(false)
      setSelectedPostId(null)
      setDetail(null)
      setRefreshTick(n => n + 1)
      useGameStore.getState().setNotice(t('social.guide.noticeDeleted'))
    } catch (err) {
      setDeleteConfirmOpen(false)
      useGameStore.getState().setNotice(err instanceof Error ? err.message : t('social.guide.actionFailed'))
    } finally {
      setDeleting(false)
    }
  }, [deleting, detail, t])

  const detailRealmInfo = useMemo(
    () => (detail ? calculateRealmInfo(0, detail.authorRealmLevel) : null),
    [detail],
  )

  const myTopBadge = permissions?.rankedBoards?.[0] ?? null
  const isDetailMine = detail !== null && selfPlayerId !== null && detail.authorId === selfPlayerId

  return (
    <div className="guide-page">
      {/* 列表视图 */}
      {selectedPostId === null ? (
        <>
          <div className="guide-toolbar-card">
            <div className="guide-toolbar-left">
              <div aria-label={t('social.guide.sortAria')} className="insight-sub-tabs" role="tablist">
                <button
                  aria-selected={sort === 'hot'}
                  className={`insight-sub-tab-btn${sort === 'hot' ? ' active' : ''}`}
                  onClick={() => setSort('hot')}
                  role="tab"
                  type="button"
                >
                  {t('social.guide.sortHot')}
                </button>
                <button
                  aria-selected={sort === 'reward'}
                  className={`insight-sub-tab-btn${sort === 'reward' ? ' active' : ''}`}
                  onClick={() => setSort('reward')}
                  role="tab"
                  type="button"
                >
                  {t('social.guide.sortReward')}
                </button>
              </div>

              <div className="insight-sub-tabs">
                <button
                  className={`insight-sub-tab-btn${!mineOnly ? ' active' : ''}`}
                  onClick={() => setMineOnly(false)}
                  type="button"
                >
                  {t('social.guide.filterAll')}
                </button>
                <button
                  className={`insight-sub-tab-btn${mineOnly ? ' active' : ''}`}
                  onClick={() => setMineOnly(true)}
                  type="button"
                >
                  {t('social.guide.filterMine')}
                </button>
              </div>
            </div>

            <div className="guide-toolbar-right">
              {myTopBadge && (
                <LeaderboardRankBadge badge={myTopBadge} variant="guide" />
              )}
              <button
                className="inline-modify-btn"
                onClick={() => setRefreshTick(n => n + 1)}
                type="button"
              >
                {t('social.zongmen.refresh')}
              </button>
              {permissions?.canCreate ? (
                <button className="primary-btn guide-write-btn" onClick={handleOpenCreate} type="button">
                  {t('social.guide.writeBtn')}
                </button>
              ) : permissions?.isRanked ? (
                <button className="inline-modify-btn" disabled type="button">
                  {t('social.guide.dailyUsedBtn', {
                    used: permissions.todayPublishedCount,
                    limit: permissions.dailyLimit,
                  })}
                </button>
              ) : (
                <button
                  className="inline-modify-btn guide-go-leaderboard-btn"
                  onClick={() => setActiveTab('leaderboard')}
                  title={t('social.guide.rankRuleHint')}
                  type="button"
                >
                  <Icon name="leaderboard" size={13} />
                  <span>{t('social.guide.needRankBtn')}</span>
                </button>
              )}
            </div>
          </div>

          {permissions && !permissions.isRanked && (
            <p className="guide-permission-banner">{t('social.guide.rankRuleHint')}</p>
          )}

          {loading ? (
            <div className="party-card"><p className="party-card-desc">{t('social.guide.loading')}</p></div>
          ) : loadError ? (
            <div className="party-card">
              <p className="party-card-desc">{loadError}</p>
              <button className="inline-modify-btn" onClick={() => setRefreshTick(n => n + 1)} type="button">
                {t('app.mail.retry')}
              </button>
            </div>
          ) : items.length === 0 ? (
            <div className="party-card guide-empty-card">
              <p className="party-card-desc">
                {mineOnly ? t('social.guide.emptyMine') : t('social.guide.emptyAll')}
              </p>
            </div>
          ) : (
            <div className="guide-list">
              {items.map(item => (
                <GuidePostCard
                  isSelfAuthor={selfPlayerId !== null && item.authorId === selfPlayerId}
                  item={item}
                  key={item.postId}
                  onInspectAuthor={requestPlayerCard}
                  onOpenDetail={setSelectedPostId}
                />
              ))}
            </div>
          )}
        </>
      ) : (
        /* 攻略详情视图 */
        <div className="guide-detail-shell">
          <div className="guide-detail-topbar">
            <button
              className="inline-modify-btn"
              onClick={() => setSelectedPostId(null)}
              type="button"
            >
              ← {t('social.guide.backToList')}
            </button>

            <div className="guide-detail-topbar-actions">
              {detail && (
                <button
                  className="guide-share-btn"
                  onClick={() => handleShareGuide(detail)}
                  title={t('combat.chat.shareToChat')}
                  type="button"
                >
                  <Icon name="chat" size={13} />
                  <span>{t('combat.chat.shareToChat')}</span>
                </button>
              )}

              {isDetailMine && detail && (
                <div className="guide-owner-actions">
                  <button className="inline-modify-btn" onClick={handleOpenEdit} type="button">
                    {t('social.guide.editBtn')}
                  </button>
                  <button className="danger inline-modify-btn" onClick={() => setDeleteConfirmOpen(true)} type="button">
                    {t('social.guide.deleteBtn')}
                  </button>
                </div>
              )}
            </div>
          </div>

          {detailLoading ? (
            <div className="party-card"><p className="party-card-desc">{t('social.guide.loading')}</p></div>
          ) : detailError || !detail ? (
            <div className="party-card">
              <p className="party-card-desc">{detailError ?? t('social.guide.loadFailed')}</p>
            </div>
          ) : (
            <article className="guide-detail-card">
              <header className="guide-detail-header">
                <h2 className="guide-detail-title">{detail.title}</h2>
                <div className="guide-detail-author-bar">
                  <div
                    className="guide-author-chip is-interactive"
                    onClick={() => requestPlayerCard(detail.authorId)}
                    onKeyDown={e => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault()
                        requestPlayerCard(detail.authorId)
                      }
                    }}
                    role="button"
                    tabIndex={0}
                  >
                    <span className="guide-author-avatar-box guide-author-avatar-box--lg">
                      <CharacterAvatar
                        actorId={resolvePlayerActorId(detail.authorAvatar)}
                        className="guide-author-avatar guide-author-avatar--lg"
                        name={detail.authorName}
                        role="player"
                      />
                    </span>
                    <div className="guide-detail-author-meta">
                      <div className="guide-detail-author-line">
                        {detail.authorZongmenPrefix && (
                          <em className="guide-zongmen-prefix">[{detail.authorZongmenPrefix}]</em>
                        )}
                        <strong>{detail.authorName}</strong>
                        {detail.authorTopRank && (
                          <LeaderboardRankBadge badge={detail.authorTopRank} variant="guide" />
                        )}
                      </div>
                      <span className="guide-detail-timestamps">
                        {t('social.guide.publishedAt', { time: formatShortTime(detail.createdAt) })}
                      </span>
                    </div>
                  </div>
                  {detailRealmInfo && <RealmBadge realmInfo={detailRealmInfo} />}
                </div>
              </header>

              {detail.summary && (
                <p className="guide-detail-summary">{detail.summary}</p>
              )}

              {detail.locked ? (
                <section className="guide-paywall-box">
                  <div className="guide-paywall-title">
                    <Icon name="lock" size={18} />
                    <strong>{t('social.guide.paywallTitle')}</strong>
                  </div>
                  <p className="guide-paywall-desc">{t('social.guide.paywallDesc')}</p>
                  <div className="guide-paywall-cost-row">
                    <span>{t('social.guide.unlockNeedLabel')}</span>
                    <strong className="tone-gold guide-stone-inline">
                      <ItemIcon itemId="currency.spirit-stone" />
                      <span>{formatNumber(detail.unlockPrice)}</span>
                    </strong>
                    <span className="guide-wallet-hint">
                      ({t('social.guide.myStonesLabel', { count: formatNumber(mySpiritStones) })})
                    </span>
                  </div>
                  <button
                    className="primary-btn guide-unlock-btn"
                    disabled={unlocking || mySpiritStones < detail.unlockPrice}
                    onClick={() => setUnlockConfirmOpen(true)}
                    type="button"
                  >
                    <ItemIcon itemId="currency.spirit-stone" />
                    <span>
                      {mySpiritStones < detail.unlockPrice
                        ? t('social.guide.insufficientStones')
                        : t('social.guide.unlockPayBtn', { price: formatNumber(detail.unlockPrice) })}
                    </span>
                  </button>
                </section>
              ) : (
                <div className="guide-detail-body">
                  <GuideRichContent body={detail.body ?? ''} />
                </div>
              )}

              {/* 底部点赞与打赏栏 */}
              <footer className="guide-detail-footer">
                <div className="guide-engagement-bar">
                  <button
                    aria-pressed={detail.likedByMe}
                    className={`guide-like-btn${detail.likedByMe ? ' is-liked' : ''}`}
                    disabled={liking}
                    onClick={handleToggleLike}
                    type="button"
                  >
                    <span>♥</span>
                    <span>{detail.likedByMe ? t('social.guide.likedBtn') : t('social.guide.likeBtn')}</span>
                    <strong>{formatNumber(detail.likeCount)}</strong>
                  </button>

                  <div className="guide-reward-summary">
                    <span>{t('social.guide.viewsShort', { count: formatNumber(detail.viewCount) })}</span>
                    <span>·</span>
                    <span>{t('social.guide.tippersCount', { count: formatNumber(detail.tipperCount) })}</span>
                    <span>·</span>
                    <span className="guide-stone-inline tone-gold">
                      <ItemIcon itemId="currency.spirit-stone" />
                      <strong>{formatNumber(detail.totalRewardStones)}</strong>
                    </span>
                  </div>
                </div>

                {!isDetailMine && !detail.locked && (
                  <div className="guide-tip-box">
                    <div className="guide-tip-header">
                      <strong>{t('social.guide.tipBoxTitle')}</strong>
                      <span className="guide-wallet-hint">
                        {t('social.guide.myStonesLabel', { count: formatNumber(mySpiritStones) })}
                        {detail.myTippedStones > 0 && ` · ${t('social.guide.myTippedLabel', { count: formatNumber(detail.myTippedStones) })}`}
                      </span>
                    </div>
                    <div className="guide-tip-controls">
                      <div className="guide-price-presets">
                        {TIP_PRESETS.map(preset => (
                          <button
                            className={Number(tipInput) === preset ? 'active' : ''}
                            key={preset}
                            onClick={() => setTipInput(String(preset))}
                            type="button"
                          >
                            {preset}
                          </button>
                        ))}
                      </div>
                      <div className="guide-tip-input-group">
                        <ItemIcon itemId="currency.spirit-stone" />
                        <input
                          aria-label={t('social.guide.tipAmountAria')}
                          max={GUIDE_MAX_PRICE_STONES}
                          min={1}
                          onChange={e => setTipInput(e.target.value)}
                          step={1}
                          type="number"
                          value={tipInput}
                        />
                        <button
                          className="primary-btn"
                          disabled={tipping}
                          onClick={handleRequestTip}
                          type="button"
                        >
                          {t('social.guide.sendTipBtn')}
                        </button>
                      </div>
                    </div>
                  </div>
                )}
              </footer>
            </article>
          )}
        </div>
      )}

      {/* 独立攻略编辑器 */}
      <GuideEditorModal
        editingPost={editingPost}
        onClose={() => {
          if (!editorSubmitting) {
            setEditorOpen(false)
            setEditingPost(null)
          }
        }}
        onSubmit={handleEditorSubmit}
        open={editorOpen}
        submitting={editorSubmitting}
      />

      {/* 付费解锁二次确认 */}
      <ConfirmDialog
        confirmLabel={t('social.guide.confirmUnlockBtn')}
        description={t('social.guide.confirmUnlockDesc', {
          price: formatNumber(detail?.unlockPrice ?? 0),
          title: detail?.title ?? '',
        })}
        onClose={() => setUnlockConfirmOpen(false)}
        onConfirm={() => { void handleConfirmUnlock() }}
        open={unlockConfirmOpen}
        title={t('social.guide.confirmUnlockTitle')}
      />

      {/* 打赏二次确认 */}
      <ConfirmDialog
        confirmLabel={t('social.guide.sendTipBtn')}
        description={t('social.guide.confirmTipDesc', {
          amount: formatNumber(pendingTipAmount ?? 0),
          author: detail?.authorName ?? '',
        })}
        onClose={() => setPendingTipAmount(null)}
        onConfirm={() => { void handleConfirmTip() }}
        open={pendingTipAmount !== null}
        title={t('social.guide.confirmTipTitle')}
      />

      {/* 删除二次确认 */}
      <ConfirmDialog
        confirmLabel={t('social.guide.deleteBtn')}
        danger
        description={t('social.guide.confirmDeleteDesc')}
        onClose={() => setDeleteConfirmOpen(false)}
        onConfirm={() => { void handleConfirmDelete() }}
        open={deleteConfirmOpen}
        title={t('social.guide.confirmDeleteTitle')}
      />
    </div>
  )
})
