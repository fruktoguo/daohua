import { create } from 'zustand'
import type {
  FriendDto,
  FriendRequestDto,
  NearbyPlayerDto,
  PartyDto,
  PartyInvitationDto,
  PartyLobbyDto,
  PartySeekerDto,
  PlayerCardDto,
  RespawnPolicyCommandPayload,
  SocialStateDto,
  ValuationBreakdownDto,
} from '../network/protocol'
import type { BattleReport, RespawnPolicy } from './types'
import { gameClient } from '../network/gameClient'
import { useGameStore } from './gameStore'

/** 队伍开启槽位数下限（§21.13：2~5） */
export const PARTY_OPEN_SLOTS_MIN = 2
/** 队伍开启槽位数上限 */
export const PARTY_OPEN_SLOTS_MAX = 5


/** 队伍成员当前行动收益缓存项 */
export interface TeammateReportEntry {
  report: BattleReport | null
  loading: boolean
  updatedAtMs: number
}

/** 存储在途的队友行动收益请求（requestId → playerId） */
const teammatePendingReqs = new Map<string, string>()
/**
 * 社交状态接口（好友、申请、队伍、匹配登记、附近修士）。
 * 数据完全来自服务端下发的 SocialState / PartyLobby 帧：客户端不做任何本地推演，
 * 只维护「好友频道当前选中的私聊对象」这类纯视图状态。
 */
interface SocialStore {
  /** 好友列表 */
  friends: readonly FriendDto[]
  /** 收到的好友申请 */
  incomingRequests: readonly FriendRequestDto[]
  /** 发出的好友申请 */
  outgoingRequests: readonly FriendRequestDto[]
  /** 所在队伍；未组队为 null */
  party: PartyDto | null
  /** 待处理的队伍邀请 */
  partyInvitation: PartyInvitationDto | null
  /** 本人「匹配中」散人登记；未登记为 null */
  seeking: PartySeekerDto | null
  /** 同节点在线修士（含自己） */
  nearbyPlayers: readonly NearbyPlayerDto[]
  /** 最近一次组队大厅查询结果（PartyLobby 帧，服务端权威视图） */
  lobby: PartyLobbyDto | null
  /** 大厅回执携带的本人身家；尚未查询过为 null */
  myValuation: number | null
  /** 大厅回执或同节点自身投影携带的本人身家细分；尚未获取时为 null */
  myValuationBreakdown: ValuationBreakdownDto | null
  /** 当前展示的角色信息卡（PlayerCard 帧应答，含 error 字段的失败应答）；未打开为 null */
  playerCard: PlayerCardDto | null
  /** 角色信息卡在途请求 ID：请求已发出、应答未回，弹窗处于加载态；空闲为 null */
  playerCardRequestId: string | null
  /** 好友频道当前选中的私聊对象玩家 ID */
  activeFriendId: string | null
  /** 队伍成员当前行动收益缓存（键为 playerId） */
  teammateReports: Readonly<Record<string, TeammateReportEntry>>
  /** 请求指定队伍成员的当前行动收益（复用 PlayerCardRequest） */
  requestTeammateReport: (playerId: string) => void
  /** 应用服务端下发的社交状态 */
  applyState: (state: SocialStateDto) => void
  /** 应用服务端下发的大厅视图（整体替换） */
  applyLobby: (lobby: PartyLobbyDto) => void
  /** 发起角色信息卡查询（§21.14）：发送成功即进入加载态，失败不打开弹窗 */
  requestPlayerCard: (playerId: string) => void
  /** 应用 PlayerCard 帧：仅接收与在途请求 ID 匹配的应答，迟到旧应答直接丢弃 */
  applyPlayerCard: (card: PlayerCardDto) => void
  /** 关闭角色信息卡并清理在途请求（断线时亦调用，避免加载态悬挂） */
  closePlayerCard: () => void
  /** 选择好友频道的私聊对象 */
  setActiveFriend: (playerId: string | null) => void
  /** 清空社交状态（登出/断线重连前） */
  reset: () => void
  /**
   * 本人切换为已准备（party.ready 社交指令）。
   * 无队伍、本人已准备或会话进行中（阵容锁定）时不派发，返回 false。
   */
  readyParty: () => boolean
  /**
   * 本人取消准备（party.unready 社交指令）。
   * 无队伍或本人未准备时不派发；会话进行中取消即触发全队散场（服务端裁决），仍允许派发。
   */
  unreadyParty: () => boolean
  /**
   * 队长设置开启槽位数（party.slots 社交指令，party 载荷携带 openSlots）。
   * 校验：整数且 2~5、不得低于当前队员数（§21.13.5 收窄需先移出成员）；不合法返回 false。
   */
  setPartySlots: (openSlots: number) => boolean
  /**
   * 队长切换队伍斗法开关（party.update 社交指令，party 载荷携带 pvpEnabled）。
   * §7.10.4：整队作为一个匹配单位参与 PVP 撮合；仅讨伐目标可置位、仅队长可改，
   * 会话存续期间修改由服务端裁决散场重建。非队长/非讨伐/同值不派发，返回 false。
   */
  setPartyPvp: (pvpEnabled: boolean) => boolean
  /**
   * 本人设定组队讨伐的复活时机策略（respawn.policy 命令，写本人 settings，不影响队友）。
   * 校验：策略为三值之一；`target-ally-alive` 必须指定当前队伍中的其他成员（不能是自己）。
   * 目标无变化时不派发，返回 false。
   */
  setRespawnPolicy: (policy: RespawnPolicy, targetPlayerId?: string | null) => boolean
}

const EMPTY: Pick<SocialStore, 'friends' | 'incomingRequests' | 'outgoingRequests' | 'party' | 'partyInvitation' | 'seeking' | 'nearbyPlayers' | 'lobby' | 'myValuation' | 'myValuationBreakdown' | 'playerCard' | 'playerCardRequestId' | 'activeFriendId' | 'teammateReports'> = {
  friends: [],
  incomingRequests: [],
  outgoingRequests: [],
  party: null,
  partyInvitation: null,
  seeking: null,
  nearbyPlayers: [],
  lobby: null,
  myValuation: null,
  myValuationBreakdown: null,
  playerCard: null,
  playerCardRequestId: null,
  activeFriendId: null,
  teammateReports: {},
}

/**
 * 社交状态全局 Zustand Store。
 * 注意：选中好友在被删除或不再是好友时自动回落为 null，避免聊天面板停留在失效会话。
 */
export const useSocialStore = create<SocialStore>((set, get) => ({
  ...EMPTY,
  applyState: state => set(current => {
    const friends = Array.isArray(state.friends) ? state.friends : []
    const stillFriend = current.activeFriendId !== null && friends.some(friend => friend.playerId === current.activeFriendId)
    return {
      friends,
      incomingRequests: Array.isArray(state.incomingRequests) ? state.incomingRequests : [],
      outgoingRequests: Array.isArray(state.outgoingRequests) ? state.outgoingRequests : [],
      party: state.party ?? null,
      partyInvitation: state.partyInvitation ?? null,
      seeking: state.seeking ?? null,
      nearbyPlayers: Array.isArray(state.nearbyPlayers) ? state.nearbyPlayers : [],
      activeFriendId: stillFriend ? current.activeFriendId : null,
      myValuation: (() => {
        const myId = useGameStore.getState().playerId
        const me = myId ? state.nearbyPlayers?.find(p => p.playerId === myId) : null
        return me?.valuation ?? current.myValuation
      })(),
      myValuationBreakdown: (() => {
        const myId = useGameStore.getState().playerId
        const me = myId ? state.nearbyPlayers?.find(p => p.playerId === myId) : null
        return me?.valuationBreakdown ?? current.myValuationBreakdown
      })(),
    }
  }),
  applyLobby: lobby => set({
    lobby,
    myValuation: lobby.myValuation,
    myValuationBreakdown: lobby.myValuationBreakdown ?? null,
  }),
  requestPlayerCard: playerId => {
    const requestId = gameClient.requestPlayerCard(playerId)
    // 发送失败（断线）不进入加载态；成功则以 requestId 配对后续应答。
    if (requestId === null) return
    set({ playerCard: null, playerCardRequestId: requestId })
  },
  requestTeammateReport: playerId => {
    const requestId = gameClient.requestPlayerCard(playerId)
    if (requestId === null) return
    teammatePendingReqs.set(requestId, playerId)
    set(curr => ({
      teammateReports: {
        ...curr.teammateReports,
        [playerId]: {
          report: curr.teammateReports[playerId]?.report ?? null,
          loading: true,
          updatedAtMs: curr.teammateReports[playerId]?.updatedAtMs ?? 0,
        },
      },
    }))
  },
  applyPlayerCard: card => {
    const isTeammateReq = teammatePendingReqs.has(card.requestId)
    if (isTeammateReq) {
      teammatePendingReqs.delete(card.requestId)
    }
    if (card.playerId) {
      set(curr => ({
        teammateReports: {
          ...curr.teammateReports,
          [card.playerId]: {
            report: card.currentReport ?? null,
            loading: false,
            updatedAtMs: Date.now(),
          },
        },
      }))
    }
    // 若仅是查询队友行动收益，不触发 PlayerCardModal 弹窗
    if (isTeammateReq && card.requestId !== get().playerCardRequestId) return

    if (card.requestId !== get().playerCardRequestId) return
    set({ playerCard: card, playerCardRequestId: null })
  },
  closePlayerCard: () => set({ playerCard: null, playerCardRequestId: null }),
  setActiveFriend: activeFriendId => set({ activeFriendId }),
  reset: () => {
    teammatePendingReqs.clear()
    set({ ...EMPTY })
  },
  readyParty: () => {
    const party = get().party
    if (!party || party.activeSession) return false
    const self = party.members.find(member => member.playerId === gameClient.playerId)
    if (self?.ready) return false
    return gameClient.sendSocialAction('party.ready')
  },
  unreadyParty: () => {
    const party = get().party
    if (!party) return false
    const self = party.members.find(member => member.playerId === gameClient.playerId)
    if (self !== undefined && !self.ready) return false
    return gameClient.sendSocialAction('party.unready')
  },
  setPartySlots: openSlots => {
    const party = get().party
    if (!party || party.leaderId !== gameClient.playerId) return false
    if (!Number.isInteger(openSlots) || openSlots < PARTY_OPEN_SLOTS_MIN || openSlots > PARTY_OPEN_SLOTS_MAX) return false
    if (openSlots < party.members.length) return false
    if (openSlots === party.openSlots) return false
    return gameClient.sendSocialAction('party.slots', undefined, undefined, { party: { openSlots } })
  },
  setPartyPvp: pvpEnabled => {
    const party = get().party
    if (!party || party.leaderId !== gameClient.playerId) return false
    if (party.activityKind !== 'combat') return false
    if (party.pvpEnabled === pvpEnabled) return false
    return gameClient.sendSocialAction('party.update', undefined, undefined, { party: { pvpEnabled } })
  },
  setRespawnPolicy: (policy, targetPlayerId) => {
    // 复活策略是玩家自己的设置：不校验队伍、不校验队长，本人存什么就是什么。
    // 「指定队友存活」的观察对象允许先于组队设定（离线设置、换队后沿用），
    // 是否真在会话名册内由战斗侧按成员序解析，不在册时退化为直接复活。
    const target = policy === 'target-ally-alive' ? (targetPlayerId ?? null) : null
    if (policy === 'target-ally-alive' && target === null) return false
    // 与服务端下发状态同值时不派发，避免每次点击都产生一次无意义写盘与快照推送。
    const current = useGameStore.getState()
    if (current.respawnPolicy === policy && current.respawnTargetPlayerId === target) return false
    const payload: RespawnPolicyCommandPayload = target === null ? { policy } : { policy, targetPlayerId: target }
    return gameClient.sendCommand('respawn.policy', payload)
  },
}))
