import * as i18n from '../i18n/index.ts'
import type { TFunction } from '../i18n/index.ts'

export function isExternalLocaleActive(): boolean {
  const store = (i18n as { useI18nStore?: { getState?: () => { loadedLocale?: string } } }).useI18nStore
  const loaded = store?.getState?.()?.loadedLocale
  return Boolean(loaded && loaded !== 'zh-CN')
}

// 不把玩家可读的英文名或带点号的名称误判为键；只识别已知词典命名空间。
const TRANSLATION_KEY = /^(?:app|combat|common|game|insight|market|shop|social)(?:\.[\w-]+){2,}$/

/** 从跨边界传入的候选名称中取首个可显示值。ID 自身和附带强化等级的 ID 都不是名称。 */
export function readableEntityName(id: string, ...candidates: (string | null | undefined)[]): string | undefined {
  for (const candidate of candidates) {
    const name = candidate?.trim()
    if (!name || name === id || TRANSLATION_KEY.test(name)) continue
    if (id && name.startsWith(`${id} +`) && /^\d+$/.test(name.slice(id.length + 2))) continue
    return name
  }
  return undefined
}

const UNKNOWN_NAME_KEYS = {
  ability: 'common.unknownName.ability',
  action: 'common.unknownName.action',
  buff: 'common.unknownName.buff',
  building: 'common.unknownName.building',
  character: 'common.unknownName.character',
  enemy: 'common.unknownName.enemy',
  entity: 'common.unknownName.entity',
  equipment: 'common.unknownName.equipment',
  item: 'common.unknownName.item',
  location: 'common.unknownName.location',
  mechanic: 'common.unknownName.mechanic',
  trait: 'common.unknownName.trait',
} as const

export type EntityNameKind = keyof typeof UNKNOWN_NAME_KEYS

/** 根据实体类别与 ID 推导对应的标准 i18n 名称词条键 */
function entityI18nNameKey(kind: EntityNameKind | 'encounter', id: string): string | undefined {
  if (!id) return undefined
  switch (kind) {
    case 'item':
    case 'equipment':
      return `game.item.${id}.label`
    case 'ability':
      return `game.ability.${id}.name`
    case 'action':
      return `game.action.${id}.name`
    case 'building':
      return `game.building.${id}.name`
    case 'encounter':
      return `game.encounter.${id}.name`
    case 'enemy':
      return `game.monster.${id}.name`
    case 'location':
      return id.startsWith('map.') ? `game.map.${id}.name` : `game.node.${id}.name`
    case 'buff':
      return `combat.trigger.buff.${id.startsWith('buff.') ? id.slice(5) : id}`
    default:
      return undefined
  }
}

export function resolveEntityName(t: TFunction, kind: EntityNameKind, id: string, ...candidates: (string | null | undefined)[]): string {
  return readableEntityName(id, ...candidates) ?? t(UNKNOWN_NAME_KEYS[kind])
}

/**
 * 多语言实体名解析：外语包激活时优先按 (kind, id) 命中当前语言词典（避免服务端中文目录名覆盖译文），
 * 中文态或外语缺键时无缝回退候选目录名与分类兜底。
 */
export function resolveLocalizedEntityName(
  t: TFunction,
  kind: EntityNameKind | 'encounter',
  id: string,
  ...candidates: (string | null | undefined)[]
): string {
  const key = entityI18nNameKey(kind, id)
  const hasTranslationFn = (i18n as { hasTranslation?: (k: string) => boolean }).hasTranslation
  const i18nCandidate = key && (!hasTranslationFn || hasTranslationFn(key)) ? t(key) : undefined
  const fallbackKind: EntityNameKind = kind === 'encounter' ? 'location' : kind
  if (isExternalLocaleActive()) {
    return resolveEntityName(t, fallbackKind, id, i18nCandidate, ...candidates)
  }
  return resolveEntityName(t, fallbackKind, id, ...candidates, i18nCandidate)
}

/** 物品与装备共享词条空间；外语态优先当前语言词典，动态目录缺失时也能由内置词典恢复真实名称。 */
export function resolveAssetName(t: TFunction, id: string, ...candidates: (string | null | undefined)[]): string {
  const kind: EntityNameKind = id.startsWith('equipment.') ? 'equipment' : 'item'
  return resolveLocalizedEntityName(t, kind, id, ...candidates)
}
