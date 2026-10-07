import { memo, useMemo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { PARTY_OPEN_SLOTS_MAX, useSocialStore } from '../game/socialStore'
import type { ExpeditionAlly } from '../game/types'
import { useT } from '../i18n'
import type { PartyMemberDto } from '../network/protocol'
import { ConfirmDialog } from './ConfirmDialog'

/**
 * 战斗页队伍面板（§21.13 同图共战，对齐银河奶牛成员卡片条）：
 * 名册/就绪/在线由 SocialState 帧权威供给；会话中成员血蓝实时值按 playerId
 * 与队首 party.expedition 的 allies 阵列配对（allies 含本机位）。
 * 本人卡片提供「参与斗法」开关——勾选即 ready（全员就绪且槽位占满自动开打）；
 * 会话中取消等同散场（名册冻结铁律：任一成员变动终止本次讨伐），需确认。
 */
export const PartyPanel = memo(function PartyPanel(): React.JSX.Element | null {
  const t = useT()
  const party = useSocialStore(state => state.party)
  const myPlayerId = useGameStore(state => state.playerId)
  const expedition = useGameStore(state => state.queue.entries[0]?.expedition ?? null)
  const readyParty = useSocialStore(state => state.readyParty)
  const unreadyParty = useSocialStore(state => state.unreadyParty)
  const [confirmQuit, setConfirmQuit] = useState(false)
  // 会话实时面板按 playerId 索引（名册序与规格序不同源，不按下标对齐）
  const allyByPlayer = useMemo(() => {
    const map = new Map<string, ExpeditionAlly>()
    for (const ally of expedition?.allies ?? []) map.set(ally.playerId, ally)
    return map
  }, [expedition?.allies])
  if (!party) return null

  const session = party.activeSession ?? null
  const self = party.members.find(member => member.playerId === myPlayerId)
  const readyCount = party.members.filter(member => member.ready).length
  const openSlots = party.openSlots ?? PARTY_OPEN_SLOTS_MAX
  const emptySlots = session ? 0 : Math.max(0, openSlots - party.members.length)

  const toggleParticipate = () => {
    if (session) {
      setConfirmQuit(true)
      return
    }
    if (self?.ready) unreadyParty()
    else readyParty()
  }

  return (
    <section aria-label={t('social.strip.aria')} className="party-strip">
      <header className="party-strip-head">
        <strong className="party-strip-name">{party.name}</strong>
        <span className="party-strip-count">{t('social.memberCountOf', { count: party.members.length, slots: openSlots })}</span>
        {session ? (
          <span className="badge badge-party">{t('social.strip.fighting')}</span>
        ) : (
          <span className="badge">{t('social.ready.count', { ready: readyCount, count: party.members.length })}</span>
        )}
        <span className="party-strip-hint">
          {session ? t('social.strip.sessionHint') : t('social.strip.idleHint')}
        </span>
      </header>
      <div className="party-strip-cards" role="list">
        {party.members.map(member => (
          <MemberCard
            ally={allyByPlayer.get(member.playerId) ?? null}
            inSession={session !== null}
            isLeader={member.playerId === party.leaderId}
            isSelf={member.playerId === myPlayerId}
            key={member.playerId}
            member={member}
            onToggle={member.playerId === myPlayerId ? toggleParticipate : undefined}
          />
        ))}
        {Array.from({ length: emptySlots }, (_, index) => (
          <div className="party-slot party-slot--empty" key={`empty-${index}`} role="listitem">
            <span className="party-slot-empty-label">{t('social.strip.emptySlot')}</span>
          </div>
        ))}
      </div>
      <ConfirmDialog
        danger
        description={t('social.confirm.quitSessionDesc')}
        onClose={() => setConfirmQuit(false)}
        onConfirm={() => unreadyParty()}
        open={confirmQuit}
        title={t('social.confirm.quitSessionTitle')}
      />
    </section>
  )
})

/** 单个成员卡片：待命时显示就绪/在线状态，会话中切换为实时血蓝条。 */
function MemberCard({
  member, isLeader, isSelf, inSession, ally, onToggle,
}: {
  member: PartyMemberDto
  isLeader: boolean
  isSelf: boolean
  inSession: boolean
  /** 会话中实时战斗状态；会话刚开始镜像未到位时为 null */
  ally: ExpeditionAlly | null
  onToggle?: () => void
}): React.JSX.Element {
  const t = useT()
  const downed = inSession && ally !== null && ally.hitpoints <= 0
  return (
    <div
      className={`party-slot${isSelf ? ' party-slot--self' : ''}${downed ? ' party-slot--downed' : ''}`}
      role="listitem"
    >
      <span aria-hidden="true" className="party-slot-seal">{member.displayName.slice(0, 1)}</span>
      <div className="party-slot-body">
        <span className="party-slot-name">
          {member.displayName}
          {isLeader && <em className="chat-leader-mark">{t('social.members.leader')}</em>}
          {isSelf && <em className="arena-self-tag">{t('social.session.selfTag')}</em>}
        </span>
        {inSession ? (
          ally ? (
            <span className="party-slot-bars">
              <span className="party-slot-bar">
                <i style={{ width: `${Math.max(0, Math.min(100, ally.hitpoints / Math.max(1, ally.maxHitpoints ?? ally.hitpoints) * 100))}%` }} />
              </span>
              <span className="party-slot-bar party-slot-bar--mp">
                <i style={{ width: `${Math.max(0, Math.min(100, ally.manapoints / Math.max(1, ally.maxManapoints ?? ally.manapoints) * 100))}%` }} />
              </span>
            </span>
          ) : (
            <span className="party-slot-tags">{t('social.strip.fighting')}</span>
          )
        ) : (
          <span className="party-slot-tags">
            <span className={`chat-presence chat-presence--${member.online ? 'online' : 'offline'}`}>
              {member.online ? t('social.online') : t('social.offline')}
            </span>
            <span className={`party-ready-tag${member.ready ? ' is-ready' : ''}`}>
              {member.ready ? t('social.strip.joined') : t('social.strip.notJoined')}
            </span>
          </span>
        )}
      </div>
      {isSelf && onToggle && (
        <button
          aria-pressed={member.ready}
          className={`party-join-toggle${member.ready ? ' is-on' : ''}${inSession ? ' danger' : ''}`}
          onClick={onToggle}
          type="button"
        >
          {inSession
            ? t('social.action.quitParticipation')
            : member.ready ? t('social.action.leaveParticipation') : t('social.action.participate')}
        </button>
      )}
    </div>
  )
}
