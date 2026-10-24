import { create } from 'zustand'

/**
 * 玩家主角本命立绘默认资源地址（128px WebP q80，矢量源母模归档于 art/avatars/）
 */
export const DEFAULT_PLAYER_AVATAR_SRC = '/avatars/player.webp'

/**
 * 支持的形象资源格式类型
 */
export type AvatarImageFormat = 'webp' | 'png' | 'svg'

/**
 * 标准已注册的形象映射表 (ID -> 默认最优格式与路径)
 */
export const REGISTERED_AVATAR_MAP: Record<string, { src: string; format: AvatarImageFormat }> = {
  // 玩家
  player: { src: '/avatars/player.webp', format: 'webp' },
  'player-female': { src: '/avatars/player.webp', format: 'webp' },
  'player-male': { src: '/avatars/player-male.webp', format: 'webp' },
  'avatar-gothic-lolita': { src: '/avatars/avatar-gothic-lolita.webp', format: 'webp' },
  'avatar-katana-jk': { src: '/avatars/avatar-katana-jk.webp', format: 'webp' },
  'avatar-mecha-petite': { src: '/avatars/avatar-mecha-petite.webp', format: 'webp' },
  'avatar-hoodie-jk': { src: '/avatars/avatar-hoodie-jk.webp', format: 'webp' },
  'avatar-magic-male': { src: '/avatars/avatar-magic-male.webp', format: 'webp' },
  'avatar-magic-female': { src: '/avatars/avatar-magic-female.webp', format: 'webp' },
  'avatar-fire-male': { src: '/avatars/avatar-fire-male.webp', format: 'webp' },
  'avatar-fire-female': { src: '/avatars/avatar-fire-female.webp', format: 'webp' },
  'avatar-body-male': { src: '/avatars/avatar-body-male.webp', format: 'webp' },
  'avatar-body-female': { src: '/avatars/avatar-body-female.webp', format: 'webp' },
  'avatar-wood-male': { src: '/avatars/avatar-wood-male.webp', format: 'webp' },
  'avatar-wood-female': { src: '/avatars/avatar-wood-female.webp', format: 'webp' },
  'avatar-star-male': { src: '/avatars/avatar-star-male.webp', format: 'webp' },
  'avatar-star-female': { src: '/avatars/avatar-star-female.webp', format: 'webp' },
  'magic-male': { src: '/avatars/avatar-magic-male.webp', format: 'webp' },
  'magic-female': { src: '/avatars/avatar-magic-female.webp', format: 'webp' },
  'fire-male': { src: '/avatars/avatar-fire-male.webp', format: 'webp' },
  'fire-female': { src: '/avatars/avatar-fire-female.webp', format: 'webp' },
  'body-male': { src: '/avatars/avatar-body-male.webp', format: 'webp' },
  'body-female': { src: '/avatars/avatar-body-female.webp', format: 'webp' },
  'wood-male': { src: '/avatars/avatar-wood-male.webp', format: 'webp' },
  'wood-female': { src: '/avatars/avatar-wood-female.webp', format: 'webp' },
  'star-male': { src: '/avatars/avatar-star-male.webp', format: 'webp' },
  'star-female': { src: '/avatars/avatar-star-female.webp', format: 'webp' },
  'gothic-lolita': { src: '/avatars/avatar-gothic-lolita.webp', format: 'webp' },
  'katana-jk': { src: '/avatars/avatar-katana-jk.webp', format: 'webp' },
  'mecha-petite': { src: '/avatars/avatar-mecha-petite.webp', format: 'webp' },
  'hoodie-jk': { src: '/avatars/avatar-hoodie-jk.webp', format: 'webp' },
  default: { src: '/avatars/player.webp', format: 'webp' },
  female: { src: '/avatars/player.webp', format: 'webp' },
  male: { src: '/avatars/player-male.webp', format: 'webp' },
  // T1 怪物 (练气期)
  'demonized-rabbit': { src: '/avatars/demonized-rabbit.webp', format: 'webp' },
  'wood-spirit': { src: '/avatars/wood-spirit.webp', format: 'webp' },
  'water-spirit': { src: '/avatars/water-spirit.webp', format: 'webp' },
  'fire-spirit': { src: '/avatars/fire-spirit.webp', format: 'webp' },
  'demon-ape': { src: '/avatars/demon-ape.webp', format: 'webp' },
  'armored-bear': { src: '/avatars/armored-bear.webp', format: 'webp' },
  'chang-tiger': { src: '/avatars/chang-tiger.webp', format: 'webp' },
  // T1 二图怪物 (青云古道)
  'green-wolf': { src: '/avatars/green-wolf.webp', format: 'webp' },
  'tide-spirit': { src: '/avatars/tide-spirit.webp', format: 'webp' },
  'iron-boar': { src: '/avatars/iron-boar.webp', format: 'webp' },
  'golden-ape-boss': { src: '/avatars/golden-ape-boss.webp', format: 'webp' },

  // T2 怪物 (筑基期)
  'rift-wolf': { src: '/avatars/rift-wolf.webp', format: 'webp' },
  'star-flame-crow': { src: '/avatars/star-flame-crow.webp', format: 'webp' },
  'plunder-cultivator': { src: '/avatars/plunder-cultivator.webp', format: 'webp' },
  'blade-corpse': { src: '/avatars/blade-corpse.webp', format: 'webp' },
  'fallen-devil-priest': { src: '/avatars/fallen-devil-priest.webp', format: 'webp' },
  'falling-star-beast': { src: '/avatars/falling-star-beast.webp', format: 'webp' },
  'xuanyuan-remnant': { src: '/avatars/xuanyuan-remnant.webp', format: 'webp' },
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
 * 根据 ID 智能生成回退候选 URL 列表（已注册项直接精准返回单一路径，未注册项尝试 webp 位图）
 */
export function getAvatarCandidateUrls(rawId?: string | null): readonly string[] {
  const id = normalizeAvatarId(rawId)

  if (!id) return [DEFAULT_PLAYER_AVATAR_SRC]

  const cached = AVATAR_URL_CACHE.get(id)
  if (cached) return cached

  const registered = REGISTERED_AVATAR_MAP[id]
  if (registered) {
    const direct: readonly string[] = [registered.src]
    AVATAR_URL_CACHE.set(id, direct)
    return direct
  }

  // 未注册未知 ID：尝试 webp 位图（运行时头像统一 128px WebP q80）
  const candidates: readonly string[] = [`/avatars/${id}.webp`]
  AVATAR_URL_CACHE.set(id, candidates)
  return candidates
}

/**
 * 玩家主角形象标识
 */
export type PlayerAvatarId =
  | 'default'
  | 'female'
  | 'male'
  | 'player'
  | 'player-male'
  | 'gothic-lolita'
  | 'katana-jk'
  | 'mecha-petite'
  | 'hoodie-jk'
  | 'magic-male'
  | 'magic-female'
  | 'fire-male'
  | 'fire-female'
  | 'body-male'
  | 'body-female'
  | 'wood-male'
  | 'wood-female'
  | 'star-male'
  | 'star-female'

/**
 * 规整化玩家形象标识（统一映射到已注册选项）
 */
export function normalizePlayerAvatarId(id?: string | null): PlayerAvatarId {
  if (!id) return 'default'
  const clean = id.trim().toLowerCase()
  if (clean === 'male' || clean === 'player-male') return 'male'
  if (clean === 'gothic-lolita' || clean === 'avatar-gothic-lolita') return 'gothic-lolita'
  if (clean === 'katana-jk' || clean === 'avatar-katana-jk') return 'katana-jk'
  if (clean === 'mecha-petite' || clean === 'avatar-mecha-petite') return 'mecha-petite'
  if (clean === 'hoodie-jk' || clean === 'avatar-hoodie-jk') return 'hoodie-jk'
  if (clean === 'magic-male' || clean === 'avatar-magic-male') return 'magic-male'
  if (clean === 'magic-female' || clean === 'avatar-magic-female') return 'magic-female'
  if (clean === 'fire-male' || clean === 'avatar-fire-male') return 'fire-male'
  if (clean === 'fire-female' || clean === 'avatar-fire-female') return 'fire-female'
  if (clean === 'body-male' || clean === 'avatar-body-male') return 'body-male'
  if (clean === 'body-female' || clean === 'avatar-body-female') return 'body-female'
  if (clean === 'wood-male' || clean === 'avatar-wood-male') return 'wood-male'
  if (clean === 'wood-female' || clean === 'avatar-wood-female') return 'wood-female'
  if (clean === 'star-male' || clean === 'avatar-star-male') return 'star-male'
  if (clean === 'star-female' || clean === 'avatar-star-female') return 'star-female'
  return 'default'
}

/** 可选形象条目（名称走 i18n 键，卡片只呈现图标与名称） */
export interface AvatarOption {
  /** 形象标识 */
  id: PlayerAvatarId
  /** 名称 i18n 键 */
  nameKey:
    | 'app.avatar.defaultName'
    | 'app.avatar.maleName'
    | 'app.avatar.gothicLolitaName'
    | 'app.avatar.katanaJkName'
    | 'app.avatar.mechaPetiteName'
    | 'app.avatar.hoodieJkName'
    | 'app.avatar.magicMaleName'
    | 'app.avatar.magicFemaleName'
    | 'app.avatar.fireMaleName'
    | 'app.avatar.fireFemaleName'
    | 'app.avatar.bodyMaleName'
    | 'app.avatar.bodyFemaleName'
    | 'app.avatar.woodMaleName'
    | 'app.avatar.woodFemaleName'
    | 'app.avatar.starMaleName'
    | 'app.avatar.starFemaleName'
  /** 立绘资源地址 */
  src: string
}

/** 玩家可选形象清单（涵盖传统剑修、二次元潮酷与十方仙道全流派男女立绘） */
export const AVATAR_OPTIONS: readonly AvatarOption[] = [
  {
    id: 'default',
    nameKey: 'app.avatar.defaultName',
    src: DEFAULT_PLAYER_AVATAR_SRC,
  },
  {
    id: 'male',
    nameKey: 'app.avatar.maleName',
    src: '/avatars/player-male.webp',
  },
  {
    id: 'magic-male',
    nameKey: 'app.avatar.magicMaleName',
    src: '/avatars/avatar-magic-male.webp',
  },
  {
    id: 'magic-female',
    nameKey: 'app.avatar.magicFemaleName',
    src: '/avatars/avatar-magic-female.webp',
  },
  {
    id: 'fire-male',
    nameKey: 'app.avatar.fireMaleName',
    src: '/avatars/avatar-fire-male.webp',
  },
  {
    id: 'fire-female',
    nameKey: 'app.avatar.fireFemaleName',
    src: '/avatars/avatar-fire-female.webp',
  },
  {
    id: 'body-male',
    nameKey: 'app.avatar.bodyMaleName',
    src: '/avatars/avatar-body-male.webp',
  },
  {
    id: 'body-female',
    nameKey: 'app.avatar.bodyFemaleName',
    src: '/avatars/avatar-body-female.webp',
  },
  {
    id: 'wood-male',
    nameKey: 'app.avatar.woodMaleName',
    src: '/avatars/avatar-wood-male.webp',
  },
  {
    id: 'wood-female',
    nameKey: 'app.avatar.woodFemaleName',
    src: '/avatars/avatar-wood-female.webp',
  },
  {
    id: 'star-male',
    nameKey: 'app.avatar.starMaleName',
    src: '/avatars/avatar-star-male.webp',
  },
  {
    id: 'star-female',
    nameKey: 'app.avatar.starFemaleName',
    src: '/avatars/avatar-star-female.webp',
  },
  {
    id: 'gothic-lolita',
    nameKey: 'app.avatar.gothicLolitaName',
    src: '/avatars/avatar-gothic-lolita.webp',
  },
  {
    id: 'katana-jk',
    nameKey: 'app.avatar.katanaJkName',
    src: '/avatars/avatar-katana-jk.webp',
  },
  {
    id: 'mecha-petite',
    nameKey: 'app.avatar.mechaPetiteName',
    src: '/avatars/avatar-mecha-petite.webp',
  },
  {
    id: 'hoodie-jk',
    nameKey: 'app.avatar.hoodieJkName',
    src: '/avatars/avatar-hoodie-jk.webp',
  },
]

/** 默认初始已解锁形象（仅限默认剑修两款：青萍 / 霜刃） */
export const DEFAULT_UNLOCKED_AVATARS: readonly PlayerAvatarId[] = ['default', 'male']

/** 玩家道貌形象选择状态 */
interface AvatarStore {
  /** 当前选中的形象标识 */
  avatarId: PlayerAvatarId
  /** 已解锁的形象标识清单 */
  unlockedAvatars: readonly PlayerAvatarId[]
  /** 切换形象并持久化到 localStorage */
  setAvatarId: (avatarId: PlayerAvatarId) => void
  /** 同步服务端已解锁形象列表 */
  setUnlockedAvatars: (avatars: readonly string[]) => void
  /** 查询指定形象是否已解锁 */
  isUnlocked: (avatarId?: PlayerAvatarId | string | null) => boolean
  /** 读取当前形象资源地址 */
  getAvatarSrc: () => string
}

/** 形象选择的本地持久化键，命名与账号/语言等既有存储键风格一致。 */
const AVATAR_STORAGE_KEY = 'daohua.avatar.v1'

/** 从 localStorage 读取上次选择的形象；存储不可用或值非法时回退默认。 */
function readStoredAvatarId(): PlayerAvatarId {
  try {
    const stored = window.localStorage.getItem(AVATAR_STORAGE_KEY)
    if (stored) return normalizePlayerAvatarId(stored)
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
  unlockedAvatars: DEFAULT_UNLOCKED_AVATARS,
  setAvatarId: (avatarId: PlayerAvatarId) => {
    const normalized = normalizePlayerAvatarId(avatarId)
    set({ avatarId: normalized })
    try {
      window.localStorage.setItem(AVATAR_STORAGE_KEY, normalized)
    } catch {
      // 持久化失败仅影响下次启动的默认选择，不阻断当前会话。
    }
  },
  setUnlockedAvatars: (avatars: readonly string[]) => {
    const setKeys = new Set<PlayerAvatarId>(DEFAULT_UNLOCKED_AVATARS)
    for (const raw of avatars) {
      setKeys.add(normalizePlayerAvatarId(raw))
    }
    const unlocked = Array.from(setKeys)
    const current = get().avatarId
    if (!setKeys.has(current)) {
      set({ unlockedAvatars: unlocked, avatarId: 'default' })
      try {
        window.localStorage.setItem(AVATAR_STORAGE_KEY, 'default')
      } catch {
        // ignore
      }
    } else {
      set({ unlockedAvatars: unlocked })
    }
  },
  isUnlocked: (avatarId?: PlayerAvatarId | string | null) => {
    const norm = normalizePlayerAvatarId(avatarId)
    if (norm === 'default' || norm === 'male') return true
    return get().unlockedAvatars.includes(norm)
  },
  getAvatarSrc: () => getPlayerAvatarSrc(get().avatarId),
}))

/**
 * 获取指定或当前形象资源地址
 */
export function getPlayerAvatarSrc(avatarId?: PlayerAvatarId | string | null): string {
  const id = normalizePlayerAvatarId(avatarId ?? useAvatarStore.getState().avatarId)
  return AVATAR_OPTIONS.find(option => option.id === id)?.src ?? DEFAULT_PLAYER_AVATAR_SRC
}

/**
 * 将形象标识符转换为立绘组件可加载的实体 ID
 */
export function resolvePlayerActorId(avatarId?: PlayerAvatarId | string | null): string {
  const norm = normalizePlayerAvatarId(avatarId ?? useAvatarStore.getState().avatarId)
  if (norm === 'default') return 'player'
  if (norm === 'male') return 'player-male'
  return norm
}
