import React, { memo, useMemo, useState } from 'react'
import { parseGuideBlocks, type GuideInlineNode } from '../game/guideMarkup'
import { resolveEntityName } from '../game/entityNames'
import { useGameStore } from '../game/gameStore'
import { useUiStore } from '../uiStore'
import { useT } from '../i18n'
import { ChatImageLightbox } from './ChatImageLightbox'
import { FloatingTooltip } from './FloatingPopover'
import { Icon } from './Icon'
import { ItemReference } from './ItemDisplay'
import { QqEmoji } from './QqEmoji'

/** 攻略内嵌图片：支持自适应加载、加载失败提示与点击打开全屏灯箱缩放 */
const GuideInlineImage = memo(function GuideInlineImage({
  url,
  alt,
  onZoom,
}: {
  url: string
  alt: string
  onZoom: (src: string, alt: string) => void
}): React.JSX.Element {
  const t = useT()
  const [failed, setFailed] = useState(false)

  if (failed) {
    return (
      <span className="guide-rich-img-fallback">
        <Icon name="image" size={14} />
        <span>{t('social.guide.imageLoadFailed')}</span>
      </span>
    )
  }

  return (
    <span className="guide-rich-img-wrap">
      <button
        aria-label={alt || t('social.guide.zoomImageAria')}
        className="guide-rich-img-btn"
        onClick={() => onZoom(url, alt)}
        type="button"
      >
        <img
          alt={alt}
          className="guide-rich-img"
          decoding="async"
          loading="lazy"
          onError={() => setFailed(true)}
          referrerPolicy="no-referrer"
          src={url}
        />
      </button>
      {alt && <span className="guide-rich-img-caption">{alt}</span>}
    </span>
  )
})

/** 攻略内嵌大世界地点胶囊：点击定位地图节点 */
const GuideLocationReference = memo(function GuideLocationReference({
  nodeId,
  name,
}: {
  nodeId: string
  name?: string
}): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)
  const nodeInfo = useMemo(() => {
    for (const m of maps) {
      const found = m.nodes.find(n => n.nodeId === nodeId)
      if (found) return { node: found, mapName: m.name }
    }
    return null
  }, [maps, nodeId])

  const displayName = resolveEntityName(t, 'location', nodeId, name, nodeInfo?.node.name)

  const handleLocate = (): void => {
    useUiStore.getState().navigateToMapNode(nodeId)
    useGameStore.getState().setNotice(t('combat.chat.locationLocated', { name: displayName }))
  }

  return (
    <FloatingTooltip placement="top" title={nodeInfo?.mapName ? `${nodeInfo.mapName} · ${displayName}` : displayName} tone="jade">
      <button className="chat-location-link guide-location-pill" onClick={handleLocate} type="button">
        <Icon name="pin" size={11} />
        <span>{displayName}</span>
      </button>
    </FloatingTooltip>
  )
})

function renderInlineList(
  nodes: readonly GuideInlineNode[],
  onZoom: (src: string, alt: string) => void,
): React.ReactNode {
  return nodes.map((node, index) => {
    switch (node.type) {
      case 'text':
        return <React.Fragment key={index}>{node.text}</React.Fragment>
      case 'bold':
        return <strong className="guide-rich-bold" key={index}>{renderInlineList(node.children, onZoom)}</strong>
      case 'code':
        return <code className="guide-rich-inline-code" key={index}>{node.code}</code>
      case 'qqEmoji':
        return <QqEmoji key={index} name={node.name} size={22} />
      case 'image':
        return <GuideInlineImage alt={node.alt} key={`${index}-${node.url}`} onZoom={onZoom} url={node.url} />
      case 'item':
        return (
          <ItemReference
            enhancementLevel={node.enhancementLevel}
            itemId={node.itemId}
            key={`${index}-${node.itemId}-${node.enhancementLevel}`}
          />
        )
      case 'location':
        return <GuideLocationReference key={`${index}-${node.nodeId}`} name={node.name} nodeId={node.nodeId} />
    }
  })
}

/**
 * 道友攻略富文本安全渲染组件 (GuideRichContent)
 *
 * 将攻略纯文本解析为结构化 AST 后渲染，支持小标题、加粗、引用、列表、代码块、分割线、
 * 游戏内置动态表情、任意颜文字、自动图片链接解析（含点击灯箱缩放）与物品/地点胶囊悬浮详情。
 */
export const GuideRichContent = memo(function GuideRichContent({
  body,
  className = '',
}: {
  body: string
  className?: string
}): React.JSX.Element {
  const blocks = useMemo(() => parseGuideBlocks(body), [body])
  const [lightbox, setLightbox] = useState<{ src: string; alt: string } | null>(null)

  const handleZoom = (src: string, alt: string): void => {
    setLightbox({ src, alt })
  }

  return (
    <div className={`guide-rich-content ${className}`.trim()}>
      {blocks.map((block, index) => {
        switch (block.type) {
          case 'heading':
            if (block.level === 1) {
              return <h2 className="guide-rich-h1" key={index}>{renderInlineList(block.inlines, handleZoom)}</h2>
            }
            if (block.level === 2) {
              return <h3 className="guide-rich-h2" key={index}>{renderInlineList(block.inlines, handleZoom)}</h3>
            }
            return <h4 className="guide-rich-h3" key={index}>{renderInlineList(block.inlines, handleZoom)}</h4>

          case 'hr':
            return <hr className="guide-rich-hr" key={index} />

          case 'codeBlock':
            return (
              <pre className="guide-rich-pre" key={index}>
                <code>{block.code}</code>
              </pre>
            )

          case 'blockquote':
            return (
              <blockquote className="guide-rich-quote" key={index}>
                {block.lines.map((lineNodes, lineIdx) => (
                  <p key={lineIdx}>{renderInlineList(lineNodes, handleZoom)}</p>
                ))}
              </blockquote>
            )

          case 'unorderedList':
            return (
              <ul className="guide-rich-ul" key={index}>
                {block.items.map((itemNodes, itemIdx) => (
                  <li key={itemIdx}>{renderInlineList(itemNodes, handleZoom)}</li>
                ))}
              </ul>
            )

          case 'orderedList':
            return (
              <ol className="guide-rich-ol" key={index}>
                {block.items.map((itemNodes, itemIdx) => (
                  <li key={itemIdx}>{renderInlineList(itemNodes, handleZoom)}</li>
                ))}
              </ol>
            )

          case 'paragraph':
            return (
              <p className="guide-rich-p" key={index}>
                {block.lines.map((lineNodes, lineIdx) => (
                  <React.Fragment key={lineIdx}>
                    {lineIdx > 0 && <br />}
                    {renderInlineList(lineNodes, handleZoom)}
                  </React.Fragment>
                ))}
              </p>
            )
        }
      })}

      <ChatImageLightbox
        alt={lightbox?.alt}
        onClose={() => setLightbox(null)}
        open={lightbox !== null}
        src={lightbox?.src ?? ''}
      />
    </div>
  )
})
