import { memo, useEffect, useMemo, useRef, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { PARTY_OPEN_SLOTS_MAX, PARTY_OPEN_SLOTS_MIN, useSocialStore } from '../game/socialStore'
import { findEncounterInCatalog } from '../game/ids'
import { formatNumber } from '../game/format'
import { calculateRealmInfo } from '../game/combatConstants'
import { useT } from '../i18n'
import { requestJson } from '../network/apiClient'
import { gameClient } from '../network/gameClient'
import { PartyActivityKind, type ChatChannel, type PartyActivityKind as PartyActivityKindType, type PartyProfileDto } from '../network/protocol'
import { useUiStore, type SocialSubTab } from '../uiStore'
import { ConfirmDialog } from './ConfirmDialog'

/** 队伍目标类型的固定选项集（协议值 + i18n 键后缀）：不限 + 战斗/历练/修炼 + 五门技艺 */
const ACTIVITY_OPTIONS: readonly PartyActivityKindType[] = [
  PartyActivityKind.Any,
  PartyActivityKind.Combat,
  PartyActivityKind.Exploration,
  PartyActivityKind.Cultivation,
  PartyActivityKind.Alchemy,
  PartyActivityKind.Forging,
  PartyActivityKind.Talisman,
  PartyActivityKind.Formation,
  PartyActivityKind.Enhancement,
]

/** 身价门槛输入框解析：空串/0 视为不设门槛（协议约定 minValuation=0），负数与非法输入钳制为 0。 */
function parseMinValuation(raw: string): number {
  const value = Number.parseFloat(raw)
  return Number.isFinite(value) && value > 0 ? value : 0
}

/** 节点显示名解析：nodeId 为空或未知时显示「不限」；nodeNames 为 nodeId →「区域 · 地点」标签。 */
function nodeLabel(nodeId: string, nodeNames: ReadonlyMap<string, string>, anyLabel: string): string {
  if (!nodeId) return anyLabel
  return nodeNames.get(nodeId) ?? nodeId
}

/** 表单默认目标节点：当前所在节点；行进在边上（nodeId=null）时回落「不限」。 */
function useDefaultNodeId(): string {
  return useGameStore(state => state.world.nodeId) ?? ''
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
  options: readonly { id: string; label: string; disabled?: boolean }[]
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
              disabled={option.disabled === true}
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
  nodeNames,
  showPublic = false,
}: {
  value: { activityKind: PartyActivityKindType; nodeId: string; minValuation: string; isPublic: boolean }
  onChange: (next: { activityKind: PartyActivityKindType; nodeId: string; minValuation: string; isPublic: boolean }) => void
  nodeNames: ReadonlyMap<string, string>
  showPublic?: boolean
}): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)
  // 目标节点选项：不限 + 全图节点，标签「区域 · 地点」。
  const nodeOptions = useMemo(() => [
    { id: '', label: t('social.field.anyNode') },
    ...maps.flatMap(map => map.nodes.map(node => ({ id: node.nodeId, label: nodeNames.get(node.nodeId) ?? `${map.name} · ${node.name}` }))),
  ], [maps, nodeNames, t])
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
        <span>{t('social.field.node')}</span>
        <DropdownSelect
          label={t('social.field.node')}
          onSelect={id => onChange({ ...value, nodeId: id })}
          options={nodeOptions}
          value={value.nodeId}
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

/** 开启槽位下拉固定选项集（2~5，§21.13.5）；低于当前队员数的档位由调用方禁用。 */
const SLOT_OPTIONS: readonly number[] = Array.from(
  { length: PARTY_OPEN_SLOTS_MAX - PARTY_OPEN_SLOTS_MIN + 1 },
  (_, index) => PARTY_OPEN_SLOTS_MIN + index,
)

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

/**
 * 组队讨伐活跃会话横幅（§21.13）：展示钉死遭遇、共战地点、开打时刻与参战人数；
 * 会话全程阵容锁定，横幅内附散场提示（成员变动即终止本次讨伐）。
 */
const PartySessionBanner = memo(function PartySessionBanner({ nodeNames }: { nodeNames: ReadonlyMap<string, string> }): React.JSX.Element | null {
  const t = useT()
  const session = useSocialStore(state => state.party?.activeSession ?? null)
  const encounters = useGameStore(state => state.encounters)
  if (!session) return null
  const encounterName = findEncounterInCatalog(encounters, session.encounterId)?.name ?? session.encounterId
  const nodeName = session.nodeId ? nodeLabel(session.nodeId, nodeNames, t('social.field.anyNode')) : t('social.field.anyNode')
  return (
    <div className="party-session" role="status">
      <strong className="party-session-title">{t('social.session.banner')}</strong>
      <span>{t('social.session.encounter', { name: encounterName })}</span>
      <span>{t('social.session.node', { name: nodeName })}</span>
      <span>{t('social.session.startedAt', { time: new Date(session.startedAtUnixMilliseconds).toLocaleTimeString() })}</span>
      <span>{t('social.session.members', { count: session.memberIds.length })}</span>
      <span className="party-session-warn">{t('social.session.disbandWarn')}</span>
    </div>
  )
})

/** 队伍匹配卡片：登记散人条件；已登记时展示当前条件与撤下入口。 */
const MatchSection = memo(function MatchSection({ nodeNames }: { nodeNames: ReadonlyMap<string, string> }): React.JSX.Element {
  const t = useT()
  const seeking = useSocialStore(state => state.seeking)
  const defaultNodeId = useDefaultNodeId()
  const [form, setForm] = useState({ activityKind: PartyActivityKind.Combat as PartyActivityKindType, nodeId: defaultNodeId, minValuation: '', isPublic: true })

  const submit = (): void => {
    const party: PartyProfileDto = {
      activityKind: form.activityKind,
      nodeId: form.nodeId,
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
          <span>{t(`social.activity.${seeking.activityKind}`)} · {nodeLabel(seeking.nodeId, nodeNames, t('social.field.anyNode'))}</span>
          {seeking.minValuation > 0 && <span>{t('social.minValuationValue', { value: formatNumber(seeking.minValuation) })}</span>}
          <button onClick={() => gameClient.sendSocialAction('party.match.clear')} type="button">{t('social.action.matchClear')}</button>
        </div>
      ) : (
        <>
          <PartyProfileFields nodeNames={nodeNames} onChange={setForm} value={form} />
          <div className="party-card-actions">
            <button className="confirm-primary" onClick={submit} type="button">{t('social.action.matchSet')}</button>
          </div>
        </>
      )}
    </section>
  )
})

/** 组队大厅卡片：筛选请求 PartyLobby，渲染公开队伍与匹配中散人。 */
const LobbySection = memo(function LobbySection({ nodeNames }: { nodeNames: ReadonlyMap<string, string> }): React.JSX.Element {
  const t = useT()
  const lobby = useSocialStore(state => state.lobby)
  const myValuation = useSocialStore(state => state.myValuation)
  const myPartyId = useSocialStore(state => state.party?.partyId ?? null)
  const myPlayerId = useGameStore(state => state.playerId)
  const isLeader = useSocialStore(state => {
    const party = state.party
    return party !== null && party.leaderId === myPlayerId
  })
  // 大厅是浏览工具：筛选默认「不限」全量展示，与创建/匹配表单的「战斗+当前地点」默认区分。
  const [filter, setFilter] = useState({ activityKind: PartyActivityKind.Any as PartyActivityKindType, nodeId: '', minValuation: '', isPublic: true })

  const refresh = (): void => {
    gameClient.requestPartyLobby({
      activityKind: filter.activityKind === PartyActivityKind.Any ? null : filter.activityKind,
      nodeId: filter.nodeId || null,
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
      <PartyProfileFields nodeNames={nodeNames} onChange={setFilter} value={filter} />
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
                <span className="party-lobby-node">{nodeLabel(entry.nodeId, nodeNames, t('social.field.anyNode'))}</span>
                <span className="party-lobby-count">
                  {entry.openSlots != null
                    ? t('social.memberCountOf', { count: entry.members.length, slots: entry.openSlots })
                    : t('social.memberCount', { count: entry.members.length })}
                </span>
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
              <span className="party-lobby-node">{nodeLabel(seeker.nodeId, nodeNames, t('social.field.anyNode'))}</span>
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
const CreateSection = memo(function CreateSection({ nodeNames }: { nodeNames: ReadonlyMap<string, string> }): React.JSX.Element {
  const t = useT()
  const [name, setName] = useState('')
  const defaultNodeId = useDefaultNodeId()
  const [form, setForm] = useState({ activityKind: PartyActivityKind.Combat as PartyActivityKindType, nodeId: defaultNodeId, minValuation: '', isPublic: true })

  const submit = (): void => {
    const party: PartyProfileDto = {
      name: name.trim() || null,
      activityKind: form.activityKind,
      nodeId: form.nodeId,
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
      <PartyProfileFields nodeNames={nodeNames} onChange={setForm} showPublic value={form} />
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
const PartylessView = memo(function PartylessView({ nodeNames }: { nodeNames: ReadonlyMap<string, string> }): React.JSX.Element {
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
      {entryTab === 'lobby' && <LobbySection nodeNames={nodeNames} />}
      {entryTab === 'match' && <MatchSection nodeNames={nodeNames} />}
      {entryTab === 'create' && <CreateSection nodeNames={nodeNames} />}
    </>
  )
})

/** 队长档案编辑区：改名 + 三件套 + 公开开关 + 开启槽位（槽位走独立 party.slots 指令即选即调）。 */
const LeaderProfileEditor = memo(function LeaderProfileEditor({ nodeNames }: { nodeNames: ReadonlyMap<string, string> }): React.JSX.Element | null {
  const t = useT()
  const party = useSocialStore(state => state.party)
  const setPartySlots = useSocialStore(state => state.setPartySlots)
  const [name, setName] = useState(party?.name ?? '')
  const [form, setForm] = useState({
    activityKind: (party?.activityKind ?? PartyActivityKind.Any) as PartyActivityKindType,
    nodeId: party?.nodeId ?? '',
    minValuation: party && party.minValuation > 0 ? String(party.minValuation) : '',
    isPublic: party?.isPublic ?? true,
  })
  // 会话进行中调整槽位需二次确认（§21.13：阵容锁定，变动即散场）
  const [pendingSlots, setPendingSlots] = useState<number | null>(null)
  if (!party) return null

  const submit = (): void => {
    const partyPatch: PartyProfileDto = {
      name: name.trim() || null,
      activityKind: form.activityKind,
      nodeId: form.nodeId,
      minValuation: parseMinValuation(form.minValuation),
      isPublic: form.isPublic,
    }
    gameClient.sendSocialAction('party.update', undefined, undefined, { party: partyPatch })
  }

  const openSlots = party.openSlots ?? PARTY_OPEN_SLOTS_MAX
  const selectSlots = (id: string): void => {
    const next = Number(id)
    // 低于当前队员数的档位在选项上即禁用；会话中调整先经确认弹窗提示散场。
    if (party.activeSession !== null && party.activeSession !== undefined) setPendingSlots(next)
    else setPartySlots(next)
  }

  return (
    <section className="party-card">
      <h3>{t('social.manage.heading')}</h3>
      <div className="party-fields">
        <label className="party-field">
          <span>{t('social.field.name')}</span>
          <input maxLength={32} onChange={event => setName(event.target.value)} value={name} />
        </label>
        <div className="party-field">
          <span>{t('social.field.openSlots')}</span>
          <DropdownSelect
            label={t('social.field.openSlots')}
            onSelect={selectSlots}
            options={SLOT_OPTIONS.map(n => ({ id: String(n), label: `${n}`, disabled: n < party.members.length }))}
            value={String(openSlots)}
          />
        </div>
      </div>
      <PartyProfileFields nodeNames={nodeNames} onChange={setForm} showPublic value={form} />
      <p className="party-card-desc">{t('social.field.openSlotsHint')}</p>
      <div className="party-card-actions">
        <button className="confirm-primary" onClick={submit} type="button">{t('social.action.saveProfile')}</button>
      </div>
      <ConfirmDialog
        description={`${t('social.confirm.slotsDesc', { slots: pendingSlots ?? openSlots })}${t('social.confirm.sessionSuffix')}`}
        onClose={() => setPendingSlots(null)}
        onConfirm={() => { if (pendingSlots !== null) setPartySlots(pendingSlots) }}
        open={pendingSlots !== null}
        title={t('social.confirm.slotsTitle')}
      />
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
const PartyView = memo(function PartyView({ nodeNames }: { nodeNames: ReadonlyMap<string, string> }): React.JSX.Element | null {
  const t = useT()
  const party = useSocialStore(state => state.party)
  const myPlayerId = useGameStore(state => state.playerId)
  const friends = useSocialStore(state => state.friends)
  const readyParty = useSocialStore(state => state.readyParty)
  const unreadyParty = useSocialStore(state => state.unreadyParty)
  const partySubTab = useUiStore(state => state.partySubTab)
  const setPartySubTab = useUiStore(state => state.setPartySubTab)
  const [confirmLeave, setConfirmLeave] = useState(false)
  const [confirmDisband, setConfirmDisband] = useState(false)
  const [confirmQuitSession, setConfirmQuitSession] = useState(false)
  const [kickTarget, setKickTarget] = useState<{ playerId: string; name: string } | null>(null)
  if (!party) return null

  const isLeader = party.leaderId === myPlayerId
  // 在队态只认 overview/lobby 两页签；match/create 等无队伍态残留值回落为概览。
  const manageTab = partySubTab === 'lobby' ? 'lobby' : 'overview'
  const memberIds = new Set(party.members.map(member => member.playerId))
  const invitableFriends = friends.filter(friend => !memberIds.has(friend.playerId))
  // 同图共战（§21.13）：开启槽位即队伍容量；会话进行中阵容冻结，本人参战开关转为「退出斗法」散场确认。
  const openSlots = party.openSlots ?? PARTY_OPEN_SLOTS_MAX
  const sessionActive = party.activeSession !== null && party.activeSession !== undefined
  const readyCount = party.members.filter(member => member.ready).length
  // 会话期间成员变动（离队/移出/收窄槽位）即散场：确认弹窗统一追加散场提示。
  const sessionSuffix = sessionActive ? t('social.confirm.sessionSuffix') : ''

  return (
    <>
      <section className="party-card party-card--header">
        <div className="party-header">
          <h3 className="party-name">{party.name}</h3>
          <span className="badge">{t(`social.activity.${party.activityKind}`)}</span>
          <span className="badge">{nodeLabel(party.nodeId, nodeNames, t('social.field.anyNode'))}</span>
          <span className={`badge ${party.isPublic ? '' : 'badge-danger'}`}>
            {party.isPublic ? t('social.visibility.public') : t('social.visibility.private')}
          </span>
          {party.minValuation > 0 && <span className="badge">{t('social.minValuationValue', { value: formatNumber(party.minValuation) })}</span>}
          <span className="party-lobby-count">{t('social.memberCountOf', { count: party.members.length, slots: openSlots })}</span>
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
        <LobbySection nodeNames={nodeNames} />
      ) : (
        <>
          <PartySessionBanner nodeNames={nodeNames} />
          <section className="party-card">
            <h3>{t('social.members.heading')}</h3>
            <div className="party-ready-row">
              <span className="badge">{t('social.ready.count', { ready: readyCount, count: party.members.length })}</span>
              <span className="party-card-desc">{t('social.ready.hint')}</span>
            </div>
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
                  <span className={`party-ready-tag${member.ready ? ' is-ready' : ''}`}>
                    {member.ready ? t('social.ready.status') : t('social.ready.notReady')}
                  </span>
                  <span className="party-member-valuation">{t('social.valuationValue', { value: formatNumber(member.valuation) })}</span>
                  {member.playerId === myPlayerId && (
                    <button
                      className={sessionActive ? 'danger' : undefined}
                      onClick={() => {
                        // 会话中取消参战=散场（名册冻结铁律），需确认；待命期自由开关。
                        if (sessionActive) setConfirmQuitSession(true)
                        else if (member.ready) unreadyParty()
                        else readyParty()
                      }}
                      type="button"
                    >{sessionActive
                      ? t('social.action.quitParticipation')
                      : member.ready ? t('social.action.leaveParticipation') : t('social.action.participate')}</button>
                  )}
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

          {isLeader && <LeaderProfileEditor nodeNames={nodeNames} />}
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
        description={(isLeader ? t('social.confirm.leaveLeaderDesc') : t('social.confirm.leaveDesc')) + sessionSuffix}
        onClose={() => setConfirmLeave(false)}
        onConfirm={() => gameClient.sendSocialAction('party.leave')}
        open={confirmLeave}
        title={t('social.confirm.leaveTitle')}
      />
      <ConfirmDialog
        danger
        description={t('social.confirm.quitSessionDesc')}
        onClose={() => setConfirmQuitSession(false)}
        onConfirm={() => unreadyParty()}
        open={confirmQuitSession}
        title={t('social.confirm.quitSessionTitle')}
      />
      <ConfirmDialog
        danger
        description={t('social.confirm.disbandDesc') + sessionSuffix}
        onClose={() => setConfirmDisband(false)}
        onConfirm={() => gameClient.sendSocialAction('party.disband')}
        open={confirmDisband}
        title={t('social.confirm.disbandTitle')}
      />
      <ConfirmDialog
        danger
        description={t('social.confirm.kickDesc', { name: kickTarget?.name ?? '' }) + sessionSuffix}
        onClose={() => setKickTarget(null)}
        onConfirm={() => { if (kickTarget) gameClient.sendSocialAction('party.kick', kickTarget.playerId) }}
        open={kickTarget !== null}
        title={t('social.confirm.kickTitle')}
      />
    </>
  )
})

/** 同道页子选项卡表：队伍 + 邀请。 */
const SOCIAL_SUB_TABS: ReadonlyArray<{ id: SocialSubTab; labelKey: string }> = [
  { id: 'party', labelKey: 'social.sub.party' },
  { id: 'invite', labelKey: 'social.sub.invite' },
]

/** GET /api/v1/invitation 的响应形态（HTTP 低频拉取，不进 WebSocket 协议帧）。 */
interface InvitationOverviewDto {
  inviteCode: string
  inviteeCount: number
  invitees: {
    displayName: string
    highestRealmLevel: number
    qiRewarded: boolean
    foundationRewarded: boolean
    createdAt: string
  }[]
  invitedByName: string | null
}

/** 剪贴板写入：优先异步 Clipboard API，非安全上下文降级 execCommand；返回是否成功。 */
async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch { /* 继续走降级路径 */ }
  try {
    const helper = document.createElement('textarea')
    helper.value = text
    helper.style.position = 'fixed'
    helper.style.opacity = '0'
    document.body.appendChild(helper)
    helper.select()
    const ok = document.execCommand('copy')
    document.body.removeChild(helper)
    return ok
  } catch {
    return false
  }
}

/**
 * 邀请面板 (InviteView)：展示专属邀请码/邀请链接并提供一键复制，
 * 下方列出被邀请人境界进度与练气/筑基里程碑达成状态；数据来自 GET /api/v1/invitation。
 */
const InviteView = memo(function InviteView(): React.JSX.Element | null {
  const t = useT()
  const [overview, setOverview] = useState<InvitationOverviewDto | null>(null)
  const [loadFailed, setLoadFailed] = useState(false)
  const [copiedKey, setCopiedKey] = useState<'code' | 'link' | null>(null)
  const copyTimerRef = useRef<number | null>(null)

  useEffect(() => {
    let cancelled = false
    requestJson<InvitationOverviewDto>('/api/v1/invitation')
      .then(dto => {
        if (cancelled) return
        setOverview(dto)
        setLoadFailed(false)
      })
      .catch(() => { if (!cancelled) setLoadFailed(true) })
    return () => {
      cancelled = true
      if (copyTimerRef.current !== null) window.clearTimeout(copyTimerRef.current)
    }
  }, [])

  const inviteLink = overview ? `${window.location.origin}${window.location.pathname}?invite=${encodeURIComponent(overview.inviteCode)}` : ''

  const handleCopy = (key: 'code' | 'link', text: string) => {
    copyText(text).then(ok => {
      if (!ok) return
      setCopiedKey(key)
      if (copyTimerRef.current !== null) window.clearTimeout(copyTimerRef.current)
      copyTimerRef.current = window.setTimeout(() => setCopiedKey(null), 1600)
    })
  }

  if (loadFailed) return <div className="party-card"><p className="party-card-desc">{t('social.invite.loadFailed')}</p></div>
  if (overview === null) return null

  return (
    <div className="invite-panel">
      <div className="party-card">
        <h3>{t('social.invite.title')}</h3>
        <div className="invite-row">
          <span className="invite-label">{t('social.invite.codeLabel')}</span>
          <code className="invite-code">{overview.inviteCode}</code>
          <button type="button" className="invite-copy-btn" onClick={() => handleCopy('code', overview.inviteCode)}>
            {copiedKey === 'code' ? t('social.invite.copied') : t('social.invite.copy')}
          </button>
        </div>
        <div className="invite-row">
          <span className="invite-label">{t('social.invite.linkLabel')}</span>
          <code className="invite-code invite-code--link">{inviteLink}</code>
          <button type="button" className="invite-copy-btn" onClick={() => handleCopy('link', inviteLink)}>
            {copiedKey === 'link' ? t('social.invite.copied') : t('social.invite.copy')}
          </button>
        </div>
        {overview.invitedByName !== null && (
          <p className="party-card-desc">{t('social.invite.invitedBy', { name: overview.invitedByName })}</p>
        )}
      </div>

      <div className="party-card">
        <h3>{t('social.invite.rulesTitle')}</h3>
        <p className="party-card-desc">{t('social.invite.rewardSignup')}</p>
        <p className="party-card-desc">{t('social.invite.rewardQi')}</p>
        <p className="party-card-desc">{t('social.invite.rewardFoundation')}</p>
      </div>

      <div className="party-card">
        <h3>{t('social.invite.listTitle', { count: overview.inviteeCount })}</h3>
        {overview.invitees.length === 0 ? (
          <p className="party-card-desc">{t('social.invite.empty')}</p>
        ) : (
          <div className="invite-list">
            {overview.invitees.map(invitee => (
              <div className="invite-list-row" key={invitee.displayName}>
                <span className="invite-name">{invitee.displayName}</span>
                <span className="invite-realm">{calculateRealmInfo(0, invitee.highestRealmLevel).minorName}</span>
                <span className={`invite-milestone${invitee.qiRewarded ? ' is-done' : ''}`}>{t('social.invite.milestoneQi')}</span>
                <span className={`invite-milestone${invitee.foundationRewarded ? ' is-done' : ''}`}>{t('social.invite.milestoneFoundation')}</span>
                <span className="invite-date">{new Date(invitee.createdAt).toLocaleDateString()}</span>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
})

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

  // 节点标签表：nodeId →「区域 · 地点」，选项与展示徽标共用同一解析口径。
  const nodeNames = useMemo(() => {
    const names = new Map<string, string>()
    for (const map of maps) for (const node of map.nodes) names.set(node.nodeId, `${map.name} · ${node.name}`)
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
        {socialSubTab === 'party' && (party ? <PartyView nodeNames={nodeNames} /> : <PartylessView nodeNames={nodeNames} />)}
        {socialSubTab === 'invite' && <InviteView />}
      </div>
    </section>
  )
})
