import type { TFunction } from '../i18n'

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

export function resolveEntityName(t: TFunction, kind: keyof typeof UNKNOWN_NAME_KEYS, id: string, ...candidates: (string | null | undefined)[]): string {
  return readableEntityName(id, ...candidates) ?? t(UNKNOWN_NAME_KEYS[kind])
}

/** 物品与装备共享词条空间；动态目录缺失时还能由内置词典恢复真实名称。 */
export function resolveAssetName(t: TFunction, id: string, ...candidates: (string | null | undefined)[]): string {
  return readableEntityName(id, ...candidates)
    ?? resolveEntityName(t, id.startsWith('equipment.') ? 'equipment' : 'item', id, id ? t(`game.item.${id}.label`) : undefined)
}
