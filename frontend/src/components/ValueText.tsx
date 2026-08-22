/** 匹配包含修仙单位与符号的数值模式正则 */
const NUMBER_PATTERN = /([+-]?(?:\d+(?:\.\d+)?|\.\d+)(?:万|亿|兆|%|秒|层|点)?)/g

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
 * 扫描任意描述文本中的数字、百分比与加减量，自动切分为微胶囊高亮片段。
 */
export function ValueText({ className = '', text }: { className?: string; text: string }): React.JSX.Element {
  const segments: React.ReactNode[] = []
  let cursor = 0
  NUMBER_PATTERN.lastIndex = 0
  for (const match of text.matchAll(NUMBER_PATTERN)) {
    const index = match.index
    if (index > cursor) segments.push(text.slice(cursor, index))
    const token = match[0]
    segments.push(
      <span className={`value-capsule value-capsule--${valueTone(text, token, index)} ${className}`.trim()} key={`${index}-${token}`}>
        {token}
      </span>,
    )
    cursor = index + token.length
  }
  if (cursor < text.length) segments.push(text.slice(cursor))
  return <>{segments.length > 0 ? segments : text}</>
}
