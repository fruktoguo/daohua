import type { ActionDefinition, ItemDefinition } from './types'

/**
 * 万象图鉴分类归一与筛选口径（纯函数，不依赖 React 与 store 运行时）。
 *
 * 与坊市 `marketFilters.ts` 同构：每个维度首项为「全部」（该维不过滤），其余取值互斥，
 * 维度之间统一 AND 联合。分类真源一律取配表字段（材料 `subCategory`、技艺 `technique`、
 * 建筑 `group`、功法 `discipline`/`category`、妖兽 `rank`、特质 `quality`），
 * 禁止按物品 ID 写特例分流。
 */

/** 材料子分类：直接对齐配表 `subCategory` 四类 */
export type CodexMaterialSubTab = 'all' | 'spiritMaterial' | 'mineral' | 'exoticMaterial' | 'refinedMaterial'

/** 技艺子分类：炼丹 / 炼器 / 阵法 / 符箓 */
export type CodexCraftingSubTab = 'all' | 'alchemy' | 'forging' | 'formation' | 'talisman'

/** 建筑分组：生产 / 战斗（对齐配表 `group`） */
export type CodexBuildingGroup = 'all' | 'production' | 'combat'

/** 功法门类：术法 / 内功 */
export type CodexAbilityDiscipline = 'all' | 'spell' | 'internal'

/** 功法伤害大类：物理 / 法术 */
export type CodexAbilityDamage = 'all' | 'physical' | 'spell'

/** 妖兽生命层次：普通 / 精英 / 妖王 */
export type CodexMonsterRank = 'all' | 'normal' | 'elite' | 'boss'

/** 特质品阶：全部 / 凡品 ~ 天品（对齐 `TRAIT_DEFINITIONS.quality` 1~5） */
export type CodexTraitGrade = 'all' | 'q1' | 'q2' | 'q3' | 'q4' | 'q5'

/** 收录状态：全部 / 已收录 / 未收录 */
export type CodexCollectFilter = 'all' | 'collected' | 'missing'

/** 图鉴筛选状态：各顶级分类各自持有一份，切页返回保持 */
export interface CodexFilterState {
  materialSubTab: CodexMaterialSubTab
  craftingSubTab: CodexCraftingSubTab
  buildingGroup: CodexBuildingGroup
  abilityDiscipline: CodexAbilityDiscipline
  abilityDamage: CodexAbilityDamage
  monsterRank: CodexMonsterRank
  traitGrade: CodexTraitGrade
  /** 等阶过滤：0 表示任意，1~8 对应品质档 */
  grade: number
  collect: CodexCollectFilter
}

/** 图鉴筛选默认值：各维度首项「全部」，等阶 0 表示任意 */
export const DEFAULT_CODEX_FILTERS: CodexFilterState = {
  materialSubTab: 'all',
  craftingSubTab: 'all',
  buildingGroup: 'all',
  abilityDiscipline: 'all',
  abilityDamage: 'all',
  monsterRank: 'all',
  traitGrade: 'all',
  grade: 0,
  collect: 'all',
}

/**
 * 材料子分类归一：直取配表 `subCategory`。
 * 未声明或未知 `subCategory` 的材料无法归类，返回 null（由配表完备性单测强制补齐）。
 */
export function resolveCodexMaterialSubTab(
  item: Pick<ItemDefinition, 'subCategory'>,
): Exclude<CodexMaterialSubTab, 'all'> | null {
  switch (item.subCategory) {
    case 'spiritMaterial':
    case 'mineral':
    case 'exoticMaterial':
    case 'refinedMaterial':
      return item.subCategory
    default:
      return null
  }
}

/**
 * 技艺子分类归一：`technique` 字段优先；缺失时按行动 ID 前缀兜底
 * （精炼/萃取类 `craft.*` 归属其实际技艺，与制造页同一口径）。
 */
export function resolveCodexCraftingSubTab(
  action: Pick<ActionDefinition, 'id' | 'technique'>,
): Exclude<CodexCraftingSubTab, 'all'> | null {
  const technique = action.technique
  if (technique === 'alchemy' || technique === 'forging' || technique === 'formation' || technique === 'talisman') {
    return technique
  }
  if (action.id.startsWith('alchemy.') || action.id.startsWith('craft.extract-')) return 'alchemy'
  if (action.id.startsWith('forge.') || action.id.startsWith('craft.refine-')) return 'forging'
  if (action.id.startsWith('formation.')) return 'formation'
  if (action.id.startsWith('talisman.')) return 'talisman'
  return null
}

/** 妖兽生命层次归一：妖王 / 精英 / 其余一律普通 */
export function resolveCodexMonsterRank(
  rank: string | null | undefined,
): Exclude<CodexMonsterRank, 'all'> {
  if (rank === 'boss') return 'boss'
  if (rank === 'elite') return 'elite'
  return 'normal'
}

/**
 * 等阶过滤：`grade` 为 0 表示任意；等阶缺省按 1 阶计（与 `resolveItemGrade` 归一一致）。
 */
export function matchesCodexGrade(tier: number | null | undefined, grade: number): boolean {
  if (grade <= 0) return true
  return Math.max(1, Math.floor(tier ?? 1)) === grade
}

/** 特质品阶过滤：'all' 表示不过滤；Q1~Q5 之外的品阶只在「全部」下可见 */
export function matchesCodexTraitGrade(quality: number, tab: CodexTraitGrade): boolean {
  if (tab === 'all') return true
  return quality >= 1 && quality <= 5 && tab === `q${Math.floor(quality)}`
}

/** 收录状态过滤：'all' 表示不过滤 */
export function matchesCodexCollect(filter: CodexCollectFilter, collected: boolean): boolean {
  return filter === 'all' || (filter === 'collected') === collected
}

/**
 * 按 bucket 统计池内条目的收录度（Toggle 角标数据源）。
 *
 * 每个 bucket 自带判定谓词，`all` bucket 的谓词恒真，因此一次遍历即可得到全部角标；
 * 角标口径固定为「已收录/总数」，与顶部大分类进度胶囊一致。
 */
export function tallyCodexBuckets<Row, Value extends string>(
  pool: readonly Row[],
  buckets: ReadonlyArray<{ value: Value; match: (row: Row) => boolean }>,
  isCollected: (row: Row) => boolean,
): Record<Value, { collected: number; total: number }> {
  const tally = {} as Record<Value, { collected: number; total: number }>
  for (const bucket of buckets) tally[bucket.value] = { collected: 0, total: 0 }
  for (const row of pool) {
    const collected = isCollected(row)
    for (const bucket of buckets) {
      if (!bucket.match(row)) continue
      const entry = tally[bucket.value]
      entry.total += 1
      if (collected) entry.collected += 1
    }
  }
  return tally
}
