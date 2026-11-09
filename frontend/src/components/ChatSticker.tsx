import { memo, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { chatStickerService } from '../game/chatStickerService'
import { FloatingMenuItem, FloatingMenuGroup, FloatingPopover } from './FloatingPopover'
import { ChatImageLightbox } from './ChatImageLightbox'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { StatGlyph } from './StatGlyph'
import { uploadChatImage } from '../game/chatImageUploadService'
export interface ChatStickerProps {
  url: string
  className?: string
  alt?: string
  /** 是否允许左键单击/触屏点按放大查看（消息正文 true；表情选择器内保持 false，点击语义为选表情） */
  zoomable?: boolean
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
  zoomable = false,
}: ChatStickerProps): React.JSX.Element {
  const t = useT()
  const trimmedUrl = url.trim()
  const [cachedSrc, setCachedSrc] = useState<string>(trimmedUrl)
  const [isLoaded, setIsLoaded] = useState(false)
  const [isError, setIsError] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)
  const [isHealing, setIsHealing] = useState(false)
  const [lightboxOpen, setLightboxOpen] = useState(false)
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

    chatStickerService.loadImageWithCache(trimmedUrl).then(src => {
      if (!isCancelled && mountedRef.current && src && src !== trimmedUrl) {
        setCachedSrc(src)
      }
    }).catch(() => {
      // 静默降级保持 trimmedUrl
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

  const handleRemoveFromStickers = (): void => {
    chatStickerService.removeCustomSticker(trimmedUrl)
    setMenuOpen(false)
    useGameStore.getState().setNotice(t('combat.chat.stickerDeleted'))
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

  /** 左键单击/触屏点按：放大查看图片细节（仅消息正文启用） */
  const handleOpenLightbox = (): void => {
    if (!zoomable || !cachedSrc || isError || !isLoaded) return
    setLightboxOpen(true)
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
          label={t('combat.chat.removeFromStickers')}
          onClick={handleRemoveFromStickers}
          tone="crimson"
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
        className={`chat-sticker-wrapper${zoomable && isLoaded && !isError ? ' is-zoomable' : ''}`}
        onClick={handleOpenLightbox}
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
            onError={() => setIsError(true)}
            onLoad={() => setIsLoaded(true)}
            src={cachedSrc}
          />
        )}

        {/* 加载异常容错：支持点击触发自愈重传或重试 */}
        {isError && (
          <span
            className="chat-sticker-error"
            onClick={async (e) => {
              e.stopPropagation()
              if (isHealing) return
              setIsHealing(true)
              try {
                const validUrl = await chatStickerService.ensureValidStickerUrl(trimmedUrl, async (blob) => {
                  return uploadChatImage(blob)
                })
                if (validUrl && validUrl !== trimmedUrl) {
                  setCachedSrc(validUrl)
                  setIsError(false)
                  setIsLoaded(false)
                  useGameStore.getState().setNotice(t('combat.chat.stickerHealSuccess'))
                } else {
                  setIsError(false)
                  setIsLoaded(false)
                }
              } catch {
                setIsError(false)
                setIsLoaded(false)
              } finally {
                setIsHealing(false)
              }
            }}
            role="button"
            style={{ cursor: 'pointer' }}
            tabIndex={0}
            title={t('combat.chat.stickerHealRetry')}
          >
            {isHealing ? t('combat.chat.stickerHealing') : t('combat.chat.stickerLoadError')}
          </span>
        )}
      </span>
      {lightboxOpen && (
        <ChatImageLightbox
          alt={alt ?? t('combat.chat.stickerAlt')}
          onClose={() => setLightboxOpen(false)}
          open={lightboxOpen}
          src={cachedSrc}
        />
      )}
    </FloatingPopover>
  )
})
