import { useChatImageGeneration } from './useChatImageLease'
import { memo, useEffect, useState, useSyncExternalStore } from 'react'
import {
  chatStickerService,
  MAX_CUSTOM_STICKERS,
  MAX_STICKER_URL_LENGTH,
  type CustomStickerItem,
} from '../game/chatStickerService'
import { computeBlobSha256, uploadChatImage } from '../game/chatImageUploadService'
import { ChatSticker } from './ChatSticker'
import { QqEmoji } from './QqEmoji'
import { FloatingMenuGroup, FloatingMenuItem, FloatingPopover } from './FloatingPopover'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { StatGlyph } from './StatGlyph'
import { Icon } from './Icon'

export interface ChatEmojiPickerProps {
  onSelectEmoji: (emoji: string) => void
  onSelectSticker: (url: string) => void
  onClose?: () => void
}

/** 单个表情包网格项（支持普通选图/右键删除与管理模式勾选） */
const StickerPickerItem = memo(function StickerPickerItem({
  item,
  manageMode,
  selected,
  busy,
  onSelect,
  onToggleSelect,
}: {
  item: CustomStickerItem
  manageMode: boolean
  selected: boolean
  busy: boolean
  onSelect: (item: CustomStickerItem) => void
  onToggleSelect: (stickerId: string) => void
}): React.JSX.Element {
  const t = useT()
  const [menuOpen, setMenuOpen] = useState(false)

  const handleContextMenu = (e: React.MouseEvent): void => {
    if (manageMode) return
    e.preventDefault()
    e.stopPropagation()
    setMenuOpen(true)
  }

  const handleDelete = (): void => {
    setMenuOpen(false)
    chatStickerService.removeCustomStickers([item.stickerId])
    useGameStore.getState().setNotice(t('combat.chat.stickerDeleted'))
  }

  const menuContent = (
    <FloatingMenuGroup>
      <FloatingMenuItem
        icon={<StatGlyph glyph="qi" size={14} />}
        label={t('combat.chat.deleteSticker')}
        onClick={handleDelete}
        tone="crimson"
      />
    </FloatingMenuGroup>
  )

  return (
    <FloatingPopover
      content={menuContent}
      interactive
      onOpenChange={setMenuOpen}
      open={menuOpen}
      placement="top"
      size="small"
      trigger="manual"
      triggerAs="span"
    >
      <button
        className={`chat-picker-sticker-btn${manageMode ? ' is-manage' : ''}${selected ? ' is-selected' : ''}${busy ? ' is-busy' : ''}`}
        disabled={busy}
        onClick={() => (manageMode ? onToggleSelect(item.stickerId) : onSelect(item))}
        onContextMenu={handleContextMenu}
        title={manageMode ? t('combat.chat.stickerSelectHint') : t('combat.chat.stickerItemHint')}
        type="button"
      >
        <ChatSticker sha256={item.sha256} url={item.url} />
        {item.needsReupload && (
          <span className="chat-picker-sticker-badge" title={t('combat.chat.stickerMissingLocalBlob')}>
            {t('combat.chat.stickerNeedsReuploadBadge')}
          </span>
        )}
        {manageMode ? (
          <span aria-hidden="true" className={`chat-picker-sticker-check${selected ? ' is-checked' : ''}`}>
            {selected ? '✓' : ''}
          </span>
        ) : (
          <span
            aria-label={t('combat.chat.deleteSticker')}
            className="chat-picker-sticker-del-icon"
            onClick={(e) => {
              e.stopPropagation()
              e.preventDefault()
              handleDelete()
            }}
            role="button"
            tabIndex={0}
            title={t('combat.chat.deleteSticker')}
          >
            <Icon name="close" size={11} />
          </span>
        )}
      </button>
    </FloatingPopover>
  )
})

/**
 * 传音表情与表情包选择器浮层面板 (ChatEmojiPicker)
 *
 * 核心交互：
 * 1. 顶部 Tab：「表情」与「表情包」；
 * 2. 表情 Tab：按使用频次自适应排序的内置动态表情网格，点击即插入；
 * 3. 表情包 Tab：支持 100 个上限展示（X/100）、批量勾选管理删除、多文件上传、链接添加与多端云端同步；
 *    点击表情包时自动探测云端有效性并在过期时从本地 IndexedDB 自动重传换链后插入输入框草稿。
 */
export const ChatEmojiPicker = memo(function ChatEmojiPicker(props: ChatEmojiPickerProps): React.JSX.Element {
  const generation = useChatImageGeneration()
  return <SessionChatEmojiPicker key={generation} {...props} generation={generation} />
})

function SessionChatEmojiPicker({
  onSelectEmoji,
  onSelectSticker,
  onClose,
  generation,
}: ChatEmojiPickerProps & { generation: number }): React.JSX.Element {
  const t = useT()
  const isCurrentSession = () => generation === chatStickerService.getGeneration()
  const [activeTab, setActiveTab] = useState<'emoji' | 'sticker'>('emoji')
  const [newStickerUrl, setNewStickerUrl] = useState('')
  const [manageMode, setManageMode] = useState(false)
  const [selectedIds, setSelectedIds] = useState<Set<string>>(() => new Set())
  const [busyId, setBusyId] = useState<string | null>(null)
  const [isAddingUrl, setIsAddingUrl] = useState(false)
  const [isUploading, setIsUploading] = useState(false)

  useSyncExternalStore(
    chatStickerService.subscribe.bind(chatStickerService),
    () => chatStickerService.getRevision(),
    () => 0
  )

  const emojis = chatStickerService.getSortedEmojis()
  const stickers = chatStickerService.getSortedStickers()
  const syncError = chatStickerService.getLastSyncError()

  useEffect(() => {
    if (activeTab === 'sticker') {
      void chatStickerService.syncWithServer()
    }
  }, [activeTab])

  useEffect(() => {
    if (stickers.length === 0 && manageMode) {
      setManageMode(false)
      setSelectedIds(new Set())
    }
  }, [stickers.length, manageMode])

  const handleEmojiClick = (emojiName: string): void => {
    chatStickerService.recordEmojiUsage(emojiName)
    onSelectEmoji(`[${emojiName}]`)
  }

  const handleStickerClick = async (item: CustomStickerItem): Promise<void> => {
    if (!isCurrentSession() || busyId) return
    setBusyId(item.stickerId)
    try {
      const ready = await chatStickerService.ensureStickerReadyForSend(item.url, blob => uploadChatImage(blob))
      if (!isCurrentSession()) return
      if (ready.missingLocalBlob) {
        useGameStore.getState().setNotice(t('combat.chat.stickerMissingLocalBlob'))
        return
      }
      if (ready.healed) {
        useGameStore.getState().setNotice(t('combat.chat.stickerHealedInserted'))
      }
      chatStickerService.recordStickerUsage(ready.url)
      onSelectSticker(ready.url)
      onClose?.()
    } finally {
      if (isCurrentSession()) setBusyId(null)
    }
  }

  const handleToggleSelect = (stickerId: string): void => {
    setSelectedIds(prev => {
      const next = new Set(prev)
      if (next.has(stickerId)) next.delete(stickerId)
      else next.add(stickerId)
      return next
    })
  }

  const handleSelectAll = (): void => {
    if (selectedIds.size >= stickers.length) {
      setSelectedIds(new Set())
    } else {
      setSelectedIds(new Set(stickers.map(s => s.stickerId)))
    }
  }

  const handleBatchDelete = (): void => {
    if (selectedIds.size === 0) return
    const removedCount = chatStickerService.removeCustomStickers(Array.from(selectedIds))
    setSelectedIds(new Set())
    if (removedCount > 0) {
      useGameStore.getState().setNotice(t('combat.chat.stickerBatchDeleted', { count: removedCount }))
    }
    if (chatStickerService.getSortedStickers().length === 0) {
      setManageMode(false)
    }
  }

  const handleAddStickerSubmit = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault()
    // 阻止 React 合成 submit 经 Portal 冒泡到外层承载表单（如攻略编辑器 <form>）引发误提交
    e.stopPropagation()
    if (!isCurrentSession() || isAddingUrl) return
    const trimmed = newStickerUrl.trim()
    if (!trimmed) return

    if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) {
      useGameStore.getState().setNotice(t('combat.chat.invalidUrl'))
      return
    }

    if (trimmed.length > MAX_STICKER_URL_LENGTH) {
      useGameStore.getState().setNotice(t('combat.chat.urlTooLong'))
      return
    }

    if (stickers.length >= MAX_CUSTOM_STICKERS && !chatStickerService.isStickerSaved(trimmed)) {
      useGameStore.getState().setNotice(t('combat.chat.stickerLimitReached', { max: MAX_CUSTOM_STICKERS }))
      return
    }

    setIsAddingUrl(true)
    try {
      const result = await chatStickerService.saveStickerFromUrl(trimmed)
      if (!isCurrentSession()) return
      if (result.ok) {
        setNewStickerUrl('')
        useGameStore.getState().setNotice(t('combat.chat.stickerAdded'))
      } else if (result.reason === 'limit') {
        useGameStore.getState().setNotice(t('combat.chat.stickerLimitReached', { max: MAX_CUSTOM_STICKERS }))
      } else if (result.reason === 'duplicate') {
        useGameStore.getState().setNotice(t('combat.chat.stickerAlreadyAdded'))
      } else {
        useGameStore.getState().setNotice(t('combat.chat.stickerInvalidImage'))
      }
    } finally {
      if (isCurrentSession()) setIsAddingUrl(false)
    }
  }

  const handleFileSelect = async (e: React.ChangeEvent<HTMLInputElement>): Promise<void> => {
    const files = Array.from(e.target.files ?? [])
    e.target.value = ''
    if (!isCurrentSession() || files.length === 0 || isUploading) return

    const currentCount = chatStickerService.getSortedStickers().length
    if (currentCount >= MAX_CUSTOM_STICKERS) {
      // 单图若与已有内容重复仍允许检测复用；全部超出上限时明确拦截提示
      const firstSha = files.length === 1 ? await computeBlobSha256(files[0]).catch(() => '') : ''
      if (!isCurrentSession()) return
      if (!firstSha || !chatStickerService.isStickerSaved('', firstSha)) {
        useGameStore.getState().setNotice(t('combat.chat.stickerLimitReached', { max: MAX_CUSTOM_STICKERS }))
        return
      }
    }

    setIsUploading(true)
    let addedCount = 0
    let skippedLimitCount = 0
    let lastSelectedUrl: string | null = null
    let convertedGif = false

    try {
      for (let i = 0; i < files.length; i++) {
        const file = files[i]
        if (files.length > 1) {
          useGameStore.getState().setNotice(
            t('combat.chat.stickerBatchUploading', { current: i + 1, total: files.length })
          )
        } else {
          useGameStore.getState().setNotice(t('combat.chat.uploadingImage'))
        }

        const preSha = await computeBlobSha256(file).catch(() => '')
        if (!isCurrentSession()) return
        const existing = preSha ? chatStickerService.findSticker(preSha) : undefined
        if (existing) {
          const ready = await chatStickerService.ensureStickerReadyForSend(existing.url, blob => uploadChatImage(blob))
          if (!isCurrentSession()) return
          lastSelectedUrl = ready.url
          await chatStickerService.saveImageBlob(ready.url, file, preSha)
          if (!isCurrentSession()) return
          continue
        }

        if (chatStickerService.getSortedStickers().length >= MAX_CUSTOM_STICKERS) {
          skippedLimitCount += files.length - i
          break
        }

        const uploaded = await uploadChatImage(file)
        if (!isCurrentSession()) return
        if (uploaded.transcodedFromGif) convertedGif = true
        const addRes = chatStickerService.addCustomStickerDetailed(uploaded.url, {
          sha256: uploaded.sha256,
          mime: uploaded.mime,
          blob: uploaded.blob,
          skipCacheFetch: true,
        })
        if (addRes.ok) {
          addedCount += 1
          lastSelectedUrl = uploaded.url
        } else if (addRes.reason === 'limit') {
          skippedLimitCount += files.length - i
          break
        } else if (addRes.item) {
          lastSelectedUrl = addRes.item.url
        }
      }

      if (files.length === 1 && lastSelectedUrl) {
        onSelectSticker(lastSelectedUrl)
        useGameStore.getState().setNotice(
          convertedGif ? t('combat.chat.stickerGifConverted') : t('combat.chat.uploadSuccess')
        )
        onClose?.()
      } else if (skippedLimitCount > 0) {
        useGameStore.getState().setNotice(
          t('combat.chat.stickerBatchUploadPartial', {
            added: addedCount,
            max: MAX_CUSTOM_STICKERS,
            skipped: skippedLimitCount,
          })
        )
      } else if (addedCount > 0) {
        useGameStore.getState().setNotice(
          t('combat.chat.stickerBatchUploadSummary', { added: addedCount })
        )
      }
    } catch (err: unknown) {
      if (!isCurrentSession()) return
      const errMsg = err instanceof Error ? err.message : t('combat.chat.uploadFailed')
      useGameStore.getState().setNotice(
        errMsg === 'IMAGE_TOO_LARGE' ? t('combat.chat.uploadImageTooLarge') : t('combat.chat.uploadFailed')
      )
    } finally {
      if (isCurrentSession()) setIsUploading(false)
    }
  }

  return (
    <div className="chat-emoji-picker">
      <div className="chat-picker-tabs">
        <button
          className={`chat-picker-tab-btn ${activeTab === 'emoji' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('emoji')}
          type="button"
        >
          {t('combat.chat.tabEmoji')}
        </button>
        <button
          className={`chat-picker-tab-btn ${activeTab === 'sticker' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('sticker')}
          type="button"
        >
          {t('combat.chat.tabSticker')}
        </button>
      </div>

      {activeTab === 'emoji' && (
        <div className="chat-picker-emoji-grid">
          {emojis.map((emojiName, index) => (
            <button
              className="chat-picker-emoji-btn"
              key={`${emojiName}-${index}`}
              onClick={() => handleEmojiClick(emojiName)}
              title={`[${emojiName}]`}
              type="button"
            >
              <QqEmoji name={emojiName} size={24} />
            </button>
          ))}
        </div>
      )}

      {activeTab === 'sticker' && (
        <div className="chat-picker-sticker-container">
          <div className="chat-picker-sticker-toolbar">
            <span
              className={`chat-picker-sticker-count${
                stickers.length >= MAX_CUSTOM_STICKERS
                  ? ' is-full'
                  : stickers.length >= 90
                    ? ' is-near'
                    : ''
              }`}
            >
              {t('combat.chat.stickerCapacity', { count: stickers.length, max: MAX_CUSTOM_STICKERS })}
            </span>
            <div className="chat-picker-sticker-actions">
              {manageMode && stickers.length > 0 && (
                <>
                  <button
                    className="chat-picker-toolbar-btn"
                    onClick={handleSelectAll}
                    type="button"
                  >
                    {selectedIds.size >= stickers.length
                      ? t('combat.chat.stickerClearSelection')
                      : t('combat.chat.stickerSelectAll')}
                  </button>
                  <button
                    className="chat-picker-toolbar-btn is-danger"
                    disabled={selectedIds.size === 0}
                    onClick={handleBatchDelete}
                    type="button"
                  >
                    {t('combat.chat.stickerBatchDelete', { count: selectedIds.size })}
                  </button>
                </>
              )}
              {stickers.length > 0 && (
                <button
                  className={`chat-picker-toolbar-btn${manageMode ? ' is-active' : ''}`}
                  onClick={() => {
                    setManageMode(prev => !prev)
                    setSelectedIds(new Set())
                  }}
                  type="button"
                >
                  {manageMode ? t('combat.chat.stickerDone') : t('combat.chat.stickerManage')}
                </button>
              )}
            </div>
          </div>

          {syncError === 'limit' && (
            <div className="chat-picker-sync-warning">
              {t('combat.chat.stickerSyncConflictLimit', { max: MAX_CUSTOM_STICKERS })}
            </div>
          )}

          <div className="chat-picker-sticker-grid">
            {stickers.map(item => (
              <StickerPickerItem
                busy={busyId === item.stickerId}
                item={item}
                key={item.stickerId}
                manageMode={manageMode}
                onSelect={handleStickerClick}
                onToggleSelect={handleToggleSelect}
                selected={selectedIds.has(item.stickerId)}
              />
            ))}
            {stickers.length === 0 && (
              <div className="chat-picker-empty">
                <p>{t('combat.chat.stickerEmptyHint')}</p>
                <small>{t('combat.chat.stickerEmptySub')}</small>
              </div>
            )}
          </div>

          <form className="chat-picker-add-form" onSubmit={(e) => void handleAddStickerSubmit(e)}>
            <input
              aria-label={t('combat.chat.addStickerPlaceholder')}
              disabled={isAddingUrl || isUploading}
              maxLength={MAX_STICKER_URL_LENGTH}
              onChange={e => setNewStickerUrl(e.target.value)}
              placeholder={t('combat.chat.addStickerPlaceholder')}
              value={newStickerUrl}
            />
            <button disabled={!newStickerUrl.trim() || isAddingUrl || isUploading} type="submit">
              {t('combat.chat.addSticker')}
            </button>
            <label className="chat-picker-upload-label" title={t('combat.chat.uploadImageTooltip')}>
              <input
                accept="image/*"
                disabled={isUploading}
                multiple
                onChange={(e) => void handleFileSelect(e)}
                style={{ display: 'none' }}
                type="file"
              />
              <Icon name="image" size={15} />
            </label>
          </form>
        </div>
      )}
    </div>
  )
}
