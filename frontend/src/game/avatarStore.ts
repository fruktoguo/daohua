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

/** 候选 URL 列表进程内缓存：候选集在会话内不变，避免每次渲染重建数组 */
const AVATAR_URL_CACHE = new Map<string, readonly string[]>()

/**
 * 根据 ID 智能生成多格式回退候选 URL 列表 (优先 WebP -> PNG -> SVG)
 */
export function getAvatarCandidateUrls(rawId?: string | null): readonly string[] {
  const id = normalizeAvatarId(rawId)
  if (!id) return [DEFAULT_PLAYER_AVATAR_SRC]

  const cached = AVATAR_URL_CACHE.get(id)
  if (cached) return cached

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

  AVATAR_URL_CACHE.set(id, candidates)
  return candidates
}

/**
 * 玩家主角形象标识
 */
export type PlayerAvatarId = 'default' | 'female' | 'male' | 'player'

/** 可选形象条目（名称/描述均走 i18n 键） */
export interface AvatarOption {
  /** 形象标识 */
  id: PlayerAvatarId
  /** 名称 i18n 键 */
  nameKey: 'app.avatar.defaultName'
  /** 描述 i18n 键 */
  descKey: 'app.avatar.defaultDesc'
  /** 立绘资源地址 */
  src: string
}

/** 玩家可选形象清单（当前仅默认法相，扩展时追加条目即可） */
export const AVATAR_OPTIONS: readonly AvatarOption[] = [
  {
    id: 'default',
    nameKey: 'app.avatar.defaultName',
    descKey: 'app.avatar.defaultDesc',
    src: DEFAULT_PLAYER_AVATAR_SRC,
  },
]

/** 玩家道貌形象选择状态 */
interface AvatarStore {
  /** 当前选中的形象标识 */
  avatarId: PlayerAvatarId
  /** 切换形象并持久化到 localStorage */
  setAvatarId: (avatarId: PlayerAvatarId) => void
  /** 读取当前形象资源地址 */
  getAvatarSrc: () => string
}

/** 形象选择的本地持久化键，命名与账号/语言等既有存储键风格一致。 */
const AVATAR_STORAGE_KEY = 'daohua.avatar.v1'

/** 从 localStorage 读取上次选择的形象；存储不可用或值非法时回退默认。 */
function readStoredAvatarId(): PlayerAvatarId {
  try {
    const stored = window.localStorage.getItem(AVATAR_STORAGE_KEY)
    if (stored && AVATAR_OPTIONS.some(option => option.id === stored)) return stored as PlayerAvatarId
  } catch {
    // 隐私模式等 localStorage 不可用场景：静默回退默认形象。
  }
  return 'default'
}

/**
 * 玩家道貌形象全局状态
 */
export const useAvatarStore = create<AvatarStore>((set, get) => ({
  avatarId: readStoredAvatarId(),
  setAvatarId: (avatarId: PlayerAvatarId) => {
    set({ avatarId })
    try {
      window.localStorage.setItem(AVATAR_STORAGE_KEY, avatarId)
    } catch {
      // 持久化失败仅影响下次启动的默认选择，不阻断当前会话。
    }
  },
  getAvatarSrc: () => getPlayerAvatarSrc(get().avatarId),
}))

/**
 * 获取指定或当前形象资源地址
 */
export function getPlayerAvatarSrc(avatarId?: PlayerAvatarId | string | null): string {
  const id = (avatarId ?? useAvatarStore.getState().avatarId) as PlayerAvatarId
  return AVATAR_OPTIONS.find(option => option.id === id)?.src ?? DEFAULT_PLAYER_AVATAR_SRC
}
