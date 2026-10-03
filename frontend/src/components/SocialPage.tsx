import { memo, useEffect, useMemo, useRef, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { useSocialStore } from '../game/socialStore'
import { formatNumber } from '../game/format'
import { useT } from '../i18n'
import { gameClient } from '../network/gameClient'
import { PartyActivityKind, type ChatChannel, type PartyActivityKind as PartyActivityKindType, type PartyProfileDto } from '../network/protocol'
import { useUiStore, type SocialSubTab } from '../uiStore'
import { ConfirmDialog } from './ConfirmDialog'

/** 队伍目标类型的固定选项集（协议值 + i18n 键后缀） */
const ACTIVITY_OPTIONS: readonly PartyActivityKindType[] = [
  PartyActivityKind.Any,
  PartyActivityKind.Combat,
  PartyActivityKind.Exploration,
  PartyActivityKind.Cultivation,
]

/** 身价门槛输入框解析：空串/0 视为不设门槛（协议约定 minValuation=0），负数与非法输入钳制为 0。 */
function parseMinValuation(raw: string): number {
  const value = Number.parseFloat(raw)
  return Number.isFinite(value) && value > 0 ? value : 0
}

/** 地图显示名解析：mapId 为空或未知时显示「不限」。 */
function mapLabel(mapId: string, mapNames: ReadonlyMap<string, string>, anyLabel: string): string {
  if (!mapId) return anyLabel
  return mapNames.get(mapId) ?? mapId
}

/**
 * 细条内联下拉 (DropdownSelect)
 *
 * 点击触发器在下方展开贴边小列表（非模态），点外部或 Esc 关闭；
 * 与 ChoiceMenu 的模态/瓦片形态区分——本组件面向队伍表单的轻量单选场景。
 */
const DropdownSelect = memo(function DropdownSelect({
  label,
  value,
  options,
  onSelect,
  className = '',
}: {
  /** 字段名（作 aria-label；可见标签由外层 .party-field span 或占位项承担） */
  label: string
  /** 当前选中项 id */
  value: string
  options: readonly { id: string; label: string }[]
  onSelect: (id: string) => void
  className?: string
}): React.JSX.Element {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const selected = options.find(option => option.id === value)

  useEffect(() => {
    if (!open) return
    const onPointerDown = (event: PointerEvent): void => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') setOpen(false)
    }
    window.addEventListener('pointerdown', onPointerDown)
    window.addEventListener('keydown', onKeyDown)
    return () => {
      window.removeEventListener('pointerdown', onPointerDown)
      window.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  return (
    <div className={`party-select${open ? ' is-open' : ''} ${className}`.trim()} ref={rootRef}>
      <button
        aria-expanded={open}
        aria-haspopup="listbox"
        aria-label={label}
        className="party-select-trigger"
        onClick={() => setOpen(current => !current)}
        type="button"
      >
        <span className="party-select-value">{selected?.label ?? label}</span>
        <span aria-hidden="true" className="party-select-arrow">▾</span>
      </button>
      {open && (
        <div className="party-select-popover" role="listbox">
          {options.map(option => (
            <button
              aria-selected={option.id === value}
              className={`party-select-option${option.id === value ? ' is-selected' : ''}`}
              key={option.id}
              onClick={() => {
                onSelect(option.id)
                setOpen(false)
              }}
              role="option"
              type="button"
            >{option.label}</button>
          ))}
        </div>
      )}
    </div>
  )
})

/** 目标类型/地图/门槛三件套的选择器行：无队伍三表单与队长编辑共用同一组控件。 */
const PartyProfileFields = memo(function PartyProfileFields({
  value,
  onChange,
  mapNames,
  showPublic = false,
}: {
  value: { activityKind: PartyActivityKindType; mapId: string; minValuation: string; isPublic: boolean }
  onChange: (next: { activityKind: PartyActivityKindType; mapId: string; minValuation: string; isPublic: boolean }) => void
  mapNames: ReadonlyMap<string, string>
  showPublic?: boolean
}): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)
  return (
    <div className="party-fields">
      <div className="party-field">
        <span>{t('social.field.activity')}</span>
        <DropdownSelect
          label={t('social.field.activity')}
          onSelect={id => onChange({ ...value, activityKind: id as PartyActivityKindType })}
          options={ACTIVITY_OPTIONS.map(kind => ({ id: kind, label: t(`social.activity.${kind}`) }))}
          value={value.activityKind}
        />
      </div>
      <div className="party-field">
        <span>{t('social.field.map')}</span>
        <DropdownSelect
          label={t('social.field.map')}
          onSelect={id => onChange({ ...value, mapId: id })}
          options={[
            { id: '', label: t('social.field.anyMap') },
            ...maps.map(map => ({ id: map.mapId, label: mapNames.get(map.mapId) ?? map.name })),
          ]}
          value={value.mapId}
        />
      </div>
      <label className="party-field">
        <span>{t('social.field.minValuation')}</span>
        <input
          min={0}
          onChange={event => onChange({ ...value, minValuation: event.target.value })}
          placeholder={t('social.field.minValuationPlaceholder')}
          type="number"
          value={value.minValuation}
        />
      </label>
      {showPublic && (
        <label className="party-field party-field--check">
          <input
            checked={value.isPublic}
            onChange={event => onChange({ ...value, isPublic: event.target.checked })}
            type="checkbox"
          />
          <span>{t('social.field.isPublic')}</span>
        </label>
      )}
    </div>
  )
})

/** 待处理队伍邀请横幅（无队伍时置顶显示）。 */
const PartyInvitationBanner = memo(function PartyInvitationBanner(): React.JSX.Element | null {
  const t = useT()
  const invitation = useSocialStore(state => state.partyInvitation)
  if (!invitation) return null
  return (
    <div className="party-invitation" role="status">
      <span>{t('social.invitation.body', { name: invitation.inviterName, party: invitation.partyName || t('social.invitation.unnamed') })}</span>
      <span className="chat-entry-actions">
        <button onClick={() => gameClient.sendSocialAction('party.accept')} type="button">{t('social.action.accept')}</button>
        <button onClick={() => gameClient.sendSocialAction('party.decline')} type="button">{t('social.action.decline')}</button>
      </span>
    </div>
  )
})

/** 队伍匹配卡片：登记散人条件；已登记时展示当前条件与撤下入口。 */
const MatchSection = memo(function MatchSection({ mapNames }: { mapNames: ReadonlyMap<string, string> }): React.JSX.Element {
  const t = useT()
  const seeking = useSocialStore(state => state.seeking)
  const [form, setForm] = useState({ activityKind: PartyActivityKind.Any as PartyActivityKindType, mapId: '', minValuation: '', isPublic: true })

  const submit = (): void => {
    const party: PartyProfileDto = {
      activityKind: form.activityKind,
      mapId: form.mapId,
      minValuation: parseMinValuation(form.minValuation),
    }
    gameClient.sendSocialAction('party.match.set', undefined, undefined, { party })
  }

  return (
    <section className="party-card">
      <h3>{t('social.match.heading')}</h3>
      <p className="party-card-desc">{t('social.match.desc')}</p>
      {seeking ? (
        <div className="party-seeking">
          <span className="badge">{t('social.match.seeking')}</span>
          <span>{t(`social.activity.${seeking.activityKind}`)} · {mapLabel(seeking.mapId, mapNames, t('social.field.anyMap'))}</span>
          {seeking.minValuation > 0 && <span>{t('social.minValuationValue', { value: formatNumber(seeking.minValuation) })}</span>}
          <button onClick={() => gameClient.sendSocialAction('party.match.clear')} type="button">{t('social.action.matchClear')}</button>
        </div>
      ) : (
        <>
          <PartyProfileFields mapNames={mapNames} onChange={setForm} value={form} />
          <div className="party-card-actions">
            <button className="confirm-primary" onClick={submit} type="button">{t('social.action.matchSet')}</button>
          </div>
        </>
      )}
    </section>
  )
})

/** 组队大厅卡片：筛选请求 PartyLobby，渲染公开队伍与匹配中散人。 */
const LobbySection = memo(function LobbySection({ mapNames }: { mapNames: ReadonlyMap<string, string> }): React.JSX.Element {
  const t = useT()
  const lobby = useSocialStore(state => state.lobby)
  const myValuation = useSocialStore(state => state.myValuation)
  const myPartyId = useSocialStore(state => state.party?.partyId ?? null)
  const myPlayerId = useGameStore(state => state.playerId)
  const isLeader = useSocialStore(state => {
    const party = state.party
    return party !== null && party.leaderId === myPlayerId
  })
  const [filter, setFilter] = useState({ activityKind: PartyActivityKind.Any as PartyActivityKindType, mapId: '', minValuation: '', isPublic: true })

  const refresh = (): void => {
    gameClient.requestPartyLobby({
      activityKind: filter.activityKind === PartyActivityKind.Any ? null : filter.activityKind,
      mapId: filter.mapId || null,
      minValuation: parseMinValuation(filter.minValuation) || null,
    })
  }

  // 进入大厅分区即拉取一次当前视图（仅挂载时）；后续由筛选条件变化手动刷新。
  useEffect(() => {
    refresh()
  }, [])

  return (
    <section className="party-card">
      <h3>{t('social.lobby.heading')}</h3>
      <p className="party-card-desc">{t('social.lobby.desc')}</p>
      <PartyProfileFields mapNames={mapNames} onChange={setFilter} value={filter} />
      <div className="party-card-actions">
        <button className="confirm-primary" onClick={refresh} type="button">{t('social.action.refreshLobby')}</button>
        {myValuation !== null && <span className="party-my-valuation">{t('social.lobby.myValuation', { value: formatNumber(myValuation) })}</span>}
      </div>

      <h4 className="party-list-heading">{t('social.lobby.partiesHeading')}</h4>
      <ul className="party-lobby-list">
        {(lobby?.parties ?? []).map(entry => {
          const meets = myValuation === null || entry.minValuation <= myValuation
          const isMine = entry.partyId === myPartyId
          return (
            <li className="party-lobby-entry" key={entry.partyId}>
              <div className="party-lobby-entry-head">
                <strong>{entry.name}</strong>
                <span className="badge">{t(`social.activity.${entry.activityKind}`)}</span>
                <span className="party-lobby-map">{mapLabel(entry.mapId, mapNames, t('social.field.anyMap'))}</span>
                <span className="party-lobby-count">{t('social.memberCount', { count: entry.members.length })}</span>
              </div>
              <div className="party-lobby-entry-meta">
                <span>{t('social.lobby.leader', { name: entry.leaderName })}</span>
                {entry.minValuation > 0 && (
                  <span className={meets ? 'party-gate party-gate--met' : 'party-gate party-gate--unmet'}>
                    {t('social.minValuationValue', { value: formatNumber(entry.minValuation) })}
                  </span>
                )}
              </div>
              <div className="party-lobby-entry-actions">
                {isMine
                  ? <span className="badge">{t('social.lobby.myParty')}</span>
                  : myPartyId === null && (
                    <button disabled={!meets} onClick={() => gameClient.sendSocialAction('party.join', undefined, undefined, { partyId: entry.partyId })} type="button">
                      {meets ? t('social.action.join') : t('social.action.joinLocked')}
                    </button>)}
              </div>
            </li>
          )
        })}
        {(lobby?.parties ?? []).length === 0 && <li className="party-lobby-empty">{t('social.lobby.noParties')}</li>}
      </ul>

      <h4 className="party-list-heading">{t('social.lobby.seekersHeading')}</h4>
      <ul className="party-lobby-list">
        {(lobby?.seekers ?? []).map(seeker => (
          <li className="party-lobby-entry" key={seeker.playerId}>
            <div className="party-lobby-entry-head">
              <strong>{seeker.displayName}</strong>
              <span className={`chat-presence chat-presence--${seeker.online ? 'online' : 'offline'}`}>
                {seeker.online ? t('social.online') : t('social.offline')}
              </span>
              <span className="badge">{t(`social.activity.${seeker.activityKind}`)}</span>
              <span className="party-lobby-map">{mapLabel(seeker.mapId, mapNames, t('social.field.anyMap'))}</span>
            </div>
            <div className="party-lobby-entry-meta">
              <span>{t('social.valuationValue', { value: formatNumber(seeker.valuation) })}</span>
              {seeker.minValuation > 0 && <span>{t('social.lobby.seekerWants', { value: formatNumber(seeker.minValuation) })}</span>}
            </div>
            {isLeader && seeker.playerId !== myPlayerId && (
              <div className="party-lobby-entry-actions">
                <button onClick={() => gameClient.sendSocialAction('party.invite', seeker.playerId)} type="button">{t('social.action.invite')}</button>
              </div>
            )}
          </li>
        ))}
        {(lobby?.seekers ?? []).length === 0 && <li className="party-lobby-empty">{t('social.lobby.noSeekers')}</li>}
      </ul>
    </section>
  )
})

/** 创建队伍卡片：名称 + 目标三件套 + 公开开关，提交 party.create。 */
const CreateSection = memo(function CreateSection({ mapNames }: { mapNames: ReadonlyMap<string, string> }): React.JSX.Element {
  const t = useT()
  const [name, setName] = useState('')
  const [form, setForm] = useState({ activityKind: PartyActivityKind.Any as PartyActivityKindType, mapId: '', minValuation: '', isPublic: true })

  const submit = (): void => {
    const party: PartyProfileDto = {
      name: name.trim() || null,
      activityKind: form.activityKind,
      mapId: form.mapId,
      minValuation: parseMinValuation(form.minValuation),
      isPublic: form.isPublic,
    }
    gameClient.sendSocialAction('party.create', undefined, undefined, { party })
  }

  return (
    <section className="party-card">
      <h3>{t('social.create.heading')}</h3>
      <p className="party-card-desc">{t('social.create.desc')}</p>
      <div className="party-fields">
        <label className="party-field">
          <span>{t('social.field.name')}</span>
          <input
            maxLength={32}
            onChange={event => setName(event.target.value)}
            placeholder={t('social.field.namePlaceholder')}
            value={name}
          />
        </label>
      </div>
      <PartyProfileFields mapNames={mapNames} onChange={setForm} showPublic value={form} />
      <div className="party-card-actions">
        <button className="confirm-primary" onClick={submit} type="button">{t('social.action.create')}</button>
      </div>
    </section>
  )
})

/** 无队伍态三入口页签（§21.4「自主选择」：任选其一，一次只显示一个模式面板）。 */
const PARTYLESS_TABS: ReadonlyArray<{ id: 'lobby' | 'match' | 'create'; labelKey: string }> = [
  { id: 'lobby', labelKey: 'social.tab.lobby' },
  { id: 'match', labelKey: 'social.tab.match' },
  { id: 'create', labelKey: 'social.tab.create' },
]

/** 无队伍视图：邀请横幅置顶 + 模式页签，按所选模式渲染单一面板。 */
const PartylessView = memo(function PartylessView({ mapNames }: { mapNames: ReadonlyMap<string, string> }): React.JSX.Element {
  const t = useT()
  const partySubTab = useUiStore(state => state.partySubTab)
  const setPartySubTab = useUiStore(state => state.setPartySubTab)
  // 'overview' 是在队态页签；离队后回落到默认入口「组队大厅」。
  const entryTab = partySubTab === 'match' || partySubTab === 'create' ? partySubTab : 'lobby'
  return (
    <>
      <PartyInvitationBanner />
      <nav aria-label={t('social.tab.aria')} className="market-tabs party-mode-tabs" role="tablist">
        {PARTYLESS_TABS.map(tab => (
          <button
            aria-selected={entryTab === tab.id}
            className={entryTab === tab.id ? 'active' : ''}
            key={tab.id}
            onClick={() => setPartySubTab(tab.id)}
            role="tab"
            type="button"
          >{t(tab.labelKey)}</button>
        ))}
      </nav>
      {entryTab === 'lobby' && <LobbySection mapNames={mapNames} />}
      {entryTab === 'match' && <MatchSection mapNames={mapNames} />}
      {entryTab === 'create' && <CreateSection mapNames={mapNames} />}
    </>
  )
})

/** 队长档案编辑区：改名 + 三件套 + 公开开关，一键提交 party.update 补丁。 */
const LeaderProfileEditor = memo(function LeaderProfileEditor({ mapNames }: { mapNames: ReadonlyMap<string, string> }): React.JSX.Element | null {
  const t = useT()
  const party = useSocialStore(state => state.party)
  const [name, setName] = useState(party?.name ?? '')
  const [form, setForm] = useState({
    activityKind: (party?.activityKind ?? PartyActivityKind.Any) as PartyActivityKindType,
    mapId: party?.mapId ?? '',
    minValuation: party && party.minValuation > 0 ? String(party.minValuation) : '',
    isPublic: party?.isPublic ?? true,
  })
  if (!party) return null

  const submit = (): void => {
    const partyPatch: PartyProfileDto = {
      name: name.trim() || null,
      activityKind: form.activityKind,
      mapId: form.mapId,
      minValuation: parseMinValuation(form.minValuation),
      isPublic: form.isPublic,
    }
    gameClient.sendSocialAction('party.update', undefined, undefined, { party: partyPatch })
  }

  return (
    <section className="party-card">
      <h3>{t('social.manage.heading')}</h3>
      <div className="party-fields">
        <label className="party-field">
          <span>{t('social.field.name')}</span>
          <input maxLength={32} onChange={event => setName(event.target.value)} value={name} />
        </label>
      </div>
      <PartyProfileFields mapNames={mapNames} onChange={setForm} showPublic value={form} />
      <div className="party-card-actions">
        <button className="confirm-primary" onClick={submit} type="button">{t('social.action.saveProfile')}</button>
      </div>
    </section>
  )
})

/** 队伍分享区：世界/附近频道快捷分享 + 好友私聊定向分享。 */
const ShareSection = memo(function ShareSection(): React.JSX.Element {
  const t = useT()
  const friends = useSocialStore(state => state.friends)
  const nodeId = useGameStore(state => state.world.nodeId)
  const [friendId, setFriendId] = useState('')

  const share = (channel: ChatChannel): void => {
    gameClient.sendSocialAction('party.share', channel === 'friend' ? friendId || null : null, undefined, { channel })
  }

  return (
    <section className="party-card">
      <h3>{t('social.share.heading')}</h3>
      <p className="party-card-desc">{t('social.share.desc')}</p>
      <div className="party-card-actions">
        <button onClick={() => share('world')} type="button">{t('social.share.world')}</button>
        <button disabled={!nodeId} onClick={() => share('nearby')} type="button">{t('social.share.nearby')}</button>
        <DropdownSelect
          className="party-share-friend"
          label={t('social.share.friendField')}
          onSelect={setFriendId}
          options={[
            { id: '', label: t('social.share.pickFriend') },
            ...friends.map(friend => ({ id: friend.playerId, label: friend.displayName })),
          ]}
          value={friendId}
        />
        <button disabled={!friendId} onClick={() => share('friend')} type="button">{t('social.share.friend')}</button>
      </div>
    </section>
  )
})

/** 在队态页签：队伍信息 / 组队大厅（招募散人入口，邀请按钮仅队长可见）。 */
const PARTY_TABS: ReadonlyArray<{ id: 'overview' | 'lobby'; labelKey: string }> = [
  { id: 'overview', labelKey: 'social.tab.overview' },
  { id: 'lobby', labelKey: 'social.tab.lobby' },
]

/** 在队视图：档案头 + 「队伍信息/组队大厅」页签；信息页含成员表、队长管理、分享与离队/解散。 */
const PartyView = memo(function PartyView({ mapNames }: { mapNames: ReadonlyMap<string, string> }): React.JSX.Element | null {
  const t = useT()
  const party = useSocialStore(state => state.party)
  const myPlayerId = useGameStore(state => state.playerId)
  const friends = useSocialStore(state => state.friends)
  const partySubTab = useUiStore(state => state.partySubTab)
  const setPartySubTab = useUiStore(state => state.setPartySubTab)
  const [confirmLeave, setConfirmLeave] = useState(false)
  const [confirmDisband, setConfirmDisband] = useState(false)
  const [kickTarget, setKickTarget] = useState<{ playerId: string; name: string } | null>(null)
  if (!party) return null

  const isLeader = party.leaderId === myPlayerId
  // 在队态只认 overview/lobby 两页签；match/create 等无队伍态残留值回落为概览。
  const manageTab = partySubTab === 'lobby' ? 'lobby' : 'overview'
  const memberIds = new Set(party.members.map(member => member.playerId))
  const invitableFriends = friends.filter(friend => !memberIds.has(friend.playerId))

  return (
    <>
      <section className="party-card party-card--header">
        <div className="party-header">
          <h3 className="party-name">{party.name}</h3>
          <span className="badge">{t(`social.activity.${party.activityKind}`)}</span>
          <span className="badge">{mapLabel(party.mapId, mapNames, t('social.field.anyMap'))}</span>
          <span className={`badge ${party.isPublic ? '' : 'badge-danger'}`}>
            {party.isPublic ? t('social.visibility.public') : t('social.visibility.private')}
          </span>
          {party.minValuation > 0 && <span className="badge">{t('social.minValuationValue', { value: formatNumber(party.minValuation) })}</span>}
          <span className="party-lobby-count">{t('social.memberCount', { count: party.members.length })}</span>
        </div>
      </section>

      <nav aria-label={t('social.tab.aria')} className="market-tabs party-mode-tabs" role="tablist">
        {PARTY_TABS.map(tab => (
          <button
            aria-selected={manageTab === tab.id}
            className={manageTab === tab.id ? 'active' : ''}
            key={tab.id}
            onClick={() => setPartySubTab(tab.id)}
            role="tab"
            type="button"
          >{t(tab.labelKey)}</button>
        ))}
      </nav>

      {manageTab === 'lobby' ? (
        /* 大厅对在队成员开放浏览；散人条目上的「邀请」按钮仅队长可见（§21.5/§21.6）。 */
        <LobbySection mapNames={mapNames} />
      ) : (
        <>
          <section className="party-card">
            <h3>{t('social.members.heading')}</h3>
            <ul className="party-member-list">
              {party.members.map(member => (
                <li className="party-member" key={member.playerId}>
                  <span className="chat-entry-name">
                    {member.displayName}
                    {member.playerId === party.leaderId && <em className="chat-leader-mark">{t('social.members.leader')}</em>}
                  </span>
                  <span className={`chat-presence chat-presence--${member.online ? 'online' : 'offline'}`}>
                    {member.online ? t('social.online') : t('social.offline')}
                  </span>
                  <span className="party-member-valuation">{t('social.valuationValue', { value: formatNumber(member.valuation) })}</span>
                  {isLeader && member.playerId !== myPlayerId && (
                    <button onClick={() => setKickTarget({ playerId: member.playerId, name: member.displayName })} type="button">
                      {t('social.action.kick')}
                    </button>
                  )}
                </li>
              ))}
            </ul>
            {isLeader && invitableFriends.length > 0 && (
              <div className="party-invite-row">
                <span>{t('social.members.inviteFriend')}</span>
                {invitableFriends.map(friend => (
                  <button key={friend.playerId} onClick={() => gameClient.sendSocialAction('party.invite', friend.playerId)} type="button">
                    {friend.displayName}
                  </button>
                ))}
              </div>
            )}
          </section>

          {isLeader && <LeaderProfileEditor mapNames={mapNames} />}
          <ShareSection />

          <section className="party-card party-card--danger">
            <div className="party-card-actions">
              <button onClick={() => setConfirmLeave(true)} type="button">{t('social.action.leave')}</button>
              {isLeader && (
                <button className="danger" onClick={() => setConfirmDisband(true)} type="button">{t('social.action.disband')}</button>
              )}
            </div>
          </section>
        </>
      )}

      <ConfirmDialog
        description={isLeader ? t('social.confirm.leaveLeaderDesc') : t('social.confirm.leaveDesc')}
        onClose={() => setConfirmLeave(false)}
        onConfirm={() => gameClient.sendSocialAction('party.leave')}
        open={confirmLeave}
        title={t('social.confirm.leaveTitle')}
      />
      <ConfirmDialog
        danger
        description={t('social.confirm.disbandDesc')}
        onClose={() => setConfirmDisband(false)}
        onConfirm={() => gameClient.sendSocialAction('party.disband')}
        open={confirmDisband}
        title={t('social.confirm.disbandTitle')}
      />
      <ConfirmDialog
        danger
        description={t('social.confirm.kickDesc', { name: kickTarget?.name ?? '' })}
        onClose={() => setKickTarget(null)}
        onConfirm={() => { if (kickTarget) gameClient.sendSocialAction('party.kick', kickTarget.playerId) }}
        open={kickTarget !== null}
        title={t('social.confirm.kickTitle')}
      />
    </>
  )
})

/** 同道页子选项卡表（当前仅「队伍」一栏）。 */
const SOCIAL_SUB_TABS: ReadonlyArray<{ id: SocialSubTab; labelKey: string }> = [
  { id: 'party', labelKey: 'social.sub.party' },
]

/**
 * 同道页 (SocialPage)
 *
 * 顶级「同道」页签内容：子选项卡 + 队伍面板。无队伍时提供匹配登记/组队大厅/创建队伍三入口，
 * 有队伍时展示档案、成员与队长管理；所有数据以服务端 SocialState/PartyLobby 帧为唯一来源。
 */
export const SocialPage = memo(function SocialPage(): React.JSX.Element {
  const t = useT()
  const party = useSocialStore(state => state.party)
  const maps = useGameStore(state => state.maps)
  const socialSubTab = useUiStore(state => state.socialSubTab)
  const setSocialSubTab = useUiStore(state => state.setSocialSubTab)

  const mapNames = useMemo(() => {
    const names = new Map<string, string>()
    for (const map of maps) names.set(map.mapId, map.name)
    return names
  }, [maps])

  return (
    <section className="panel social-page">
      <header className="panel-header social-page-header">
        <h2>{t('social.title')}</h2>
        <nav aria-label={t('social.subAria')} className="market-tabs" role="tablist">
          {SOCIAL_SUB_TABS.map(tab => (
            <button
              aria-selected={socialSubTab === tab.id}
              className={socialSubTab === tab.id ? 'active' : ''}
              key={tab.id}
              onClick={() => setSocialSubTab(tab.id)}
              role="tab"
              type="button"
            >{t(tab.labelKey)}</button>
          ))}
        </nav>
      </header>
      <div className="social-page-body">
        {socialSubTab === 'party' && (party ? <PartyView mapNames={mapNames} /> : <PartylessView mapNames={mapNames} />)}
      </div>
    </section>
  )
})
