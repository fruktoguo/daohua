import { memo, useMemo } from 'react'
import { parseChatMarkup, type ChatMarkupSegment } from '../game/chatMarkup'
import { findEquipmentInCatalog, findItemInCatalog, resolveItemMetadata } from '../game/ids'
import { useGameStore } from '../game/gameStore'
import { useMarketStore } from '../game/marketStore'
import { useUiStore } from '../uiStore'
import { useSocialStore } from '../game/socialStore'
import { gameClient } from '../network/gameClient'
import { useT } from '../i18n'
import { FloatingTooltip } from './FloatingPopover'
import { ItemIcon, ItemReference, ItemTooltipTarget } from './ItemDisplay'
import { ChatSticker } from './ChatSticker'
import { QqEmoji } from './QqEmoji'
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

/** 单条队伍超链接胶囊 (ChatPartyReference) */
const ChatPartyReference = memo(function ChatPartyReference({
  party,
}: {
  party: Extract<ChatMarkupSegment, { type: 'party' }>
}): React.JSX.Element {
  const t = useT()
  const maps = useGameStore((state) => state.maps)
  const nodeName = useMemo(() => {
    if (!party.nodeId) return null
    for (const m of maps) {
      const found = m.nodes.find((n) => n.nodeId === party.nodeId)
      if (found) return found.name
    }
    return party.nodeId
  }, [maps, party.nodeId])

  const displayName = party.name || t('social.invitation.unnamed')
  const activityLabel = party.activityKind ? t(`social.activity.${party.activityKind}`) : null
  const memberCount = party.memberCount ?? 1
  const maxSlots = party.maxSlots ?? 3

  const handleClick = (e: React.MouseEvent | React.KeyboardEvent): void => {
    e.stopPropagation()
    const myParty = useSocialStore.getState().party
    if (myParty) {
      if (myParty.partyId === party.partyId) {
        useGameStore.getState().setNotice(t('combat.chat.partyAlreadyIn'))
      } else {
        useGameStore.getState().setNotice(t('combat.chat.partyLeaveFirst'))
      }
      useUiStore.getState().setActiveTab('social')
      useUiStore.getState().setSocialSubTab('party')
      return
    }
    gameClient.sendSocialAction('party.join', undefined, undefined, { partyId: party.partyId })
    useUiStore.getState().setActiveTab('social')
    useUiStore.getState().setSocialSubTab('party')
    useGameStore.getState().setNotice(t('combat.chat.partyJoinRequested'))
  }

  const handleKeyDown = (e: React.KeyboardEvent): void => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      handleClick(e)
    }
  }

  const tooltipContent = (
    <div className="chat-link-popover">
      <div className="chat-link-popover-header">
        <strong className="text-gold">👥 {displayName}</strong>
      </div>
      <div className="chat-link-popover-body">
        {activityLabel && (
          <p>
            <span className="text-muted">{t('social.field.activity')}: </span>
            <span>{activityLabel}</span>
          </p>
        )}
        {nodeName && (
          <p>
            <span className="text-muted">{t('social.field.node')}: </span>
            <span>{nodeName}</span>
          </p>
        )}
        {Boolean(party.minValuation && party.minValuation > 0) && (
          <p>
            <span className="text-muted">{t('social.field.minValuation')}: </span>
            <span>{party.minValuation}</span>
          </p>
        )}
        <p>
          <span className="text-muted">{t('social.field.openSlots')}: </span>
          <span>{memberCount} / {maxSlots}</span>
        </p>
      </div>
      <div className="chat-link-popover-hint">
        <small className="text-jade">{t('combat.chat.partyClickActionHint')}</small>
      </div>
    </div>
  )

  return (
    <FloatingTooltip placement="top" title={tooltipContent} tone="gold">
      <span
        aria-label={t('combat.chat.partyAria', {
          name: displayName,
          members: memberCount,
          slots: maxSlots,
        })}
        className="chat-party-link"
        onClick={handleClick}
        onKeyDown={handleKeyDown}
        role="button"
        tabIndex={0}
      >
        <span className="chat-party-badge">👥 {t('social.sub.party')}</span>
        <span className="chat-party-name">{displayName}</span>
        {activityLabel && <span className="chat-party-activity">{activityLabel}</span>}
        <span className="chat-party-slots">({memberCount}/{maxSlots})</span>
        <span className="chat-party-action-hint">{t('combat.chat.partyJoin')}</span>
      </span>
    </FloatingTooltip>
  )
})

/** 单条地点超链接胶囊 (ChatLocationReference) */
const ChatLocationReference = memo(function ChatLocationReference({
  location,
}: {
  location: Extract<ChatMarkupSegment, { type: 'location' }>
}): React.JSX.Element {
  const t = useT()
  const maps = useGameStore((state) => state.maps)
  const nodeInfo = useMemo(() => {
    for (const m of maps) {
      const found = m.nodes.find((n) => n.nodeId === location.nodeId)
      if (found) return { node: found, mapName: m.name }
    }
    return null
  }, [maps, location.nodeId])

  const displayName = location.name || nodeInfo?.node.name || location.nodeId

  const handleClick = (e: React.MouseEvent | React.KeyboardEvent): void => {
    e.stopPropagation()
    useUiStore.getState().navigateToMapNode(location.nodeId)
    useGameStore.getState().setNotice(t('combat.chat.locationLocated', { name: displayName }))
  }

  const handleKeyDown = (e: React.KeyboardEvent): void => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      handleClick(e)
    }
  }

  const tooltipContent = (
    <div className="chat-link-popover">
      <div className="chat-link-popover-header">
        <strong className="text-jade">📍 {displayName}</strong>
      </div>
      <div className="chat-link-popover-body">
        {nodeInfo?.mapName && (
          <p>
            <span className="text-muted">{t('social.field.region')}: </span>
            <span>{nodeInfo.mapName}</span>
          </p>
        )}
        {nodeInfo?.node.description && (
          <p className="chat-link-popover-desc">{nodeInfo.node.description}</p>
        )}
      </div>
      <div className="chat-link-popover-hint">
        <small className="text-jade">{t('combat.chat.locationClickActionHint')}</small>
      </div>
    </div>
  )

  return (
    <FloatingTooltip placement="top" title={tooltipContent} tone="jade">
      <span
        aria-label={t('combat.chat.locationAria', { name: displayName })}
        className="chat-location-link"
        onClick={handleClick}
        onKeyDown={handleKeyDown}
        role="button"
        tabIndex={0}
      >
        <span className="chat-location-badge">📍 {t('combat.chat.locationBadge')}</span>
        <span className="chat-location-name">{displayName}</span>
      </span>
    </FloatingTooltip>
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
        if (seg.type === 'party') {
          return <ChatPartyReference key={idx} party={seg} />
        }
        if (seg.type === 'location') {
          return <ChatLocationReference key={idx} location={seg} />
        }
        if (seg.type === 'sticker') {
          return <ChatSticker key={idx} url={seg.url} />
        }
        if (seg.type === 'qqEmoji') {
          return <QqEmoji key={idx} name={seg.name} />
        }
        return null
      })}
    </>
  )
})
