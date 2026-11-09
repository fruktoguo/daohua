/**
 * 经典 QQ 风格官方动态表情定义与双源分发映射 (qqEmojis)
 *
 * 物理隔离与双源梯级分发设计：
 * 1. 纯 TypeScript 数据契约，不包含 JSX 语法，确保 Node.js ESM 单测与构建工具链零障碍原生加载；
 * 2. 一级优先源：自建 Cloudflare Worker + R2 边缘图床 (https://img.yuohira.com/emotes/s{qsid}.gif)；
 * 3. 二级保底源：游戏服务器前端本地静态资源 (/emotes/s{qsid}.gif)；
 * 4. 离线缓存层：结合客户端 IndexedDB (daohua_image_cache)，单次拉取后永久本地二进制秒开。
 */

export interface QqEmojiMeta {
  /** 经典中文名称，如 "微笑" */
  name: string
  /** 官方 QSid 标识，如 "14" */
  qsid: string
  /** 英文标识，如 "smile" */
  id: string
}

/** 官方 CDN / 自建图床基础地址 */
export const CF_EMOTE_BASE_URL = 'https://img.yuohira.com/emotes'
export const LOCAL_EMOTE_BASE_URL = '/emotes'

/** 精选 70+ 个经典常用 QQ 动态表情列表 */
export const QQ_EMOJI_LIST: readonly QqEmojiMeta[] = [
  { name: '微笑', qsid: '14', id: 'smile' },
  { name: '呲牙', qsid: '13', id: 'grin' },
  { name: '偷笑', qsid: '20', id: 'smirk' },
  { name: '发呆', qsid: '3', id: 'dazed' },
  { name: '得意', qsid: '4', id: 'proud' },
  { name: '流泪', qsid: '5', id: 'tear' },
  { name: '害羞', qsid: '6', id: 'shy' },
  { name: '闭嘴', qsid: '7', id: 'silent' },
  { name: '睡', qsid: '8', id: 'sleep' },
  { name: '大哭', qsid: '9', id: 'sob' },
  { name: '尴尬', qsid: '10', id: 'awkward' },
  { name: '发怒', qsid: '11', id: 'angry' },
  { name: '调皮', qsid: '12', id: 'tongue' },
  { name: '酷', qsid: '16', id: 'cool' },
  { name: '抓狂', qsid: '18', id: 'crazy' },
  { name: '吐', qsid: '19', id: 'vomit' },
  { name: '可爱', qsid: '21', id: 'cute' },
  { name: '白眼', qsid: '22', id: 'roll-eyes' },
  { name: '傲慢', qsid: '23', id: 'arrogant' },
  { name: '饥饿', qsid: '24', id: 'hungry' },
  { name: '困', qsid: '25', id: 'sleepy' },
  { name: '惊恐', qsid: '26', id: 'panic' },
  { name: '流汗', qsid: '27', id: 'sweat' },
  { name: '憨笑', qsid: '28', id: 'giggle' },
  { name: '悠闲', qsid: '29', id: 'relaxed' },
  { name: '奋斗', qsid: '30', id: 'struggle' },
  { name: '咒骂', qsid: '31', id: 'curse' },
  { name: '疑问', qsid: '32', id: 'question' },
  { name: '嘘', qsid: '33', id: 'shh' },
  { name: '晕', qsid: '34', id: 'dizzy' },
  { name: '折磨', qsid: '35', id: 'torture' },
  { name: '骷髅', qsid: '37', id: 'skull' },
  { name: '敲打', qsid: '38', id: 'hammer' },
  { name: '再见', qsid: '39', id: 'wave' },
  { name: '爱情', qsid: '42', id: 'love' },
  { name: '猪头', qsid: '46', id: 'pig' },
  { name: '拥抱', qsid: '49', id: 'hug' },
  { name: '蛋糕', qsid: '53', id: 'cake' },
  { name: '闪电', qsid: '54', id: 'lightning' },
  { name: '炸弹', qsid: '55', id: 'bomb' },
  { name: '刀', qsid: '56', id: 'knife' },
  { name: '便便', qsid: '59', id: 'poop' },
  { name: '咖啡', qsid: '60', id: 'coffee' },
  { name: '玫瑰', qsid: '63', id: 'rose' },
  { name: '凋谢', qsid: '64', id: 'fade' },
  { name: '爱心', qsid: '66', id: 'heart' },
  { name: '心碎', qsid: '67', id: 'broken-heart' },
  { name: '太阳', qsid: '74', id: 'sun' },
  { name: '月亮', qsid: '75', id: 'moon' },
  { name: '赞', qsid: '76', id: 'thumbs-up' },
  { name: '握手', qsid: '78', id: 'handshake' },
  { name: '胜利', qsid: '79', id: 'victory' },
  { name: '坏笑', qsid: '101', id: 'evil' },
  { name: '哈欠', qsid: '104', id: 'yawn' },
  { name: '鄙视', qsid: '105', id: 'disdain' },
  { name: '委屈', qsid: '106', id: 'wronged' },
  { name: '快哭了', qsid: '107', id: 'crying' },
  { name: '阴险', qsid: '108', id: 'sinister' },
  { name: '吓', qsid: '110', id: 'scared' },
  { name: '可怜', qsid: '111', id: 'plead' },
  { name: '菜刀', qsid: '112', id: 'cleaver' },
  { name: '啤酒', qsid: '113', id: 'beer' },
  { name: '篮球', qsid: '114', id: 'basketball' },
  { name: '乒乓', qsid: '115', id: 'pingpong' },
  { name: '抱拳', qsid: '118', id: 'salute' },
  { name: '勾引', qsid: '119', id: 'seduce' },
  { name: '拳头', qsid: '120', id: 'fist' },
  { name: 'OK', qsid: '124', id: 'ok' },
  { name: '滑稽', qsid: '178', id: 'funny' },
  { name: '点赞', qsid: '201', id: 'like' },
  { name: '托腮', qsid: '212', id: 'think' },
  { name: '捂脸', qsid: '264', id: 'facepalm' },
  { name: '吃瓜', qsid: '271', id: 'melon' },
]

export const QQ_EMOJI_MAP: ReadonlyMap<string, QqEmojiMeta> = new Map(
  QQ_EMOJI_LIST.map((item) => [item.name, item])
)

export function isQqEmoji(name: string): boolean {
  return QQ_EMOJI_MAP.has(name)
}

/**
 * 获取指定表情的双源分发地址
 */
export function getQqEmojiUrls(qsid: string): { cfUrl: string; localUrl: string } {
  return {
    cfUrl: `${CF_EMOTE_BASE_URL}/s${qsid}.gif`,
    localUrl: `${LOCAL_EMOTE_BASE_URL}/s${qsid}.gif`,
  }
}
