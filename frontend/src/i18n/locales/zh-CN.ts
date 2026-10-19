import { commonZh } from '../domains/common.zh'
import { appZh } from '../domains/app.zh'
import { insightZh } from '../domains/insight.zh'
import { marketZh } from '../domains/market.zh'
import { combatZh } from '../domains/combat.zh'
import { gameZh } from '../domains/game.zh'
import { socialZh } from '../domains/social.zh'
import { shopZh } from '../domains/shop.zh'

/**
 * 中文源词典：新增文案先登记到对应域的 .zh.ts，再同步到同名 .en.ts。
 * 各域根键（common/app/insight/market/combat/game/social）互不重叠，避免跨域冲突。
 */
export const zhCN = {
  ...commonZh,
  ...appZh,
  ...insightZh,
  ...marketZh,
  ...combatZh,
  ...gameZh,
  ...socialZh,
  ...shopZh,
}

/** 词条树类型：TranslationKey 的推导源，新增词条经此获得编译期键校验 */
export type Messages = typeof zhCN
