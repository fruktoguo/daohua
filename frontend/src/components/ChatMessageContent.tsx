import { memo, useMemo } from 'react'
import { parseChatMarkup, type ChatMarkupSegment } from '../game/chatMarkup'
import { findEquipmentInCatalog, findItemInCatalog, resolveItemMetadata } from '../game/ids'
import { useGameStore } from '../game/gameStore'
import { useMarketStore } from '../game/marketStore'
import { useUiStore } from '../uiStore'
import { useT } from '../i18n'
import { ItemIcon, ItemReference, ItemTooltipTarget } from './ItemDisplay'
import { UnitNumber } from './UnitNumber'

/** 单条订单超链接胶囊 (ChatOrderReference) */
const ChatOrderReference = memo(function ChatOrderReference({
  order,
}: {
  order: Extract<ChatMarkupSegment, { type: 'order' }>
}): React.JSX.Element {
  const t = useT()
  const definition = useGameStore((state) => findItemInCatalog(state.items, order.itemId))
  const equipmentDef = useGameStore((state) => findEquipmentInCatalog(state.equipment, order.itemId))
  const metadata = resolveItemMetadata(order.itemId)
  const baseLabel = definition?.name ?? equipmentDef?.name ?? metadata?.label ?? order.itemId
  const displayLabel = order.enhancementLevel > 0 ? `${baseLabel} +${order.enhancementLevel}` : baseLabel
  const sideLabel = order.side === 'buy' ? t('market.side.buy') : t('market.side.sell')
  const handleClick = (e: React.MouseEvent | React.KeyboardEvent): void => {
    // 允许冒泡或阻止冒泡：阻止冒泡避免触发整行的其他点击行为
    e.stopPropagation()
    useUiStore.getState().setActiveTab('market')
    const market = useMarketStore.getState()
    market.setActiveTab('browse')
    market.setSelectedItemId(order.itemId)
  }

  const handleKeyDown = (e: React.KeyboardEvent): void => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      handleClick(e)
    }
  }

  return (
    <ItemTooltipTarget
      className="chat-order-link-anchor"
      definition={definition}
      enhancementLevel={order.enhancementLevel}
      equipmentDefinition={equipmentDef}
      itemId={order.itemId}
    >
      <span
        aria-label={t('market.chatOrderAria', {
          side: sideLabel,
          name: displayLabel,
          price: order.price,
          quantity: order.quantity,
        })}
        className={`chat-order-link chat-order-link--${order.side}`}
        onClick={handleClick}
        onKeyDown={handleKeyDown}
        role="button"
        tabIndex={0}
        title={t('market.chatOrderClickHint')}
      >
        <span className={`chat-order-badge chat-order-badge--${order.side}`}>
          {sideLabel}
        </span>
        <ItemIcon itemId={order.itemId} />
        <span className="chat-order-name">{displayLabel}</span>
        <span className="chat-order-meta">
          <span className="chat-order-price">
            @{order.price}
          </span>
          <span className="chat-order-qty">
            × <UnitNumber disableTooltip value={order.quantity} />
          </span>
        </span>
      </span>
    </ItemTooltipTarget>
  )
})

/**
 * 传音正文富文本渲染组件 (ChatMessageBody)
 *
 * 将传音文本中的 {{item:...}} 与 {{order:...}} 标记解析并渲染为带交互的修仙风格超链接胶囊。
 */
export const ChatMessageBody = memo(function ChatMessageBody({
  body,
}: {
  body: string
}): React.JSX.Element {
  const segments = useMemo(() => parseChatMarkup(body), [body])

  if (segments.length === 0) return <>{body}</>
  if (segments.length === 1 && segments[0].type === 'text') {
    return <>{segments[0].text}</>
  }

  return (
    <>
      {segments.map((seg, idx) => {
        if (seg.type === 'text') {
          return <span key={idx}>{seg.text}</span>
        }
        if (seg.type === 'item') {
          return (
            <ItemReference
              enhancementLevel={seg.enhancementLevel}
              itemId={seg.itemId}
              key={idx}
            />
          )
        }
        if (seg.type === 'order') {
          return <ChatOrderReference key={idx} order={seg} />
        }
        return null
      })}
    </>
  )
})
