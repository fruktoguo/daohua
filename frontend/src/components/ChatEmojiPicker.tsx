import { memo, useState, useSyncExternalStore } from 'react'
import { chatStickerService, type CustomStickerItem } from '../game/chatStickerService'
import { ChatSticker } from './ChatSticker'
import { QqEmoji } from './QqEmoji'
import { FloatingMenuGroup, FloatingMenuItem, FloatingPopover } from './FloatingPopover'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { StatGlyph } from './StatGlyph'

export interface ChatEmojiPickerProps {
  onSelectEmoji: (emoji: string) => void
  onSelectSticker: (url: string) => void
  onClose?: () => void
}

/** 单个表情包网格项（带右键删除菜单） */
const StickerPickerItem = memo(function StickerPickerItem({
  item,
  onSelect,
}: {
  item: CustomStickerItem
  onSelect: (url: string) => void
}): React.JSX.Element {
  const t = useT()
  const [menuOpen, setMenuOpen] = useState(false)

  const handleContextMenu = (e: React.MouseEvent): void => {
    e.preventDefault()
    e.stopPropagation()
    setMenuOpen(true)
  }

  const handleDelete = (): void => {
    setMenuOpen(false)
    chatStickerService.removeCustomSticker(item.url)
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
        className="chat-picker-sticker-btn"
        onClick={() => onSelect(item.url)}
        onContextMenu={handleContextMenu}
        title={t('combat.chat.stickerItemHint')}
        type="button"
      >
        <ChatSticker url={item.url} />
      </button>
    </FloatingPopover>
  )
})

/**
 * 传音表情与表情包选择器浮层面板 (ChatEmojiPicker)
 *
 * 核心交互：
 * 1. 顶部 Tab：「表情」与「表情包」；
 * 2. 表情 Tab：按使用频次自适应排序的原生 Emoji 网格，点击即插入；
 * 3. 表情包 Tab：按使用频次自适应排序的图片表情网格（未加载显 [图片] 占位），右键可删除；
 * 4. 底部支持手动快捷录入图片直链（长度不超过 200 字符限制）。
 */
export const ChatEmojiPicker = memo(function ChatEmojiPicker({
  onSelectEmoji,
  onSelectSticker,
  onClose,
}: ChatEmojiPickerProps): React.JSX.Element {
  const t = useT()
  const [activeTab, setActiveTab] = useState<'emoji' | 'sticker'>('emoji')
  const [newStickerUrl, setNewStickerUrl] = useState('')

  // 响应表情/表情包数据变动（频次增加或添加/删除）
  useSyncExternalStore(
    chatStickerService.subscribe.bind(chatStickerService),
    () => chatStickerService.getSortedStickers().length + chatStickerService.getSortedEmojis().length,
    () => 0
  )

  const emojis = chatStickerService.getSortedEmojis()
  const stickers = chatStickerService.getSortedStickers()

  const handleEmojiClick = (emojiName: string): void => {
    chatStickerService.recordEmojiUsage(emojiName)
    onSelectEmoji(`[${emojiName}]`)
  }

  const handleStickerClick = (url: string): void => {
    chatStickerService.recordStickerUsage(url)
    onSelectSticker(url)
    onClose?.()
  }

  const handleAddStickerSubmit = (e: React.FormEvent): void => {
    e.preventDefault()
    const trimmed = newStickerUrl.trim()
    if (!trimmed) return

    if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) {
      useGameStore.getState().setNotice(t('combat.chat.invalidUrl'))
      return
    }

    if (trimmed.length > 200) {
      useGameStore.getState().setNotice(t('combat.chat.urlTooLong'))
      return
    }

    const success = chatStickerService.addCustomSticker(trimmed)
    if (success) {
      setNewStickerUrl('')
      useGameStore.getState().setNotice(t('combat.chat.stickerAdded'))
      // 预先拉取并缓存图片到 IndexedDB
      chatStickerService.loadImageWithCache(trimmed).catch(() => {})
    } else {
      useGameStore.getState().setNotice(t('combat.chat.stickerAlreadyAdded'))
    }
  }

  return (
    <div className="chat-emoji-picker">
      {/* 顶部 Tab 栏 */}
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

      {/* 表情 Tab（经典 QQ 小黄脸） */}
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

      {/* 表情包 Tab */}
      {activeTab === 'sticker' && (
        <div className="chat-picker-sticker-container">
          <div className="chat-picker-sticker-grid">
            {stickers.map(item => (
              <StickerPickerItem
                item={item}
                key={item.url}
                onSelect={handleStickerClick}
              />
            ))}
            {stickers.length === 0 && (
              <div className="chat-picker-empty">
                <p>{t('combat.chat.stickerEmptyHint')}</p>
                <small>{t('combat.chat.stickerEmptySub')}</small>
              </div>
            )}
          </div>

          {/* 快捷录入添加外链表情 */}
          <form className="chat-picker-add-form" onSubmit={handleAddStickerSubmit}>
            <input
              aria-label={t('combat.chat.addStickerPlaceholder')}
              maxLength={200}
              onChange={e => setNewStickerUrl(e.target.value)}
              placeholder={t('combat.chat.addStickerPlaceholder')}
              value={newStickerUrl}
            />
            <button disabled={!newStickerUrl.trim()} type="submit">
              {t('combat.chat.addSticker')}
            </button>
          </form>
        </div>
      )}
    </div>
  )
})
