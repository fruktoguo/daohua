import { memo, useEffect, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { localizeActionDisplayText, resolveActionDefinition } from '../game/ids'
import { useSocialStore } from '../game/socialStore'
import { useT } from '../i18n'
import { ItemReference } from './ItemDisplay'
import { ReportBlock } from './reportSections'
import { ReportBriefSection } from './ReportBriefSection'

export type StatsViewMode = 'brief' | 'detailed'

/**
 * 行动统计面板 (StatsPanel)
 *
 * 展示当前队首行动或所选队伍成员的行动收益：
 * 1. 视图切换：简要 (Brief) 与 详细 (Detailed) 双模式 Tab，默认为简要；
 *    - 简要：基于已流逝时长换算为平均每日灵石收益估值、战斗频次/轮次效率、每小时修为与战斗经验速率；
 *    - 详细：保留既有行动账本结构（遭遇/击杀/死亡/斗法/物品得失/修为底蕴六维经验明细）；
 * 2. 队伍协同：队伍有其他队友时，顶栏支持在「本人」与「队友」间自由切换查看各自行动收益；
 * 3. 斗法暂存区：仅在查看本人且队首为勾选斗法的战斗行动时保留展示。
 */
export const StatsPanel = memo(function StatsPanel(): React.JSX.Element {
  const t = useT()
  const myPlayerId = useGameStore(state => state.playerId)
  const myName = useGameStore(state => state.name)
  const duel = useGameStore(state => state.duel)
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const headEntry = useGameStore(state => state.queue.entries[0])
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)

  // 队伍信息与队友收益查询
  const party = useSocialStore(state => state.party)
  const teammateReports = useSocialStore(state => state.teammateReports)
  const requestTeammateReport = useSocialStore(state => state.requestTeammateReport)

  // 状态：选中的成员（空串或等于 myPlayerId 表示本人）
  const [selectedMemberId, setSelectedMemberId] = useState<string>(myPlayerId ?? '')
  // 状态：视图模式（简要 vs 详细），默认简要
  const [viewMode, setViewMode] = useState<StatsViewMode>('brief')

  const partyMembers = party?.members ?? []
  const hasTeammates = partyMembers.length > 1

  // 若选中的队友离开队伍，自动回退到本人
  useEffect(() => {
    if (!hasTeammates && selectedMemberId !== myPlayerId) {
      setSelectedMemberId(myPlayerId ?? '')
    }
  }, [hasTeammates, myPlayerId, selectedMemberId])

  const isSelf = !selectedMemberId || selectedMemberId === myPlayerId
  const selectedTeammate = !isSelf ? partyMembers.find(m => m.playerId === selectedMemberId) : null

  // 选中队友时自动拉取/刷新战报数据（若缓存为空或已超过 5 秒）
  useEffect(() => {
    if (!isSelf && selectedMemberId) {
      const cached = teammateReports[selectedMemberId]
      if (!cached || (Date.now() - cached.updatedAtMs > 5000 && !cached.loading)) {
        requestTeammateReport(selectedMemberId)
      }
    }
  }, [isSelf, selectedMemberId, teammateReports, requestTeammateReport])

  // 当前展示的账本
  const teammateEntry = !isSelf && selectedMemberId ? teammateReports[selectedMemberId] : null
  const current = isSelf ? duel.currentReport : (teammateEntry?.report ?? null)
  const isLoadingTeammate = !isSelf && Boolean(teammateEntry?.loading && !teammateEntry.report)

  // 暂存区仅在「查看本人」且「队首为勾选斗法的战斗行动」且存在暂存物品时展示
  const pvpArmed = isSelf && Boolean(headEntry && headEntry.executionKind === 'combat-encounter' && headEntry.pvpEnabled)
  const stagedTotals = new Map<string, number>()
  if (isSelf) {
    for (const item of duel.stagedItems) {
      stagedTotals.set(item.itemId, (stagedTotals.get(item.itemId) ?? 0) + item.quantity)
    }
  }
  const showStaged = pvpArmed && stagedTotals.size > 0

  const duelGained = (isSelf ? current?.duelItemsGained : null) ?? []
  const duelLost = (isSelf ? current?.duelItemsLost : null) ?? []
  const showDuelResults = isSelf && (duelGained.length > 0 || duelLost.length > 0)
  const hasDuelSection = showStaged || showDuelResults

  // 「进行中」时长需要每秒刷新：仅当前账本存在时启用 tick
  const needsTick = Boolean(current)
  const [, setTick] = useState(0)
  useEffect(() => {
    if (!needsTick) return undefined
    const timer = window.setInterval(() => setTick(value => value + 1), 1000)
    return () => window.clearInterval(timer)
  }, [needsTick])

  // 以「快照服务端时刻 + 距上次同步的本地流逝」估算当前服务端时间
  const estimatedServerNow = serverTime + Math.max(0, performance.now() - lastSyncPerformanceTime) / 1000

  const isEmpty = !isLoadingTeammate && !current && (!isSelf || !hasDuelSection)

  const actionTitle = current
    ? localizeActionDisplayText(
      resolveActionDefinition(current.actionId)?.name
        || current.actionName || current.actionId || t('combat.report.currentSection'),
      world,
      maps,
    )
    : ''

  return (
    <section aria-label={t('combat.report.title')} className="battle-report-panel">
      <header className="battle-report-header">
        <div className="battle-report-header-left">
          <span className="battle-report-title" id="battle-report-panel-title">{t('combat.report.title')}</span>
          {/* 简要 / 详细 模式切换页签 */}
          <nav aria-label={t('combat.report.viewModeAria')} className="expedition-tabs battle-report-mode-tabs" role="tablist">
            <button
              aria-selected={viewMode === 'brief'}
              className={viewMode === 'brief' ? 'active' : ''}
              onClick={() => setViewMode('brief')}
              role="tab"
              type="button"
            >{t('combat.report.modeBrief')}</button>
            <button
              aria-selected={viewMode === 'detailed'}
              className={viewMode === 'detailed' ? 'active' : ''}
              onClick={() => setViewMode('detailed')}
              role="tab"
              type="button"
            >{t('combat.report.modeDetailed')}</button>
          </nav>
        </div>

        {/* 队伍成员切换栏（组队且成员 > 1 时可见） */}
        {hasTeammates && (
          <nav aria-label={t('combat.report.membersAria')} className="expedition-tabs battle-report-member-tabs" role="tablist">
            {partyMembers.map(member => {
              const memberIsSelf = member.playerId === myPlayerId
              const active = memberIsSelf ? isSelf : member.playerId === selectedMemberId
              return (
                <button
                  aria-selected={active}
                  className={active ? 'active' : ''}
                  key={member.playerId}
                  onClick={() => setSelectedMemberId(member.playerId)}
                  role="tab"
                  type="button"
                >
                  {memberIsSelf
                    ? t('combat.report.selfTab', { name: myName || member.displayName })
                    : member.displayName}
                </button>
              )
            })}
          </nav>
        )}
      </header>

      {isLoadingTeammate ? (
        <p className="battle-report-loading">{t('combat.report.loadingTeammate')}</p>
      ) : isEmpty ? (
        <p className="battle-report-empty">
          {isSelf ? t('combat.report.empty') : t('combat.report.teammateEmpty', { name: selectedTeammate?.displayName ?? '' })}
        </p>
      ) : (
        <div className="battle-report-body">
          {current && (
            viewMode === 'brief' ? (
              <ReportBriefSection
                badge={t('combat.report.ongoing')}
                className="battle-report-block--current"
                nowSeconds={estimatedServerNow}
                report={current}
                title={actionTitle}
              />
            ) : (
              <ReportBlock
                badge={t('combat.report.ongoing')}
                className="battle-report-block--current"
                nowSeconds={estimatedServerNow}
                report={current}
                title={actionTitle}
              />
            )
          )}
          {isSelf && hasDuelSection && (
            <div className="battle-report-staged">
              {showStaged && (
                <div className="battle-report-staged-group">
                  <span className="battle-report-staged-title">{t('combat.report.stagedTitle')}</span>
                  <div className="battle-report-staged-list">
                    {[...stagedTotals].map(([itemId, quantity]) => (
                      <ItemReference amount={quantity} itemId={itemId} key={itemId} />
                    ))}
                  </div>
                </div>
              )}
              {showDuelResults && (
                <div className="battle-report-staged-group">
                  <div className="battle-report-item-columns">
                    {duelGained.length > 0 && (
                      <div className="battle-report-item-column">
                        <span className="battle-report-staged-title">{t('combat.report.duelItemsGained')}</span>
                        <div className="battle-report-items">
                          {duelGained.map(item => (
                            <ItemReference amount={item.quantity} itemId={item.itemId} key={item.itemId} />
                          ))}
                        </div>
                      </div>
                    )}
                    {duelLost.length > 0 && (
                      <div className="battle-report-item-column">
                        <span className="battle-report-staged-title">{t('combat.report.duelItemsLost')}</span>
                        <div className="battle-report-items">
                          {duelLost.map(item => (
                            <ItemReference amount={item.quantity} itemId={item.itemId} key={item.itemId} />
                          ))}
                        </div>
                      </div>
                    )}
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </section>
  )
})
