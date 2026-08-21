import type { Messages } from './zh-CN'
import { commonEn } from '../domains/common.en'
import { appEn } from '../domains/app.en'
import { insightEn } from '../domains/insight.en'
import { marketEn } from '../domains/market.en'
import { combatEn } from '../domains/combat.en'
import { gameEn } from '../domains/game.en'

// 英文词典：形状必须与 zhCN 完全一致；缺键或多键都会在类型检查时报错。
export const enUS: Messages = {
  ...commonEn,
  ...appEn,
  ...insightEn,
  ...marketEn,
  ...combatEn,
  ...gameEn,
}
