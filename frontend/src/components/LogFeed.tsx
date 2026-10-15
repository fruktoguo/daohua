import { memo, useLayoutEffect, useRef, type RefObject } from 'react'
import { describeAbility, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { findAbilityInCatalog, findCombatantInCatalog, findEncounterInCatalog } from '../game/ids'
import { useT } from '../i18n'
import type { ChatMessage } from '../logging/chatService'
import { gameClient } from '../network/gameClient'
import type { PartyLinkDto } from '../network/protocol'
import type { GameLogEntry, LogSegment } from '../logging/gameLogService'
import { splitLogSegmentsByLines } from '../logging/gameLogService'
import { FloatingInfoCard, FloatingPopover, FloatingTooltip } from './FloatingPopover'
import { ItemReference } from './ItemDisplay'
import { MonsterTooltipTarget } from './MonsterDisplay'
import { ValueText } from './ValueText'

/** 格式化日志时间戳为月-日 时:分:秒 */
export function formatLogTime(timestampMs: number): string {
  const date = new Date(timestampMs)
  const part = (value: number) => String(value).padStart(2, '0')
  return `${part(date.getMonth() + 1)}-${part(date.getDate())} ${part(date.getHours())}:${part(date.getMinutes())}:${part(date.getSeconds())}`
}

/**
 * 战报中的功法/神通引用组件 (AbilityReference)
 *
 * 鼠标悬停展示功法详细效果、伤害加成与冷却机制气泡。
 */
const AbilityReference = memo(function AbilityReference({ abilityId, label }: { abilityId: string; label: string }): React.JSX.Element {
  const t = useT()
  const ability = useGameStore((state) => findAbilityInCatalog(state.abilities, abilityId))

  if (!ability) {
    return (
      <FloatingTooltip placement="top" title={t('combat.log.abilityTooltip', { label })} tone="qi">
        <span className="log-reference log-reference--ability">{label}</span>
      </FloatingTooltip>
    )
  }

  const tooltip = describeAbility(ability, 1)

  return (
    <FloatingPopover
      closeDelay={120}
      content={
        <FloatingInfoCard
          badge={tooltip.badge}
          badgeTone="qi"
          description={<ValueText className="effect-value-capsule" text={tooltip.description} />}
          lines={tooltip.lines?.map((line) => (
            <ValueText className="effect-value-capsule" key={line} text={line} />
          ))}
          title={tooltip.title}
        />
      }
      gap={8}
      interactive={false}
      openDelay={60}
      placement="bottom"
      trigger="hover"
      triggerClassName="log-reference log-reference--ability"
    >
      {label}
    </FloatingPopover>
  )
})

/**
 * 战报中的妖兽/敌方遭遇引用组件 (EnemyReference)
 *
 * 悬停展示敌怪流派、属性、气血真元等面板数据。
 */
const EnemyReference = memo(function EnemyReference({
  enemyId,
  kind,
  label,
}: {
  enemyId: string
  kind: 'enemy' | 'encounter'
  label: string
}): React.JSX.Element {
  const t = useT()
  const encounters = useGameStore(state => state.encounters)
  const maps = useGameStore(state => state.maps)

  const encounter = findEncounterInCatalog(encounters, enemyId)
  const combatant = encounter?.enemy ?? findCombatantInCatalog(encounters, maps, enemyId)

  if (combatant) {
    return (
      <MonsterTooltipTarget
        className={`log-reference log-reference--${kind}`}
        monster={combatant}
        placement="bottom"
      >
        {label}
      </MonsterTooltipTarget>
    )
  }

  return (
    <FloatingTooltip placement="top" title={t('combat.log.enemyTooltip', { label })} tone="gold">
      <span className={`log-reference log-reference--${kind}`}>{label}</span>
    </FloatingTooltip>
  )
})

/**
 * 日志单条分段内容解析分发组件 (LogContent)
 *
 * 识别文本、物品掉落、功法施放、妖兽击败等分段类型并附加富文本高亮与交互气泡。
 */
const LogContent = memo(function LogContent({ segment }: { segment: LogSegment }): React.JSX.Element {
  if (segment.type === 'text') return <ValueText className="log-value-capsule" text={segment.value} />
  if (segment.kind === 'item') {
    return (
      <ItemReference
        amount={segment.amount}
        className="log-reference log-reference--item"
        itemId={segment.id}
        label={segment.label}
      />
    )
  }
  if (segment.kind === 'ability') {
    return <AbilityReference abilityId={segment.id} label={segment.label} />
  }
  if (segment.kind === 'enemy' || segment.kind === 'encounter') {
    return <EnemyReference enemyId={segment.id} kind={segment.kind} label={segment.label} />
  }
  return (
    <span className={`log-reference log-reference--${segment.kind}`} title={`${segment.kind}: ${segment.id}`}>
      {segment.label}
    </span>
  )
})

/**
 * 单行游戏日志展示组件 (LogRow)
 *
 * 当日志为连续完全重复项（count > 1）时，在尾部展示金石微光乘数小胶囊形如 `× 3`。
 */
const LogRow = memo(function LogRow({ entry }: { entry: GameLogEntry }): React.JSX.Element {
  const t = useT()
  const displayTime = formatLogTime(entry.latestTimestampMs ?? entry.timestampMs)
  const isRepeated = entry.count > 1
  const repeatTitle = isRepeated
    ? t('combat.log.repeatTooltip', {
        count: entry.count,
        first: formatLogTime(entry.timestampMs),
        latest: displayTime,
      })
    : undefined

  // 多目标施放合并行：服务端以 \n 分隔各目标子句，拆成「前缀行 + 逐条子句行」。
  // 视觉首行 = 前缀行 + 第一条子句；续行前置一个 visibility:hidden 的前缀克隆占位，
  // 使每条续行的「对/为」与首行「对」字像素级对齐（同 daojie 的 chat-merged-combat-indent 方案）。
  const lines = splitLogSegmentsByLines(entry.segments)
  const repeatCapsule = isRepeated ? (
    <span className="log-repeat-capsule" title={repeatTitle}>× {entry.count}</span>
  ) : null

  return (
    <li>
      <time dateTime={new Date(entry.latestTimestampMs ?? entry.timestampMs).toISOString()}>{displayTime}</time>
      <span className="log-message">
        {lines === null ? (
          entry.segments.map((segment, index) => <LogContent key={`${entry.id}-${index}`} segment={segment} />)
        ) : (
          <>
            <span className="log-combat-line">
              {lines[0].map((segment, index) => <LogContent key={`${entry.id}-h-${index}`} segment={segment} />)}
              {lines[1].map((segment, index) => <LogContent key={`${entry.id}-c0-${index}`} segment={segment} />)}
              {lines.length === 2 && isRepeated && repeatCapsule}
            </span>
            {lines.slice(2).map((clause, clauseIndex) => (
              <span className="log-combat-line" key={`${entry.id}-l${clauseIndex + 2}`}>
                <span aria-hidden="true" className="log-combat-indent">
                  {lines[0].map((segment, index) => <LogContent key={`${entry.id}-i${clauseIndex}-${index}`} segment={segment} />)}
                </span>
                {clause.map((segment, index) => <LogContent key={`${entry.id}-c${clauseIndex + 2}-${index}`} segment={segment} />)}
                {clauseIndex === lines.length - 3 && isRepeated && repeatCapsule}
              </span>
            ))}
          </>
        )}
        {(lines === null) && isRepeated && repeatCapsule}
      </span>
    </li>
  )
})

/**
 * 传音消息内嵌的队伍链接卡 (PartyLinkCard)
 *
 * 渲染分享时刻的队伍快照（party_ref 持久化引用）；点击「加入」走 party.join 社交指令，
 * 队伍解散后服务端会清掉 partyLink（此处不渲染），过期快照由服务端门槛裁决兜底。
 */
const PartyLinkCard = memo(function PartyLinkCard({ link }: { link: PartyLinkDto }): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)
  // 队伍目标节点解析为「区域 · 地点」；未配（空串）显示「不限」，未知 ID 原样透出。
  let nodeName = t('social.field.anyNode')
  if (link.nodeId) {
    nodeName = link.nodeId
    for (const map of maps) {
      const node = map.nodes.find(item => item.nodeId === link.nodeId)
      if (node) {
        nodeName = `${map.name} · ${node.name}`
        break
      }
    }
  }
  return (
    <span className="party-link-card">
      <span className="party-link-main">
        <strong>{link.name}</strong>
        <span className="badge">{t(`social.activity.${link.activityKind}`)}</span>
        <span>{nodeName}</span>
        <span>{t('social.memberCount', { count: link.memberCount })}</span>
        {link.minValuation > 0 && <span>{t('social.minValuationValue', { value: formatNumber(link.minValuation) })}</span>}
      </span>
      <button
        className="party-link-join"
        onClick={() => gameClient.sendSocialAction('party.join', undefined, undefined, { partyId: link.partyId })}
        type="button"
      >{t('social.action.join')}</button>
    </span>
  )
})

/**
 * 单条传音消息展示组件 (ChatRow)
 *
 * 复用战报日志的「时间 + 内容」网格排版，正文前展示发送者道号胶囊。
 * 正文按玩家原文渲染（不做数值高亮），避免聊天内容被拆成数值胶囊。
 */
const ChatRow = memo(function ChatRow({ message }: { message: ChatMessage }): React.JSX.Element {
  return (
    <li className={`chat-row chat-row--${message.channel}`}>
      <time dateTime={new Date(message.sentAtMs).toISOString()}>{formatLogTime(message.sentAtMs)}</time>
      <span className="log-message">
        <span className="chat-sender">{message.senderName}</span>
        <span className="chat-body">{message.body}</span>
        {message.partyLink && <PartyLinkCard link={message.partyLink} />}
      </span>
    </li>
  )
})

/** 日志流与传音流合并后的统一渲染项 */
export type FeedItem =
  | { kind: 'log'; id: string; timestampMs: number; entry: GameLogEntry }
  | { kind: 'chat'; id: string; timestampMs: number; message: ChatMessage }

/** 把折叠后的日志条目转换为渲染项 */
export function toLogFeedItems(entries: readonly GameLogEntry[]): FeedItem[] {
  return entries.map(entry => ({
    kind: 'log' as const,
    id: entry.id,
    timestampMs: entry.latestTimestampMs ?? entry.timestampMs,
    entry,
  }))
}

/** 把传音消息转换为渲染项 */
export function toChatFeedItems(messages: readonly ChatMessage[]): FeedItem[] {
  return messages.map(message => ({
    kind: 'chat' as const,
    id: `chat:${message.id}`,
    timestampMs: message.sentAtMs,
    message,
  }))
}

/** 按时间合并两路渲染项（时间相同按 ID 稳定排序，保证同一次渲染结果确定） */
export function mergeFeedItems(left: FeedItem[], right: FeedItem[]): FeedItem[] {
  return [...left, ...right].sort((a, b) => a.timestampMs - b.timestampMs || a.id.localeCompare(b.id))
}

/** 渲染项列表组件 */
export function FeedList({ items }: { items: readonly FeedItem[] }): React.JSX.Element {
  return (
    <>
      {items.map(item => item.kind === 'log'
        ? <LogRow entry={item.entry} key={item.id} />
        : <ChatRow key={item.id} message={item.message} />)}
    </>
  )
}

/**
 * 贴底滚动 Hook：玩家未向上翻查历史时，新内容自动滚动到最新一行。
 *
 * 贴底标记只由用户滚动行为决定：effect 一旦自行清零，当列表尚无滚动条（会话初期内容不足一屏）
 * 时程序化滚动不会触发 scroll 事件，标记永久停在 false，此后新内容再也不会自动贴底。
 */
export function useStickyScroll(revision: number): {
  listRef: RefObject<HTMLOListElement | null>
  onScroll: (event: React.UIEvent<HTMLOListElement>) => void
} {
  const listRef = useRef<HTMLOListElement>(null)
  const stickToBottom = useRef(true)
  useLayoutEffect(() => {
    const list = listRef.current
    if (!list || !stickToBottom.current) return
    list.scrollTop = list.scrollHeight
  }, [revision])
  const onScroll = (event: React.UIEvent<HTMLOListElement>): void => {
    const list = event.currentTarget
    stickToBottom.current = list.scrollHeight - list.scrollTop - list.clientHeight < 32
  }
  return { listRef, onScroll }
}
