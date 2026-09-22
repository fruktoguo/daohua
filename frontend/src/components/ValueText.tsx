/** 匹配包含修仙单位与符号的数值模式正则 */
const NUMBER_PATTERN = /([+-]?(?:\d+(?:\.\d+)?|\.\d+)(?:万|亿|兆|%|秒|层|点)?)/g
/** 整组胶囊标记：[[文本]] 内的内容渲染为一个完整胶囊（如「60%术法攻击」） */
const GROUP_PATTERN = /\[\[([^\]]+)\]\]/g

/**
 * 根据前后文意境分析数值的高亮色系 (valueTone)
 *
 * 识别上下文关键词：
 * - 灵力/法力/真元 -> mana 蓝
 * - 伤害/暴击/破招/穿透/汲取 -> damage 红/橙
 * - 生命/治疗/恢复/吸血 -> health 绿
 * - 正负符号及百分比 -> positive / negative / percent
 */
function valueTone(text: string, token: string, index: number): string {
  const context = `${text.slice(Math.max(0, index - 10), index)}${text.slice(index + token.length, index + token.length + 10)}`
  if (token.startsWith('+')) return 'positive'
  if (token.startsWith('-')) return 'negative'
  if (/灵力|法力|真元/.test(context)) return 'mana'
  if (/伤害|暴击|破招|穿透|汲取/.test(context)) return 'damage'
  if (/生命|治疗|恢复|吸血/.test(context)) return 'health'
  if (token.includes('%')) return 'percent'
  return 'number'
}

/**
 * 智能文本数值高亮胶囊组件 (ValueText)
 *
 * 扫描任意描述文本中的数字、百分比与加减量，自动切分为微胶囊高亮片段；
 * `[[...]]` 组标记内的整段文本渲染为单个完整胶囊（用于「60%术法攻击」这类构成单元）。
 */
export function ValueText({ className = '', text }: { className?: string; text: string }): React.JSX.Element {
  const segments: React.ReactNode[] = []

  const capsule = (token: string, index: number, key: string) => (
    <span className={`value-capsule value-capsule--${valueTone(text, token, index)} ${className}`.trim()} key={key}>
      {token}
    </span>
  )

  const pushNumbers = (chunk: string, offset: number, keyPrefix: string) => {
    let inner = 0
    NUMBER_PATTERN.lastIndex = 0
    for (const match of chunk.matchAll(NUMBER_PATTERN)) {
      const index = match.index
      if (index > inner) segments.push(chunk.slice(inner, index))
      segments.push(capsule(match[0], offset + index, `${keyPrefix}-${index}`))
      inner = index + match[0].length
    }
    if (inner < chunk.length) segments.push(chunk.slice(inner))
  }

  let cursor = 0
  GROUP_PATTERN.lastIndex = 0
  for (const match of text.matchAll(GROUP_PATTERN)) {
    if (match.index > cursor) pushNumbers(text.slice(cursor, match.index), cursor, `t${cursor}`)
    segments.push(capsule(match[1], match.index, `g${match.index}`))
    cursor = match.index + match[0].length
  }
  if (cursor < text.length) pushNumbers(text.slice(cursor), cursor, `t${cursor}`)

  return <>{segments.length > 0 ? segments : text}</>
}
