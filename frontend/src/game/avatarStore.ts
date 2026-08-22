import { create } from 'zustand'

/**
 * 玩家主角本命矢量立绘默认资源地址
 */
export const DEFAULT_PLAYER_AVATAR_SRC = '/avatars/player.svg'

/**
 * 支持的形象资源格式类型
 */
export type AvatarImageFormat = 'webp' | 'png' | 'svg'

/**
 * 标准已注册的形象映射表 (ID -> 默认最优格式与路径)
 */
export const REGISTERED_AVATAR_MAP: Record<string, { src: string; format: AvatarImageFormat }> = {
  // 玩家
  player: { src: '/avatars/player.svg', format: 'svg' },
  'player-female': { src: '/avatars/player.svg', format: 'svg' },
  'player-male': { src: '/avatars/player.svg', format: 'svg' },

  // T1 怪物 (练气期)
  'demonized-rabbit': { src: '/avatars/demonized-rabbit.webp', format: 'webp' },
  'wood-spirit': { src: '/avatars/wood-spirit.webp', format: 'webp' },
  'fire-spirit': { src: '/avatars/fire-spirit.webp', format: 'webp' },
  'demon-ape': { src: '/avatars/demon-ape.webp', format: 'webp' },
  'armored-bear': { src: '/avatars/armored-bear.webp', format: 'webp' },
  'chang-tiger': { src: '/avatars/chang-tiger.webp', format: 'webp' },

  // T2 怪物 (筑基期)
  'rift-wolf': { src: '/avatars/rift-wolf.webp', format: 'webp' },
  'star-flame-crow': { src: '/avatars/star-flame-crow.webp', format: 'webp' },
  'plunder-cultivator': { src: '/avatars/plunder-cultivator.webp', format: 'webp' },
  'blade-corpse': { src: '/avatars/blade-corpse.webp', format: 'webp' },
  'fallen-devil-priest': { src: '/avatars/fallen-devil-priest.webp', format: 'webp' },
  'falling-star-beast': { src: '/avatars/falling-star-beast.webp', format: 'webp' },
}

/**
 * 规整化形象标识符 (去除 enemy. 或 player. 前缀、去除多余空格并转为小写)
 */
export function normalizeAvatarId(id?: string | null): string {
  if (!id) return ''
  let clean = id.trim().toLowerCase()
  if (clean.startsWith('enemy.')) clean = clean.slice(6)
  if (clean.startsWith('player.')) clean = clean.slice(7)
  return clean
}

/**
 * 根据 ID 智能生成多格式回退候选 URL 列表 (优先 WebP -> PNG -> SVG)
 */
export function getAvatarCandidateUrls(rawId?: string | null): string[] {
  const id = normalizeAvatarId(rawId)
  if (!id) return [DEFAULT_PLAYER_AVATAR_SRC]

  const registered = REGISTERED_AVATAR_MAP[id]
  const candidates: string[] = []

  if (registered) {
    candidates.push(registered.src)
  }

  // 补齐其余可能存在的扩展名候选 (自动适应 webp/png/svg)
  const extensions: AvatarImageFormat[] = ['webp', 'png', 'svg']
  for (const ext of extensions) {
    const url = `/avatars/${id}.${ext}`
    if (!candidates.includes(url)) {
      candidates.push(url)
    }
  }

  return candidates
}

/**
 * 玩家主角形象标识
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
 * 获取指定或当前形象资源地址
 */
export function getPlayerAvatarSrc(_avatarId?: PlayerAvatarId | string | null): string {
  return DEFAULT_PLAYER_AVATAR_SRC
}
