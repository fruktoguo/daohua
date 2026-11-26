import { QQ_EMOJI_MAP } from './qqEmojis.ts'

/**
 * 道友攻略富文本安全解析器与颜文字库 (guideMarkup)
 *
 * 设计红线：
 * 1. 纯 AST 解析，零 HTML / dangerouslySetInnerHTML 注入面，仅放行 http/https 图片协议；
 * 2. 字数统计按 Unicode 标量值（code point）计算，与服务端 C# EnumerateRunes() 逐位一致；
 * 3. 同时支持 Markdown 块级/行内排版、游戏自带动态表情 [微笑]、任意颜文字、
 *    三种图像语法（![alt](url)、{{img:url}}、正文裸图片直链）以及物品/地点图鉴胶囊引用。
 */

export const GUIDE_MAX_TITLE_RUNES = 60
export const GUIDE_MAX_SUMMARY_RUNES = 200
export const GUIDE_MAX_BODY_RUNES = 10_000
export const GUIDE_MAX_IMAGE_URL_LENGTH = 500
export const GUIDE_MAX_PRICE_STONES = 100_000

/** 颜文字分类标识 */
export type KaomojiCategoryId = 'joy' | 'cultivation' | 'surprise' | 'chill'

/** 独立编辑器内置分类颜文字表 */
export const GUIDE_KAOMOJI_CATEGORIES: ReadonlyArray<{
  id: KaomojiCategoryId
  items: readonly string[]
}> = [
  {
    id: 'joy',
    items: [
      '(๑•̀ㅂ•́)و✧',
      '(｡•̀ᴗ-)✧',
      'ヾ(≧▽≦*)o',
      '(✿◡‿◡)',
      '(≧∇≦)ﾉ',
      '✧*｡٩(ˊᗜˋ*)و✧*｡',
      '(*^▽^*)',
      '(´▽`ʃ♡ƪ)',
      '٩(๑>◡<๑)۶',
      '(o゜▽゜)o☆',
    ],
  },
  {
    id: 'cultivation',
    items: [
      '( ˘ω˘ )',
      '(￣▽￣)~*',
      '(⌐■_■)',
      '(｀・ω・´)',
      '(ง •_•)ง',
      '╰(*°▽°*)╯',
      '( •̀ ω •́ )✧',
      '⊂(▀¯▀⊂)',
      '(￣︶￣)↗',
      '┌( ´_ゝ` )┐',
    ],
  },
  {
    id: 'surprise',
    items: [
      'Σ(っ °Д °;)っ',
      '(⊙_⊙)？',
      '(°ー°〃)',
      '∑( 口 ||',
      '(・∀・)？',
      '┗|｀O´|┛',
      '(⊙x⊙;)',
      'ヽ(°〇°)ﾉ',
      '(°ロ°)',
      '(っ °Д °;)っ',
    ],
  },
  {
    id: 'chill',
    items: [
      '_(:з」∠)_',
      '(╯°□°）╯︵ ┻━┻',
      '┬─┬ ノ( ゜-゜ノ)',
      '(´；ω；`)',
      '(；′⌒`)',
      '(￣﹏￣；)',
      '( ´･･)ﾉ(._.`)',
      '(っ´ω`c)',
      '(￣o￣) . z Z',
      '╮(╯▽╰)╭',
    ],
  },
]

/** 行内富文本 AST 节点 */
export type GuideInlineNode =
  | { type: 'text'; text: string }
  | { type: 'bold'; children: GuideInlineNode[] }
  | { type: 'code'; code: string }
  | { type: 'image'; url: string; alt: string }
  | { type: 'qqEmoji'; name: string }
  | { type: 'item'; itemId: string; enhancementLevel: number }
  | { type: 'location'; nodeId: string; name?: string }

/** 块级富文本 AST 节点 */
export type GuideBlockNode =
  | { type: 'heading'; level: 1 | 2 | 3; inlines: GuideInlineNode[] }
  | { type: 'hr' }
  | { type: 'codeBlock'; code: string }
  | { type: 'blockquote'; lines: GuideInlineNode[][] }
  | { type: 'unorderedList'; items: GuideInlineNode[][] }
  | { type: 'orderedList'; items: GuideInlineNode[][] }
  | { type: 'paragraph'; lines: GuideInlineNode[][] }

/** 按 Unicode 标量值（Rune）统计字符数，与服务端 C# EnumerateRunes() 严格对齐 */
export function countUnicodeRunes(text: string): number {
  if (!text) return 0
  let count = 0
  for (const _ of text) {
    count += 1
  }
  return count
}

/** 校验并净化显式图片 URL（仅放行 http:// 与 https:// 且无空白控制字符） */
export function sanitizeSafeHttpImageUrl(rawUrl: string): string | null {
  const trimmed = rawUrl.trim()
  if (!trimmed || trimmed.length > GUIDE_MAX_IMAGE_URL_LENGTH) return null
  if (!/^https?:\/\//i.test(trimmed)) return null
  if (/[\s<>"'`]/.test(trimmed)) return null
  return trimmed
}

const DIRECT_IMAGE_EXT_RE = /\.(?:png|jpe?g|gif|webp|svg|avif)(?:[?#]|$)/i

/** 判断正文裸链接是否为可自动展开渲染的图片链接 */
export function isAutoParseableImageUrl(rawUrl: string): boolean {
  const safe = sanitizeSafeHttpImageUrl(rawUrl)
  if (!safe) return false
  return DIRECT_IMAGE_EXT_RE.test(safe)
}

/**
 * 行内组合词法正则（按优先级单遍扫描）：
 * 1. `code` 行内代码
 * 2. ![alt](url) Markdown 图片
 * 3. {{kind:payload}} 传音同源实体/图片标记
 * 4. **bold** 加粗（不跨换行）
 * 5. https?://... 裸图片直链（不吞并紧随的中文字符或全角标点）
 * 6. [表情名] 内置动态表情候选
 */
const INLINE_TOKEN_RE =
  /`([^`\n]+)`|!\[([^\]]*)\]\((https?:\/\/[^\s)]+)\)|\{\{(item|location|map|img|sticker):([^}\n]+)\}\}|\*\*([^*\n]+)\*\*|(https?:\/\/[a-zA-Z0-9\-._~:/?#[\]@!$&'*+,;=%]+?\.(?:png|jpe?g|gif|webp|svg|avif)(?:[?#][a-zA-Z0-9\-._~:/?#[\]@!$&'*+,;=%]*)?)|\[([^\[\]\s]{1,10})\]/gi

function appendText(out: GuideInlineNode[], text: string): void {
  if (!text) return
  const prev = out[out.length - 1]
  if (prev && prev.type === 'text') {
    prev.text += text
  } else {
    out.push({ type: 'text', text })
  }
}

/** 解析单行文本内的富文本、内置表情、颜文字、图片直链与物品胶囊（allowBold 防止递归无限嵌套） */
export function parseGuideInlines(line: string, allowBold = true): GuideInlineNode[] {
  if (!line) return []
  const result: GuideInlineNode[] = []
  const regex = new RegExp(INLINE_TOKEN_RE.source, 'gi')
  let lastIndex = 0
  let match: RegExpExecArray | null

  while ((match = regex.exec(line)) !== null) {
    if (match.index > lastIndex) {
      appendText(result, line.slice(lastIndex, match.index))
    }

    const [
      fullMatch,
      inlineCode,
      mdImgAlt,
      mdImgUrl,
      braceKind,
      bracePayload,
      boldContent,
      bareImgUrl,
      emojiCandidate,
    ] = match

    if (inlineCode !== undefined) {
      result.push({ type: 'code', code: inlineCode })
    } else if (mdImgUrl !== undefined) {
      const safe = sanitizeSafeHttpImageUrl(mdImgUrl)
      if (safe) {
        result.push({ type: 'image', url: safe, alt: (mdImgAlt ?? '').trim() })
      } else {
        appendText(result, fullMatch)
      }
    } else if (braceKind !== undefined && bracePayload !== undefined) {
      const kind = braceKind.toLowerCase()
      const payload = bracePayload.trim()
      if (kind === 'img' || kind === 'sticker') {
        const safe = sanitizeSafeHttpImageUrl(payload)
        if (safe) {
          result.push({ type: 'image', url: safe, alt: '' })
        } else {
          appendText(result, fullMatch)
        }
      } else if (kind === 'item') {
        const parts = payload.split(':')
        const itemId = (parts[0] ?? '').trim()
        const lvl = parts.length > 1 ? Number(parts[1]) : 0
        if (itemId && /^[a-zA-Z0-9._-]+$/.test(itemId) && Number.isFinite(lvl) && lvl >= 0) {
          result.push({ type: 'item', itemId, enhancementLevel: Math.floor(lvl) })
        } else {
          appendText(result, fullMatch)
        }
      } else if (kind === 'location' || kind === 'map') {
        const idx = payload.indexOf(':')
        const nodeId = (idx >= 0 ? payload.slice(0, idx) : payload).trim()
        const locName = idx >= 0 ? payload.slice(idx + 1).trim() : undefined
        if (nodeId && /^[a-zA-Z0-9._-]+$/.test(nodeId)) {
          result.push({ type: 'location', nodeId, name: locName || undefined })
        } else {
          appendText(result, fullMatch)
        }
      } else {
        appendText(result, fullMatch)
      }
    } else if (boldContent !== undefined && allowBold) {
      result.push({ type: 'bold', children: parseGuideInlines(boldContent, false) })
    } else if (bareImgUrl !== undefined) {
      const safe = sanitizeSafeHttpImageUrl(bareImgUrl)
      if (safe && isAutoParseableImageUrl(safe)) {
        result.push({ type: 'image', url: safe, alt: '' })
      } else {
        appendText(result, fullMatch)
      }
    } else if (emojiCandidate !== undefined) {
      if (QQ_EMOJI_MAP.has(emojiCandidate)) {
        result.push({ type: 'qqEmoji', name: emojiCandidate })
      } else {
        appendText(result, fullMatch)
      }
    } else {
      appendText(result, fullMatch)
    }

    lastIndex = regex.lastIndex
  }

  if (lastIndex < line.length) {
    appendText(result, line.slice(lastIndex))
  }

  return result
}

/** 将整篇攻略正文解析为块级 AST 序列 */
export function parseGuideBlocks(body: string): GuideBlockNode[] {
  if (!body) return []
  const rawLines = body.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n')
  const blocks: GuideBlockNode[] = []
  let i = 0

  while (i < rawLines.length) {
    const line = rawLines[i]
    const trimmed = line.trim()

    // 空行分隔段落
    if (!trimmed) {
      i += 1
      continue
    }

    // 多行围栏代码块 ```
    if (trimmed.startsWith('```')) {
      const codeLines: string[] = []
      i += 1
      while (i < rawLines.length && !rawLines[i].trim().startsWith('```')) {
        codeLines.push(rawLines[i])
        i += 1
      }
      if (i < rawLines.length) i += 1
      blocks.push({ type: 'codeBlock', code: codeLines.join('\n') })
      continue
    }

    // 水平分割线 --- 或 ***
    if (/^(?:-{3,}|\*{3,})$/.test(trimmed)) {
      blocks.push({ type: 'hr' })
      i += 1
      continue
    }

    // 标题 # / ## / ###
    const headingMatch = /^(#{1,3})\s+(.+)$/.exec(trimmed)
    if (headingMatch) {
      const level = headingMatch[1].length as 1 | 2 | 3
      blocks.push({ type: 'heading', level, inlines: parseGuideInlines(headingMatch[2].trim()) })
      i += 1
      continue
    }

    // 引用块 >
    if (/^>\s?/.test(trimmed)) {
      const quoteLines: GuideInlineNode[][] = []
      while (i < rawLines.length && /^>\s?/.test(rawLines[i].trim())) {
        const content = rawLines[i].trim().replace(/^>\s?/, '')
        quoteLines.push(parseGuideInlines(content))
        i += 1
      }
      blocks.push({ type: 'blockquote', lines: quoteLines })
      continue
    }

    // 无序列表 - / *
    if (/^[-*]\s+/.test(trimmed)) {
      const items: GuideInlineNode[][] = []
      while (i < rawLines.length && /^[-*]\s+/.test(rawLines[i].trim())) {
        const content = rawLines[i].trim().replace(/^[-*]\s+/, '')
        items.push(parseGuideInlines(content))
        i += 1
      }
      blocks.push({ type: 'unorderedList', items })
      continue
    }

    // 有序列表 1.
    if (/^\d+\.\s+/.test(trimmed)) {
      const items: GuideInlineNode[][] = []
      while (i < rawLines.length && /^\d+\.\s+/.test(rawLines[i].trim())) {
        const content = rawLines[i].trim().replace(/^\d+\.\s+/, '')
        items.push(parseGuideInlines(content))
        i += 1
      }
      blocks.push({ type: 'orderedList', items })
      continue
    }

    // 普通段落（连续非特殊行合并为一个段落并保留行间软换行）
    const paraLines: GuideInlineNode[][] = []
    while (i < rawLines.length) {
      const cur = rawLines[i]
      const curTrim = cur.trim()
      if (
        !curTrim ||
        curTrim.startsWith('```') ||
        /^(?:-{3,}|\*{3,})$/.test(curTrim) ||
        /^(#{1,3})\s+/.test(curTrim) ||
        /^>\s?/.test(curTrim) ||
        /^[-*]\s+/.test(curTrim) ||
        /^\d+\.\s+/.test(curTrim)
      ) {
        break
      }
      paraLines.push(parseGuideInlines(cur))
      i += 1
    }
    if (paraLines.length > 0) {
      blocks.push({ type: 'paragraph', lines: paraLines })
    }
  }

  return blocks
}

/** 在编辑器文本指定选区插入或包裹内容，返回新文本与新光标区间 */
export function applyEditorInsertion(
  source: string,
  selectionStart: number,
  selectionEnd: number,
  options: {
    prefix: string
    suffix?: string
    placeholder?: string
    blockNewline?: boolean
  },
): { value: string; nextStart: number; nextEnd: number } {
  const start = Math.max(0, Math.min(source.length, selectionStart))
  const end = Math.max(start, Math.min(source.length, selectionEnd))
  const selected = source.slice(start, end)
  const suffix = options.suffix ?? ''
  const inner = selected || (options.placeholder ?? '')

  let leading = ''
  let trailing = ''
  if (options.blockNewline) {
    if (start > 0 && source[start - 1] !== '\n') leading = '\n'
    if (end < source.length && source[end] !== '\n') trailing = '\n'
  }

  const inserted = `${leading}${options.prefix}${inner}${suffix}${trailing}`
  const value = source.slice(0, start) + inserted + source.slice(end)
  const innerStart = start + leading.length + options.prefix.length
  const innerEnd = innerStart + inner.length
  return {
    value,
    nextStart: selected ? innerEnd + suffix.length : innerStart,
    nextEnd: selected ? innerEnd + suffix.length : innerEnd,
  }
}
