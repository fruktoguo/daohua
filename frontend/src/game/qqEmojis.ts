/**
 * 经典 QQ 风格小黄脸表情纯数据定义
 *
 * 物理隔离设计：
 * 纯 TypeScript 数据契约，不包含任何 JSX 语法，确保 Node.js 单测与构建工具链零障碍原生加载。
 */

export interface QqEmojiMeta {
  /** 经典中文名称，如 "微笑" */
  name: string
  /** 英文标识，如 "smile" */
  id: string
}

export const QQ_EMOJI_LIST: readonly QqEmojiMeta[] = [
  { name: '微笑', id: 'smile' },
  { name: '呲牙', id: 'grin' },
  { name: '偷笑', id: 'smirk' },
  { name: '发呆', id: 'dazed' },
  { name: '得意', id: 'proud' },
  { name: '流泪', id: 'tear' },
  { name: '害羞', id: 'shy' },
  { name: '闭嘴', id: 'silent' },
  { name: '大哭', id: 'sob' },
  { name: '尴尬', id: 'awkward' },
  { name: '发怒', id: 'angry' },
  { name: '调皮', id: 'tongue' },
  { name: '酷', id: 'cool' },
  { name: '冷汗', id: 'sweat' },
  { name: '抓狂', id: 'crazy' },
  { name: '白眼', id: 'roll-eyes' },
  { name: '憨笑', id: 'giggle' },
  { name: '疑问', id: 'question' },
  { name: '敲打', id: 'hammer' },
  { name: '再见', id: 'wave' },
  { name: '擦汗', id: 'wipe' },
  { name: '坏笑', id: 'evil' },
  { name: '可怜', id: 'plead' },
  { name: '滑稽', id: 'funny' },
  { name: '抱拳', id: 'salute' },
  { name: '点赞', id: 'thumbs-up' },
  { name: '爱心', id: 'heart' },
  { name: '心碎', id: 'broken-heart' },
]

export const QQ_EMOJI_MAP: ReadonlyMap<string, QqEmojiMeta> = new Map(
  QQ_EMOJI_LIST.map(item => [item.name, item])
)

export function isQqEmoji(name: string): boolean {
  return QQ_EMOJI_MAP.has(name)
}
