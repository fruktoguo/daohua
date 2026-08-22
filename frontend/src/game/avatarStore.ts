import { create } from 'zustand'

/**
 * 玩家主角本命矢量立绘默认资源地址
 */
export const DEFAULT_PLAYER_AVATAR_SRC = '/avatars/player.svg'

/**
 * 玩家主角形象标识：
 * - 'default': 太虚绝世剑仙（本命矢量立绘）
 */
export type PlayerAvatarId = 'default' | 'female' | 'male' | 'player'

export interface AvatarOption {
  id: PlayerAvatarId
  nameKey: 'app.avatar.defaultName'
  descKey: 'app.avatar.defaultDesc'
  src: string
}

export const AVATAR_OPTIONS: readonly AvatarOption[] = [
  {
    id: 'default',
    nameKey: 'app.avatar.defaultName',
    descKey: 'app.avatar.defaultDesc',
    src: DEFAULT_PLAYER_AVATAR_SRC,
  },
]

interface AvatarStore {
  avatarId: PlayerAvatarId
  setAvatarId: (avatarId: PlayerAvatarId) => void
  getAvatarSrc: () => string
}

/**
 * 玩家道貌形象全局状态
 */
export const useAvatarStore = create<AvatarStore>(() => ({
  avatarId: 'default',
  setAvatarId: () => {},
  getAvatarSrc: () => DEFAULT_PLAYER_AVATAR_SRC,
}))

/**
 * 获取指定或当前形象资源地址 (SVG 矢量立绘)
 */
export function getPlayerAvatarSrc(_avatarId?: PlayerAvatarId | string | null): string {
  return DEFAULT_PLAYER_AVATAR_SRC
}
