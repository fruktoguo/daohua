import { create } from 'zustand'

/**
 * 玩家主角形象标识：
 * - 'female': 太虚绝世剑仙（女性，默认）
 * - 'male': 凌霄傲骨剑尊（男性，可选）
 */
export type PlayerAvatarId = 'female' | 'male'

export interface AvatarOption {
  id: PlayerAvatarId
  nameKey: 'app.avatar.femaleName' | 'app.avatar.maleName'
  descKey: 'app.avatar.femaleDesc' | 'app.avatar.maleDesc'
  src: string
}

export const AVATAR_OPTIONS: readonly AvatarOption[] = [
  {
    id: 'female',
    nameKey: 'app.avatar.femaleName',
    descKey: 'app.avatar.femaleDesc',
    src: '/avatars/female.webp',
  },
  {
    id: 'male',
    nameKey: 'app.avatar.maleName',
    descKey: 'app.avatar.maleDesc',
    src: '/avatars/male.webp',
  },
]

const STORAGE_KEY = 'daohua.player.avatar'

function getInitialAvatar(): PlayerAvatarId {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved === 'male' || saved === 'female') {
      return saved
    }
  } catch {
    // 忽略本地读取异常
  }
  return 'female' // 默认女性角色
}

interface AvatarStore {
  avatarId: PlayerAvatarId
  setAvatarId: (avatarId: PlayerAvatarId) => void
  getAvatarSrc: () => string
}

/**
 * 玩家道貌形象全局状态
 */
export const useAvatarStore = create<AvatarStore>((set, get) => ({
  avatarId: getInitialAvatar(),
  setAvatarId: (avatarId: PlayerAvatarId) => {
    try {
      localStorage.setItem(STORAGE_KEY, avatarId)
    } catch {
      // 忽略持久化异常
    }
    set({ avatarId })
  },
  getAvatarSrc: () => {
    const id = get().avatarId
    return id === 'male' ? '/avatars/male.webp' : '/avatars/female.webp'
  },
}))

/**
 * 获取指定或当前形象资源地址 (WebP 256x256)
 */
export function getPlayerAvatarSrc(avatarId?: PlayerAvatarId | string | null): string {
  if (avatarId === 'male') return '/avatars/male.webp'
  return '/avatars/female.webp'
}
