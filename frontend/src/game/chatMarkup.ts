import { QQ_EMOJI_MAP } from './qqEmojis.ts'

/**
 * 传音聊天行内富文本标记解析与构建器 (chatMarkup)
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
  | {
      type: 'sticker'
      url: string
      raw: string
    }
  | {
      type: 'qqEmoji'
      name: string
      raw: string
    }

/** 行内标记匹配正则：{{item:...}} 或 {{order:...}} 或 {{img:...}} 或 {{sticker:...}} */
const CHAT_MARKUP_PATTERN = /\{\{(item|order|img|sticker):([^}]+)\}\}/g

/** 匹配经典 QQ 表情中括号标记，如 [微笑]、[呲牙] */
const QQ_EMOJI_PATTERN = /\[([^\[\]\s]{1,10})\]/g
/** 匹配常见图片扩展名的 HTTP/HTTPS 直链 */
const IMAGE_EXT_PATTERN = /\.(?:png|jpe?g|gif|webp|svg|avif)(?:[?#]|$)/i

/** 匹配正文中的 HTTP/HTTPS 链接正则 */
const URL_EXTRACT_PATTERN = /(https?:\/\/[^\s<>"'{}|\\^`]+)/gi

/** 判断某字符串是否为图片链接（具备图片后缀，或整段就是有效 http 链接且长度在合理范围） */
export function isImageUrl(url: string): boolean {
  const trimmed = url.trim()
  if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) return false
  if (trimmed.length > 200) return false
  return IMAGE_EXT_PATTERN.test(trimmed)
}

/** 构建表情包标记或返回纯链接 */
export function buildStickerChatMarkup(url: string): string {
  return url.trim()
}

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
    } else if (kind === 'img' || kind === 'sticker') {
      const url = payload.trim()
      if (url.startsWith('http://') || url.startsWith('https://')) {
        segments.push({
          type: 'sticker',
          url,
          raw,
        })
      } else {
        segments.push({ type: 'text', text: raw })
      }
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

  // 对纯文本段进行二次解析：如果包含图片 URL 或纯链接，拆解为 sticker 分段
  const expanded: ChatMarkupSegment[] = []
  for (const seg of segments) {
    if (seg.type !== 'text') {
      expanded.push(seg)
      continue
    }
    const text = seg.text
    // 若整个文本段就是一个合法的 URL（单发表情包或常见图片链接），直接转为 sticker
    const trimmed = text.trim()
    if (
      (trimmed.startsWith('http://') || trimmed.startsWith('https://')) &&
      !/\s/.test(trimmed) &&
      trimmed.length <= 200 &&
      (IMAGE_EXT_PATTERN.test(trimmed) || trimmed.length > 10)
    ) {
      expanded.push({
        type: 'sticker',
        url: trimmed,
        raw: text,
      })
      continue
    }

    // 文本内嵌图片链接拆解
    let textLast = 0
    URL_EXTRACT_PATTERN.lastIndex = 0
    let urlMatch: RegExpExecArray | null
    let hasExtracted = false

    while ((urlMatch = URL_EXTRACT_PATTERN.exec(text)) !== null) {
      const matchedUrl = urlMatch[0]
      if (isImageUrl(matchedUrl)) {
        hasExtracted = true
        if (urlMatch.index > textLast) {
          expanded.push({
            type: 'text',
            text: text.slice(textLast, urlMatch.index),
          })
        }
        expanded.push({
          type: 'sticker',
          url: matchedUrl,
          raw: matchedUrl,
        })
        textLast = URL_EXTRACT_PATTERN.lastIndex
      }
    }

    if (hasExtracted) {
      if (textLast < text.length) {
        expanded.push({
          type: 'text',
          text: text.slice(textLast),
        })
      }
    } else {
      expanded.push(seg)
    }
  }

  // 对文本分段进行 QQ 表情二次拆解
  const withEmojis: ChatMarkupSegment[] = []
  for (const seg of expanded) {
    if (seg.type !== 'text') {
      withEmojis.push(seg)
      continue
    }

    const text = seg.text
    QQ_EMOJI_PATTERN.lastIndex = 0
    let emojiMatch: RegExpExecArray | null
    let lastPos = 0
    let matchedAny = false

    while ((emojiMatch = QQ_EMOJI_PATTERN.exec(text)) !== null) {
      const emojiName = emojiMatch[1]
      if (QQ_EMOJI_MAP.has(emojiName)) {
        matchedAny = true
        if (emojiMatch.index > lastPos) {
          withEmojis.push({
            type: 'text',
            text: text.slice(lastPos, emojiMatch.index),
          })
        }
        withEmojis.push({
          type: 'qqEmoji',
          name: emojiName,
          raw: emojiMatch[0],
        })
        lastPos = QQ_EMOJI_PATTERN.lastIndex
      }
    }

    if (matchedAny) {
      if (lastPos < text.length) {
        withEmojis.push({
          type: 'text',
          text: text.slice(lastPos),
        })
      }
    } else {
      withEmojis.push(seg)
    }
  }

  // 合并相邻的纯文本分段
  const merged: ChatMarkupSegment[] = []
  for (const seg of withEmojis) {
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
