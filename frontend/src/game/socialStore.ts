import { create } from 'zustand'
import type { FriendDto, FriendRequestDto, NearbyPlayerDto, PartyDto, PartyInvitationDto, SocialStateDto } from '../network/protocol'

/**
 * 社交状态接口（好友、申请、队伍、附近修士）。
 * 数据完全来自服务端下发的 SocialState 帧：客户端不做任何本地推演，
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
  /** 同节点在线修士（含自己） */
  nearbyPlayers: readonly NearbyPlayerDto[]
  /** 好友频道当前选中的私聊对象玩家 ID */
  activeFriendId: string | null
  /** 应用服务端下发的社交状态 */
  applyState: (state: SocialStateDto) => void
  /** 选择好友频道的私聊对象 */
  setActiveFriend: (playerId: string | null) => void
  /** 清空社交状态（登出/断线重连前） */
  reset: () => void
}

const EMPTY: Pick<SocialStore, 'friends' | 'incomingRequests' | 'outgoingRequests' | 'party' | 'partyInvitation' | 'nearbyPlayers' | 'activeFriendId'> = {
  friends: [],
  incomingRequests: [],
  outgoingRequests: [],
  party: null,
  partyInvitation: null,
  nearbyPlayers: [],
  activeFriendId: null,
}

/**
 * 社交状态全局 Zustand Store。
 * 注意：选中好友在被删除或不再是好友时自动回落为 null，避免聊天面板停留在失效会话。
 */
export const useSocialStore = create<SocialStore>(set => ({
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
      nearbyPlayers: Array.isArray(state.nearbyPlayers) ? state.nearbyPlayers : [],
      activeFriendId: stillFriend ? current.activeFriendId : null,
    }
  }),
  setActiveFriend: activeFriendId => set({ activeFriendId }),
  reset: () => set({ ...EMPTY }),
}))
