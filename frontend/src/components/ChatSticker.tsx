import { memo, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { chatStickerService, MAX_CUSTOM_STICKERS } from '../game/chatStickerService'
import { useChatImageGeneration, useChatImageSource } from './useChatImageLease'
import { FloatingMenuItem, FloatingMenuGroup, FloatingPopover } from './FloatingPopover'
import { ChatImageLightbox } from './ChatImageLightbox'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { StatGlyph } from './StatGlyph'
import { uploadChatImage } from '../game/chatImageUploadService'
import { Icon } from './Icon'

export interface ChatStickerProps {
  url: string
  sha256?: string | null
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
 * 2. 自动通过 IndexedDB 本地持久化缓存，后续遇到相同 URL 或 SHA-256 直接从本地提取 ObjectURL 秒开；
 * 3. 鼠标右键（或触屏长按）呼出操作菜单，支持一键「添加到表情包」（自动强缓存原图到本地与云端同步）；
 * 4. 云端图片过期时，若本地存有原图，支持自动静默重传自愈并刷新各端直链。
 */
export const ChatSticker = memo(function ChatSticker(props: ChatStickerProps): React.JSX.Element {
  const generation = useChatImageGeneration()
  // 切会话、链接或内容 ID 时同时重置占位、错误和灯箱状态。
  const key = JSON.stringify([generation, props.url.trim(), props.sha256 ?? null])
  return <ChatStickerImage key={key} {...props} generation={generation} />
})

function ChatStickerImage({
  url,
  sha256,
  className = '',
  alt,
  zoomable = false,
  generation,
}: ChatStickerProps & { generation: number }): React.JSX.Element {
  const t = useT()
  const trimmedUrl = url.trim()
  const [imageUrl, setImageUrl] = useState(trimmedUrl)
  const [isLoaded, setIsLoaded] = useState(false)
  const [isError, setIsError] = useState(false)
  const cachedSrc = useChatImageSource(isError ? null : imageUrl, generation, sha256)
  const [menuOpen, setMenuOpen] = useState(false)
  const [isSaving, setIsSaving] = useState(false)
  const [isHealing, setIsHealing] = useState(false)
  const [lightboxOpen, setLightboxOpen] = useState(false)
  const mountedRef = useRef(true)
  const autoHealAttemptedRef = useRef<string | null>(null)

  const isSaved = useSyncExternalStore(
    chatStickerService.subscribe.bind(chatStickerService),
    () => chatStickerService.isStickerSaved(trimmedUrl, sha256),
    () => false
  )

  useEffect(() => {
    mountedRef.current = true
    return () => { mountedRef.current = false }
  }, [])

  const handleContextMenu = (e: React.MouseEvent): void => {
    e.preventDefault()
    e.stopPropagation()
    setMenuOpen(true)
  }

  const handleAddToStickers = async (): Promise<void> => {
    setMenuOpen(false)
    if (isSaving) return
    setIsSaving(true)
    try {
      const res = await chatStickerService.saveStickerFromUrl(trimmedUrl)
      if (generation !== chatStickerService.getGeneration()) return
      if (res.ok) {
        useGameStore.getState().setNotice(t('combat.chat.stickerAdded'))
      } else if (res.reason === 'limit') {
        useGameStore.getState().setNotice(t('combat.chat.stickerLimitReached', { max: MAX_CUSTOM_STICKERS }))
      } else if (res.reason === 'duplicate') {
        useGameStore.getState().setNotice(t('combat.chat.stickerAlreadyAdded'))
      } else {
        useGameStore.getState().setNotice(t('combat.chat.stickerInvalidImage'))
      }
    } finally {
      if (mountedRef.current) setIsSaving(false)
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
        if (generation === chatStickerService.getGeneration()) useGameStore.getState().setNotice(t('combat.chat.linkCopied'))
      }).catch(() => {})
    }
  }

  const handleOpenOriginal = (): void => {
    setMenuOpen(false)
    if (typeof window !== 'undefined') {
      window.open(trimmedUrl, '_blank', 'noopener,noreferrer')
    }
  }

  const triggerHeal = async (manual: boolean): Promise<void> => {
    if (isHealing) return
    setIsHealing(true)
    try {
      const res = await chatStickerService.ensureStickerReadyForSend(trimmedUrl, blob => uploadChatImage(blob))
      if (!mountedRef.current || generation !== chatStickerService.getGeneration()) return
      if (res.healed && res.url && res.url !== trimmedUrl) {
        setImageUrl(res.url)
        setIsError(false)
        setIsLoaded(false)
        useGameStore.getState().setNotice(t('combat.chat.stickerHealSuccess'))
      } else if (res.missingLocalBlob) {
        if (manual) {
          useGameStore.getState().setNotice(t('combat.chat.stickerMissingLocalBlob'))
        }
      } else if (manual) {
        setIsError(false)
        setIsLoaded(false)
      }
    } catch {
      if (manual && mountedRef.current) {
        setIsError(false)
        setIsLoaded(false)
      }
    } finally {
      if (mountedRef.current) setIsHealing(false)
    }
  }

  const handleImageError = (): void => {
    setLightboxOpen(false)
    setIsLoaded(false)
    setIsError(true)
    if (isSaved && autoHealAttemptedRef.current !== trimmedUrl) {
      autoHealAttemptedRef.current = trimmedUrl
      void triggerHeal(false)
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
          onClick={() => void handleAddToStickers()}
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
        {(!isLoaded && !isError) && (
          <span className="chat-sticker-placeholder">
            <span className="chat-sticker-placeholder-icon"><Icon name="image" size={22} /></span>
            <span className="chat-sticker-placeholder-text">{t('combat.chat.stickerPlaceholder')}</span>
          </span>
        )}

        {cachedSrc && !isError && (
          <img
            alt={alt ?? t('combat.chat.stickerAlt')}
            className={`chat-sticker-image ${isLoaded ? 'is-loaded' : 'is-loading'}`}
            decoding="async"
            onError={handleImageError}
            onLoad={() => setIsLoaded(true)}
            src={cachedSrc}
          />
        )}

        {isError && (
          <span
            className="chat-sticker-error"
            onClick={(e) => {
              e.stopPropagation()
              void triggerHeal(true)
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
      {lightboxOpen && cachedSrc && (
        <ChatImageLightbox
          alt={alt ?? t('combat.chat.stickerAlt')}
          onClose={() => setLightboxOpen(false)}
          open={lightboxOpen}
          src={cachedSrc}
        />
      )}
    </FloatingPopover>
  )
}
