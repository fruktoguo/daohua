const NUMBER_PATTERN = /([+-]?(?:\d+(?:\.\d+)?|\.\d+)(?:万|亿|兆|%|秒|层|点)?)/g

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
