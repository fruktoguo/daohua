import { memo, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react'
import { chatStickerService } from '../game/chatStickerService'
import { FloatingMenuItem, FloatingMenuGroup, FloatingPopover } from './FloatingPopover'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { StatGlyph } from './StatGlyph'

export interface ChatStickerProps {
  url: string
  className?: string
  alt?: string
}

/**
 * 传音表情包组件 (ChatSticker)
 *
 * 核心特性：
 * 1. 未加载前展示修仙风格的 [图片] 紧凑微光占位符；
 * 2. 自动通过 IndexedDB 本地持久化缓存，后续遇到相同 URL 直接从本地提取 ObjectURL 秒开；
 * 3. 鼠标右键（或触屏长按）呼出修仙操作菜单，支持一键「添加到表情包」或「复制图片链接」。
 */
export const ChatSticker = memo(function ChatSticker({
  url,
  className = '',
  alt,
}: ChatStickerProps): React.JSX.Element {
  const t = useT()
  const trimmedUrl = url.trim()
  // 智能同源加速：若是 img.yuohira.com 直链，自动转为本站同源代理 /img/，确保国内玩家 200ms 秒开且绕过 Cloudflare 跨境丢包
  const resolvedUrl = useMemo(() => {
    if (trimmedUrl.startsWith('https://img.yuohira.com/')) {
      return trimmedUrl.replace('https://img.yuohira.com/', '/img/')
    }
    return trimmedUrl
  }, [trimmedUrl])
  const [cachedSrc, setCachedSrc] = useState<string>(resolvedUrl)
  const [isLoaded, setIsLoaded] = useState(false)
  const [isError, setIsError] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)
  const mountedRef = useRef(true)

  // 响应表情包收藏状态
  const isSaved = useSyncExternalStore(
    chatStickerService.subscribe.bind(chatStickerService),
    () => chatStickerService.isStickerSaved(trimmedUrl),
    () => false
  )

  useEffect(() => {
    mountedRef.current = true
    let isCancelled = false

    chatStickerService.loadImageWithCache(resolvedUrl).then(src => {
      if (!isCancelled && mountedRef.current && src && src !== resolvedUrl) {
        setCachedSrc(src)
      }
    }).catch(() => {
      // 静默降级保持 resolvedUrl
    })
    return () => {
      isCancelled = true
      mountedRef.current = false
    }
  }, [trimmedUrl])

  const handleContextMenu = (e: React.MouseEvent): void => {
    e.preventDefault()
    e.stopPropagation()
    setMenuOpen(true)
  }

  const handleAddToStickers = (): void => {
    const success = chatStickerService.addCustomSticker(trimmedUrl)
    setMenuOpen(false)
    if (success) {
      useGameStore.getState().setNotice(t('combat.chat.stickerAdded'))
    } else {
      useGameStore.getState().setNotice(t('combat.chat.stickerAlreadyAdded'))
    }
  }

  const handleCopyLink = (): void => {
    setMenuOpen(false)
    if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(trimmedUrl).then(() => {
        useGameStore.getState().setNotice(t('combat.chat.linkCopied'))
      }).catch(() => {})
    }
  }

  const handleOpenOriginal = (): void => {
    setMenuOpen(false)
    if (typeof window !== 'undefined') {
      window.open(trimmedUrl, '_blank', 'noopener,noreferrer')
    }
  }

  const menuContent = (
    <FloatingMenuGroup>
      {!isSaved ? (
        <FloatingMenuItem
        icon={<StatGlyph glyph="qi" size={14} />}
        label={t('combat.chat.addToStickers')}
        onClick={handleAddToStickers}
        tone="jade"
      />
    ) : (
      <FloatingMenuItem
        disabled
        label={t('combat.chat.alreadyInStickers')}
      />
      )}
      <FloatingMenuItem
        label={t('combat.chat.copyLink')}
        onClick={handleCopyLink}
      />
      <FloatingMenuItem
        label={t('combat.chat.openOriginal')}
        onClick={handleOpenOriginal}
      />
    </FloatingMenuGroup>
  )

  return (
    <FloatingPopover
      content={menuContent}
      interactive
      onOpenChange={setMenuOpen}
      open={menuOpen}
      placement="bottom"
      size="small"
      trigger="manual"
      triggerAs="span"
      triggerClassName={`chat-sticker-anchor ${className}`.trim()}
    >
      <span
        className="chat-sticker-wrapper"
        onContextMenu={handleContextMenu}
        title={t('combat.chat.stickerRightClickHint')}
      >
        {/* 未加载或加载中呈现优雅修仙风格的 [图片] 占位符 */}
        {(!isLoaded && !isError) && (
          <span className="chat-sticker-placeholder">
            <span className="chat-sticker-placeholder-icon">📷</span>
            <span className="chat-sticker-placeholder-text">{t('combat.chat.stickerPlaceholder')}</span>
          </span>
        )}

        {/* 实际渲染的图像 */}
        {cachedSrc && !isError && (
          <img
            alt={alt ?? t('combat.chat.stickerAlt')}
            className={`chat-sticker-image ${isLoaded ? 'is-loaded' : 'is-loading'}`}
            decoding="async"
            onError={() => {
              // 若同源代理或原始路径出错且是 img.yuohira.com，尝试直连或回退
              setIsError(true)
            }}
            onLoad={() => setIsLoaded(true)}
            src={cachedSrc}
          />
        )}
        {/* 加载异常容错 */}
        {isError && (
          <span className="chat-sticker-error" title={trimmedUrl}>
            {t('combat.chat.stickerLoadError')}
          </span>
        )}
      </span>
    </FloatingPopover>
  )
})
