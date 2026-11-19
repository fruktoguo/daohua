import React, { memo, useEffect, useState } from 'react'
import {
  fetchLeaderboardBoard,
  fetchLeaderboardIndex,
  LeaderboardApiError,
} from '../network/apiClient'
import type {
  LeaderboardEntryDto,
  LeaderboardIndexDto,
  LeaderboardMyRankDto,
  LeaderboardSnapshotDto,
} from '../network/protocol'
import { useGameStore } from '../game/gameStore'
import { useSocialStore } from '../game/socialStore'
import { resolvePlayerActorId } from '../game/avatarStore'
import { calculateRealmInfo, TECHNIQUE_IDS } from '../game/combatConstants'
import { formatNumber } from '../game/format'
import {
  formatLeaderboardTimestamp,
  resolveBoardId,
  type LeaderboardCategory,
} from '../game/leaderboardHelpers'
import { useT } from '../i18n'
import { CharacterAvatar } from './CharacterAvatar'
import { Icon, type IconName } from './Icon'
import { OptionToggle } from './OptionToggle'
import { RealmBadge } from './RealmBadge'
import { UnitNumber } from './UnitNumber'
import { ValuationHoverTarget } from './ValuationDisplay'

interface CategoryTabSpec {
  id: LeaderboardCategory
  icon: IconName
  labelKey: string
}

const CATEGORY_TABS: readonly CategoryTabSpec[] = [
  { id: 'realm', icon: 'insight', labelKey: 'app.leaderboard.tabRealm' },
  { id: 'technique', icon: 'forging', labelKey: 'app.leaderboard.tabTechnique' },
  { id: 'valuation', icon: 'shop', labelKey: 'app.leaderboard.tabValuation' },
  { id: 'spirit-stone', icon: 'spirit', labelKey: 'app.leaderboard.tabSpiritStone' },
  { id: 'zongmen', icon: 'social', labelKey: 'app.leaderboard.tabZongmen' },
  { id: 'duel', icon: 'combat', labelKey: 'app.leaderboard.tabDuel' },
] as const

/**
 * 全服排行榜页面（支持 12 个整点快照榜单、我的名次置顶条、点击角色打开同道名片及 503 预热等待态）。
 */
export const LeaderboardPage = memo(function LeaderboardPage(): React.JSX.Element {
  const t = useT()
  const selfPlayerId = useGameStore(state => state.playerId)
  const [category, setCategory] = useState<LeaderboardCategory>('realm')
  const [techniqueId, setTechniqueId] = useState<string>(TECHNIQUE_IDS[0])
  const [indexData, setIndexData] = useState<LeaderboardIndexDto | null>(null)
  const [snapshot, setSnapshot] = useState<LeaderboardSnapshotDto | null>(null)
  const [loading, setLoading] = useState<boolean>(true)
  const [warmingUp, setWarmingUp] = useState<boolean>(false)
  const [retryAfterSeconds, setRetryAfterSeconds] = useState<number>(5)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [refreshNonce, setRefreshNonce] = useState<number>(0)

  const activeBoardId = resolveBoardId(category, techniqueId)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setErrorMessage(null)

    void Promise.all([
      fetchLeaderboardIndex(selfPlayerId, controller.signal),
      fetchLeaderboardBoard(activeBoardId, selfPlayerId, controller.signal),
    ])
      .then(([idx, board]) => {
        if (controller.signal.aborted) return
        setIndexData(idx)
        setSnapshot(board)
        setWarmingUp(false)
        setLoading(false)
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        if (error instanceof LeaderboardApiError && error.status === 503) {
          setWarmingUp(true)
          setRetryAfterSeconds(error.retryAfterSeconds ?? 5)
          setErrorMessage(null)
          setLoading(false)
          return
        }
        setWarmingUp(false)
        setErrorMessage(
          error instanceof Error && error.message
            ? error.message
            : t('app.leaderboard.loadFailed'),
        )
        setLoading(false)
      })

    return () => controller.abort()
  }, [activeBoardId, selfPlayerId, refreshNonce, t])

  // 服务端刚启动若尚未物化完成，按 Retry-After 自动重试一次以平滑过渡到首版榜单。
  useEffect(() => {
    if (!warmingUp) return undefined
    const delayMs = Math.max(2, retryAfterSeconds) * 1000
    const timer = window.setTimeout(() => {
      setRefreshNonce(n => n + 1)
    }, delayMs)
    return () => window.clearTimeout(timer)
  }, [warmingUp, retryAfterSeconds])

  const handleOpenPlayerCard = (playerId?: string | null): void => {
    if (!playerId) return
    useSocialStore.getState().requestPlayerCard(playerId)
  }

  const handleManualRefresh = (): void => {
    setRefreshNonce(n => n + 1)
  }

  const techniqueOptions = TECHNIQUE_IDS.map(id => ({
    value: id,
    label: t(`insight.technique.${id}`),
  }))

  const activeSummary = indexData?.boards.find(b => b.boardId === activeBoardId) ?? null
  const computedAtText = formatLeaderboardTimestamp(snapshot?.computedAt ?? indexData?.computedAt)
  const nextComputeAtText = formatLeaderboardTimestamp(snapshot?.nextComputeAt ?? indexData?.nextComputeAt)
  const participantCount = snapshot?.participantCount ?? activeSummary?.participantCount ?? 0

  return (
    <section className="panel leaderboard-page" aria-label={t('app.leaderboard.title')}>
      <header className="leaderboard-header">
        <div className="leaderboard-title-wrap">
          <div className="leaderboard-title-row">
            <Icon name="leaderboard" size={18} />
            <h2>{t('app.leaderboard.title')}</h2>
          </div>
          <p className="leaderboard-subtitle">{t('app.leaderboard.subtitle')}</p>
        </div>

        <div className="leaderboard-meta-strip">
          <span className="leaderboard-meta-chip">
            {t('app.leaderboard.computedAt', { time: computedAtText })}
          </span>
          <span className="leaderboard-meta-chip">
            {t('app.leaderboard.nextComputeAt', { time: nextComputeAtText })}
          </span>
          <span className="leaderboard-meta-chip">
            {t('app.leaderboard.participants', { count: formatNumber(participantCount) })}
          </span>
          <button
            className="leaderboard-refresh-btn"
            disabled={loading}
            onClick={handleManualRefresh}
            type="button"
          >
            {loading ? t('app.leaderboard.refreshing') : t('app.leaderboard.refresh')}
          </button>
        </div>
      </header>

      <div className="leaderboard-tabs" role="tablist" aria-label={t('app.leaderboard.categoryAria')}>
        {CATEGORY_TABS.map(tab => {
          const isActive = category === tab.id
          return (
            <button
              aria-selected={isActive}
              className={`leaderboard-tab-btn${isActive ? ' active' : ''}`}
              key={tab.id}
              onClick={() => setCategory(tab.id)}
              role="tab"
              type="button"
            >
              <Icon name={tab.icon} size={14} />
              <span>{t(tab.labelKey)}</span>
            </button>
          )
        })}
      </div>

      {category === 'technique' && (
        <div className="leaderboard-subtabs">
          <OptionToggle
            ariaLabel={t('app.leaderboard.techniqueSubAria')}
            onChange={setTechniqueId}
            options={techniqueOptions}
            value={techniqueId}
          />
        </div>
      )}

      {warmingUp ? (
        <div className="leaderboard-state-card is-warming">
          <strong>{t('app.leaderboard.warmingTitle')}</strong>
          <p>{t('app.leaderboard.warmingDesc', { seconds: retryAfterSeconds })}</p>
          <button onClick={handleManualRefresh} type="button">
            {t('app.leaderboard.retryNow')}
          </button>
        </div>
      ) : errorMessage ? (
        <div className="leaderboard-state-card is-error">
          <strong>{t('app.leaderboard.errorTitle')}</strong>
          <p>{errorMessage}</p>
          <button onClick={handleManualRefresh} type="button">
            {t('app.leaderboard.retryNow')}
          </button>
        </div>
      ) : loading && !snapshot ? (
        <div className="leaderboard-state-card">
          <p>{t('app.leaderboard.loading')}</p>
        </div>
      ) : snapshot ? (
        <>
          <MyRankBanner
            category={category}
            myRank={snapshot.myRank ?? null}
            entries={snapshot.entries}
          />

          {snapshot.entries.length === 0 ? (
            <div className="leaderboard-state-card">
              <p>{t('app.leaderboard.emptyList')}</p>
            </div>
          ) : (
            <div className="leaderboard-list" role="list">
              {snapshot.entries.map(entry => {
                const isSelf =
                  category === 'zongmen'
                    ? snapshot.myRank?.rank === entry.rank
                    : Boolean(selfPlayerId && entry.playerId === selfPlayerId)
                return (
                  <LeaderboardRow
                    category={category}
                    entry={entry}
                    isSelf={isSelf}
                    key={`${snapshot.boardId}:${entry.rank}:${entry.playerId ?? entry.zongmenId ?? entry.displayName}`}
                    onOpenPlayerCard={handleOpenPlayerCard}
                  />
                )
              })}
            </div>
          )}
        </>
      ) : null}
    </section>
  )
})

interface MyRankBannerProps {
  category: LeaderboardCategory
  myRank: LeaderboardMyRankDto | null
  entries: readonly LeaderboardEntryDto[]
}

const MyRankBanner = memo(function MyRankBanner({
  category,
  myRank,
  entries,
}: MyRankBannerProps): React.JSX.Element {
  const t = useT()

  if (!myRank) {
    return (
      <div className="leaderboard-my-rank is-unranked">
        <span className="leaderboard-my-rank-label">{t('app.leaderboard.myRankLabel')}</span>
        <span className="leaderboard-my-rank-empty">
          {category === 'zongmen'
            ? t('app.leaderboard.notInZongmen')
            : t('app.leaderboard.unrankedHint')}
        </span>
      </div>
    )
  }

  const matchedZongmen =
    category === 'zongmen' ? entries.find(entry => entry.rank === myRank.rank) : undefined

  return (
    <div className="leaderboard-my-rank">
      <div className="leaderboard-my-rank-left">
        <span className="leaderboard-my-rank-label">{t('app.leaderboard.myRankLabel')}</span>
        <strong className="leaderboard-my-rank-pos">#{myRank.rank}</strong>
        {matchedZongmen && (
          <span className="leaderboard-my-rank-sect">
            {matchedZongmen.zongmenPrefix ? `[${matchedZongmen.zongmenPrefix}] ` : ''}
            {matchedZongmen.zongmenName || matchedZongmen.displayName}
          </span>
        )}
      </div>
      <div className="leaderboard-my-rank-metric">
        {renderMyRankMetric(category, myRank, t)}
      </div>
    </div>
  )
})

interface LeaderboardRowProps {
  category: LeaderboardCategory
  entry: LeaderboardEntryDto
  isSelf: boolean
  onOpenPlayerCard: (playerId?: string | null) => void
}

const LeaderboardRow = memo(function LeaderboardRow({
  category,
  entry,
  isSelf,
  onOpenPlayerCard,
}: LeaderboardRowProps): React.JSX.Element {
  const t = useT()
  const podiumClass =
    entry.rank === 1 ? ' is-top1' : entry.rank === 2 ? ' is-top2' : entry.rank === 3 ? ' is-top3' : ''
  const selfClass = isSelf ? ' is-self' : ''

  if (category === 'zongmen') {
    const memberCount = entry.memberCount ?? Math.floor(entry.score)
    const hallLevel = entry.hallLevel ?? 1
    return (
      <div className={`leaderboard-row leaderboard-row--zongmen${podiumClass}${selfClass}`} role="listitem">
        <span className={`leaderboard-rank${podiumClass}`}>{entry.rank}</span>
        <div className="leaderboard-row-identity">
          <div className="leaderboard-zongmen-main">
            {entry.zongmenPrefix && (
              <span className="leaderboard-zongmen-prefix">[{entry.zongmenPrefix}]</span>
            )}
            <strong className="leaderboard-row-name">{entry.zongmenName || entry.displayName}</strong>
            {isSelf && <span className="leaderboard-self-badge">{t('app.leaderboard.mySectTag')}</span>}
          </div>
          <span className="leaderboard-row-sub">
            {t('social.zongmen.hallLevel', { level: hallLevel })}
          </span>
        </div>
        <div className="leaderboard-row-metric">
          <strong className="leaderboard-metric-primary">
            {t('app.leaderboard.zongmenMembers', { count: memberCount })}
          </strong>
          <span className="leaderboard-metric-sub">
            {t('social.zongmen.hallLevel', { level: hallLevel })}
          </span>
        </div>
      </div>
    )
  }

  const realmInfo = calculateRealmInfo(
    entry.experience ?? 0,
    entry.realmLevel ?? 1,
    entry.experienceToNext ?? undefined,
  )

  return (
    <div className={`leaderboard-row${podiumClass}${selfClass}`} role="listitem">
      <span className={`leaderboard-rank${podiumClass}`}>{entry.rank}</span>

      <button
        className="leaderboard-player-trigger"
        onClick={() => onOpenPlayerCard(entry.playerId)}
        title={t('social.card.openCardHint')}
        type="button"
      >
        <div className="leaderboard-avatar">
          <CharacterAvatar
            actorId={resolvePlayerActorId(entry.avatar)}
            name={entry.displayName}
            role="player"
          />
        </div>

        <div className="leaderboard-row-identity">
          <div className="leaderboard-row-name-line">
            <strong className="leaderboard-row-name">{entry.displayName}</strong>
            {entry.playerSeq > 0 && (
              <span className="leaderboard-player-seq">#{entry.playerSeq}</span>
            )}
            {isSelf && <span className="leaderboard-self-badge">{t('app.leaderboard.selfTag')}</span>}
          </div>
          <div className="leaderboard-row-realm">
            <RealmBadge disablePopover realmInfo={realmInfo} />
          </div>
        </div>
      </button>

      <div className="leaderboard-row-metric">
        {renderEntryMetric(category, entry, realmInfo.stageName, realmInfo.minorName, t)}
      </div>
    </div>
  )
})

function renderEntryMetric(
  category: LeaderboardCategory,
  entry: LeaderboardEntryDto,
  stageName: string,
  minorName: string,
  t: (key: string, params?: Record<string, string | number>) => string,
): React.JSX.Element {
  switch (category) {
    case 'realm':
      return (
        <>
          <strong className="leaderboard-metric-primary">
            {stageName} · {minorName}
          </strong>
          <span className="leaderboard-metric-sub">
            <span>{t('app.leaderboard.realmExpPrefix')}</span>{' '}
            <UnitNumber tone="jade" value={entry.experience ?? 0} />
          </span>
        </>
      )

    case 'technique': {
      const level = entry.techniqueLevel ?? 0
      const exp = entry.techniqueExp ?? entry.score
      return (
        <>
          <strong className="leaderboard-metric-primary">Lv.{level}</strong>
          <span className="leaderboard-metric-sub">
            <UnitNumber suffix={` ${t('app.leaderboard.expSuffix')}`} value={exp} />
          </span>
        </>
      )
    }

    case 'valuation': {
      const val = entry.valuation ?? entry.score
      return (
        <>
          <ValuationHoverTarget
            breakdown={entry.valuationBreakdown}
            placement="left"
            valuation={val}
          >
            <strong className="leaderboard-metric-primary is-hoverable">
              <UnitNumber disableTooltip tone="gold" value={val} />
            </strong>
          </ValuationHoverTarget>
          <span className="leaderboard-metric-sub">{t('app.leaderboard.valuationSub')}</span>
        </>
      )
    }

    case 'spirit-stone':
      return (
        <>
          <strong className="leaderboard-metric-primary">
            <UnitNumber suffix={` ${t('app.leaderboard.spiritStoneUnit')}`} tone="gold" value={entry.score} />
          </strong>
          <span className="leaderboard-metric-sub">{t('app.leaderboard.spiritStoneSub')}</span>
        </>
      )

    case 'duel': {
      const wins = entry.duelWins ?? Math.floor(entry.score)
      const losses = entry.duelLosses ?? 0
      const total = wins + losses
      const winRate = total > 0 ? Math.round((wins / total) * 100) : 0
      return (
        <>
          <strong className="leaderboard-metric-primary">
            {t('app.leaderboard.duelWinsScore', { wins: formatNumber(wins) })}
          </strong>
          <span className="leaderboard-metric-sub">
            {t('app.leaderboard.duelRecord', {
              wins: formatNumber(wins),
              losses: formatNumber(losses),
              rate: winRate,
            })}
          </span>
        </>
      )
    }

    default:
      return (
        <strong className="leaderboard-metric-primary">
          <UnitNumber value={entry.score} />
        </strong>
      )
  }
}

function renderMyRankMetric(
  category: LeaderboardCategory,
  myRank: LeaderboardMyRankDto,
  t: (key: string, params?: Record<string, string | number>) => string,
): React.JSX.Element {
  switch (category) {
    case 'realm':
      return (
        <span className="leaderboard-my-score">
          {t('app.leaderboard.myRealmScore', { level: Math.floor(myRank.score) })}
        </span>
      )

    case 'technique':
      return (
        <span className="leaderboard-my-score">
          Lv.{myRank.techniqueLevel ?? 0}（
          <UnitNumber suffix={` ${t('app.leaderboard.expSuffix')}`} value={myRank.score} />
          ）
        </span>
      )

    case 'valuation':
      return (
        <span className="leaderboard-my-score">
          {t('app.leaderboard.myValuationPrefix')}{' '}
          <UnitNumber tone="gold" value={myRank.score} />
        </span>
      )

    case 'spirit-stone':
      return (
        <span className="leaderboard-my-score">
          <UnitNumber suffix={` ${t('app.leaderboard.spiritStoneUnit')}`} tone="gold" value={myRank.score} />
        </span>
      )

    case 'zongmen':
      return (
        <span className="leaderboard-my-score">
          {t('app.leaderboard.zongmenMembers', { count: Math.floor(myRank.score) })}
        </span>
      )

    case 'duel':
      return (
        <span className="leaderboard-my-score">
          {t('app.leaderboard.duelWinsScore', { wins: formatNumber(Math.floor(myRank.score)) })} /{' '}
          {t('app.leaderboard.duelLossesOnly', { losses: formatNumber(myRank.duelLosses ?? 0) })}
        </span>
      )
  }
}
