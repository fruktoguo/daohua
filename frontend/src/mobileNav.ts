import type { IconName } from './components/Icon'
import type { TabId } from './uiStore'

/**
 * 移动端底部导航分组模型（纯数据 + 纯函数，无 React 依赖）。
 *
 * 竖屏底部栏由「一级分组 + 组内子行」两级构成：一级 7 个常驻入口，
 * 组内子行在其上方浮现完成二次选择；单页签且无子行的分组不渲染子行，保持一击直达。
 */

/** 移动端底部导航分组标识 */
export type MobileNavGroupId = 'explore' | 'detail' | 'insight' | 'craft' | 'market' | 'bag' | 'more'

/**
 * 子行承载内容：
 * - `tabs`：组内多页签子导航（历练/地图、坊市/商店、更多组五项）；
 * - `crafting`：技艺模式切换条（炼丹/炼器/符箓/阵法/强化，含技艺等级）；
 * - `none`：无子行。
 */
export type MobileNavSubNav = 'tabs' | 'crafting' | 'none'

/** 分组名词条键（对应 `app.nav.<labelKey>`） */
export type MobileNavLabelKey = 'exploration' | 'combat' | 'insight' | 'crafting' | 'marketGroup' | 'inventory' | 'more'

export interface MobileNavGroup {
  readonly id: MobileNavGroupId
  readonly icon: IconName
  readonly labelKey: MobileNavLabelKey
  /** 组内页签；首项为首次点击该分组的默认落点 */
  readonly tabs: readonly TabId[]
  readonly subNav: MobileNavSubNav
}

/** 一级分组展示顺序：主线循环（历练→详情）在最左，收纳型「更多」在最右 */
export const MOBILE_NAV_GROUP_ORDER: readonly MobileNavGroupId[] = ['explore', 'detail', 'insight', 'craft', 'market', 'bag', 'more']

/** 分组定义真源（键即分组标识，展示顺序由 `MOBILE_NAV_GROUP_ORDER` 决定） */
export const MOBILE_NAV_GROUP_DEFS: { readonly [K in MobileNavGroupId]: Omit<MobileNavGroup, 'id'> } = {
  explore: { icon: 'exploration', labelKey: 'exploration', tabs: ['exploration', 'map'], subNav: 'tabs' },
  detail: { icon: 'combat', labelKey: 'combat', tabs: ['combat'], subNav: 'none' },
  insight: { icon: 'insight', labelKey: 'insight', tabs: ['insight'], subNav: 'none' },
  craft: { icon: 'crafting', labelKey: 'crafting', tabs: ['crafting'], subNav: 'crafting' },
  market: { icon: 'market', labelKey: 'marketGroup', tabs: ['market', 'shop'], subNav: 'tabs' },
  bag: { icon: 'inventory', labelKey: 'inventory', tabs: ['inventory'], subNav: 'none' },
  more: { icon: 'more', labelKey: 'more', tabs: ['social', 'codex', 'log', 'records', 'settings'], subNav: 'tabs' },
}

/** 一级分组列表（顺序即底部栏展示顺序） */
export const MOBILE_NAV_GROUPS: readonly MobileNavGroup[] = MOBILE_NAV_GROUP_ORDER.map(id => ({ id, ...MOBILE_NAV_GROUP_DEFS[id] }))

/**
 * 页签 → 所属分组。
 * `satisfies Record<TabId, ...>` 同时保证两件事：每个 TabId 都有归属（漏项编译失败），
 * 且不出现任何不存在的页签键（多余项编译失败）。
 */
const GROUP_OF_TAB = {
  exploration: 'explore',
  map: 'explore',
  combat: 'detail',
  insight: 'insight',
  crafting: 'craft',
  alchemy: 'craft',
  market: 'market',
  shop: 'market',
  inventory: 'bag',
  social: 'more',
  codex: 'more',
  log: 'more',
  records: 'more',
  settings: 'more',
} as const satisfies Record<TabId, MobileNavGroupId>

/** 页签所属分组标识 */
export function mobileNavGroupIdOf(tab: TabId): MobileNavGroupId {
  return GROUP_OF_TAB[tab]
}

/** 按标识取分组（供底部栏渲染与点击回调使用） */
export function mobileNavGroupById(id: MobileNavGroupId): MobileNavGroup {
  return { id, ...MOBILE_NAV_GROUP_DEFS[id] }
}

/** 子行页签的小图标（子行空间窄，图标只做辨认辅助，不参与状态表达） */
export const TAB_ICON: Readonly<Record<TabId, IconName>> = {
  insight: 'insight',
  market: 'market',
  shop: 'shop',
  exploration: 'exploration',
  crafting: 'crafting',
  alchemy: 'alchemy',
  map: 'map',
  combat: 'combat',
  social: 'social',
  codex: 'codex',
  inventory: 'inventory',
  log: 'log',
  records: 'records',
  settings: 'settings',
}

