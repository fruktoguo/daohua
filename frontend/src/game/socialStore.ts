import { create } from 'zustand'
import type { FriendDto, FriendRequestDto, NearbyPlayerDto, PartyDto, PartyInvitationDto, PartyLobbyDto, PartySeekerDto, SocialStateDto } from '../network/protocol'
import { gameClient } from '../network/gameClient'

/** 队伍开启槽位数下限（§21.13：2~5） */
export const PARTY_OPEN_SLOTS_MIN = 2
/** 队伍开启槽位数上限 */
export const PARTY_OPEN_SLOTS_MAX = 5

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
  /** 大厅回执携带的本人身价；尚未查询过为 null */
  myValuation: number | null
  /** 好友频道当前选中的私聊对象玩家 ID */
  activeFriendId: string | null
  /** 应用服务端下发的社交状态 */
  applyState: (state: SocialStateDto) => void
  /** 应用服务端下发的大厅视图（整体替换） */
  applyLobby: (lobby: PartyLobbyDto) => void
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
}

const EMPTY: Pick<SocialStore, 'friends' | 'incomingRequests' | 'outgoingRequests' | 'party' | 'partyInvitation' | 'seeking' | 'nearbyPlayers' | 'lobby' | 'myValuation' | 'activeFriendId'> = {
  friends: [],
  incomingRequests: [],
  outgoingRequests: [],
  party: null,
  partyInvitation: null,
  seeking: null,
  nearbyPlayers: [],
  lobby: null,
  myValuation: null,
  activeFriendId: null,
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
    }
  }),
  applyLobby: lobby => set({ lobby, myValuation: lobby.myValuation }),
  setActiveFriend: activeFriendId => set({ activeFriendId }),
  reset: () => set({ ...EMPTY }),
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
}))
