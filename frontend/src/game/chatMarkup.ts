/**
 * 传音聊天行内富文本标记解析与构建器 (chatMarkup)
 *
 * 核心设计：
 * 1. 采用自包含紧凑文本标记，不破坏聊天历史存储与 WebSocket 通信信封，天然支持正文混排；
 * 2. 物品标记：{{item:itemId}} 或 {{item:itemId:enhancementLevel}}
 *    示例：{{item:equipment.weapon-sword-t1-iron:3}} -> 行内物品胶囊，带强化等级与悬浮卡片；
 * 3. 订单标记：{{order:orderId:side:itemId:enhancementLevel:price:quantity}}
 *    示例：{{order:ord-1:sell:equipment.weapon-sword-t1-iron:3:120:1}} -> 行内订单胶囊，标明挂售、强化、单价与数量；
 * 4. 容错与优雅降级：任何字段格式不合法或残缺的标记均作为普通文本处理，杜绝界面白屏或异常抛出。
 */

/** 传音正文解析出的分段类型 */
export type ChatMarkupSegment =
  | {
      type: 'text'
      text: string
    }
  | {
      type: 'item'
      itemId: string
      enhancementLevel: number
      raw: string
    }
  | {
      type: 'order'
      orderId: string
      side: 'buy' | 'sell'
      itemId: string
      enhancementLevel: number
      price: number
      quantity: number
      raw: string
    }

/** 行内超链接标记匹配正则：{{item:...}} 或 {{order:...}} */
const CHAT_MARKUP_PATTERN = /\{\{(item|order):([^}]+)\}\}/g

/**
 * 构建物品传音标记
 *
 * @param itemId 物品或装备唯一 ID
 * @param enhancementLevel 装备强化等级（默认为 0）
 */
export function buildItemChatMarkup(itemId: string, enhancementLevel = 0): string {
  const trimmedId = itemId.trim()
  const lvl = Math.max(0, Math.floor(enhancementLevel))
  if (lvl > 0) {
    return `{{item:${trimmedId}:${lvl}}}`
  }
  return `{{item:${trimmedId}}}`
}

/** 订单传音构建入参 */
export interface OrderMarkupParams {
  orderId: string
  side: 'buy' | 'sell'
  itemId: string
  enhancementLevel?: number
  price: number
  quantity: number
}

/**
 * 构建订单传音标记
 */
export function buildOrderChatMarkup({
  orderId,
  side,
  itemId,
  enhancementLevel = 0,
  price,
  quantity,
}: OrderMarkupParams): string {
  const trimmedOrderId = orderId.trim()
  const trimmedItemId = itemId.trim()
  const lvl = Math.max(0, Math.floor(enhancementLevel))
  return `{{order:${trimmedOrderId}:${side}:${trimmedItemId}:${lvl}:${price}:${quantity}}}`
}

/**
 * 解析传音正文字符串为富文本分段列表
 *
 * 纯函数：零副作用，保证异常输入安全。
 */
export function parseChatMarkup(body: string): readonly ChatMarkupSegment[] {
  if (!body) return []

  const segments: ChatMarkupSegment[] = []
  let lastIndex = 0

  CHAT_MARKUP_PATTERN.lastIndex = 0
  let match: RegExpExecArray | null

  while ((match = CHAT_MARKUP_PATTERN.exec(body)) !== null) {
    const matchStart = match.index
    const matchEnd = CHAT_MARKUP_PATTERN.lastIndex
    const raw = match[0]
    const kind = match[1]
    const payload = match[2]

    // 提取标记之前的纯文本
    if (matchStart > lastIndex) {
      segments.push({
        type: 'text',
        text: body.slice(lastIndex, matchStart),
      })
    }
    lastIndex = matchEnd

    if (kind === 'item') {
      const parts = payload.split(':')
      const itemId = parts[0]?.trim()
      if (!itemId) {
        segments.push({ type: 'text', text: raw })
        continue
      }
      let enhancementLevel = 0
      if (parts.length > 1) {
        const parsed = Number(parts[1])
        if (Number.isFinite(parsed) && parsed >= 0) {
          enhancementLevel = Math.floor(parsed)
        }
      }
      segments.push({
        type: 'item',
        itemId,
        enhancementLevel,
        raw,
      })
    } else if (kind === 'order') {
      const parts = payload.split(':')
      // 必须包含 6 项：orderId, side, itemId, enhancementLevel, price, quantity
      if (parts.length < 6) {
        segments.push({ type: 'text', text: raw })
        continue
      }
      const orderId = parts[0]?.trim()
      const side = parts[1]?.trim()
      const itemId = parts[2]?.trim()
      const enhancementLevel = Number(parts[3])
      const price = Number(parts[4])
      const quantity = Number(parts[5])

      if (
        !orderId ||
        (side !== 'buy' && side !== 'sell') ||
        !itemId ||
        !Number.isFinite(enhancementLevel) ||
        enhancementLevel < 0 ||
        !Number.isFinite(price) ||
        price < 0 ||
        !Number.isFinite(quantity) ||
        quantity <= 0
      ) {
        segments.push({ type: 'text', text: raw })
        continue
      }

      segments.push({
        type: 'order',
        orderId,
        side,
        itemId,
        enhancementLevel: Math.floor(enhancementLevel),
        price,
        quantity,
        raw,
      })
    } else {
      segments.push({ type: 'text', text: raw })
    }
  }

  // 提取末尾剩余纯文本
  if (lastIndex < body.length) {
    segments.push({
      type: 'text',
      text: body.slice(lastIndex),
    })
  }

  // 合并相邻的纯文本分段
  const merged: ChatMarkupSegment[] = []
  for (const seg of segments) {
    if (seg.type === 'text') {
      const prev = merged[merged.length - 1]
      if (prev && prev.type === 'text') {
        prev.text += seg.text
        continue
      }
      if (seg.text.length > 0) {
        merged.push(seg)
      }
    } else {
      merged.push(seg)
    }
  }

  return merged
}
