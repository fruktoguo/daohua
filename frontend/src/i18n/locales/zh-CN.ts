import { commonZh } from '../domains/common.zh'
import { appZh } from '../domains/app.zh'
import { insightZh } from '../domains/insight.zh'
import { marketZh } from '../domains/market.zh'
import { combatZh } from '../domains/combat.zh'
import { gameZh } from '../domains/game.zh'

// 中文源词典：新增文案先登记到对应域的 .zh.ts，再同步到同名 .en.ts。
// 各域根键（common/app/insight/market/combat/game）互不重叠，避免跨域冲突。
export const zhCN = {
  ...commonZh,
  ...appZh,
  ...insightZh,
  ...marketZh,
  ...combatZh,
  ...gameZh,
}

export type Messages = typeof zhCN
