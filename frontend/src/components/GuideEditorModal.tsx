import React, { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  applyEditorInsertion,
  countUnicodeRunes,
  GUIDE_KAOMOJI_CATEGORIES,
  GUIDE_MAX_BODY_RUNES,
  GUIDE_MAX_PRICE_STONES,
  GUIDE_MAX_SUMMARY_RUNES,
  GUIDE_MAX_TITLE_RUNES,
  sanitizeSafeHttpImageUrl,
  type KaomojiCategoryId,
} from '../game/guideMarkup'
import { buildItemChatMarkup } from '../game/chatMarkup'
import { resolveAssetName } from '../game/entityNames'
import { uploadChatImage } from '../game/chatImageUploadService'
import { useGameStore } from '../game/gameStore'
import { findEquipmentInCatalog, findItemInCatalog, parseEnhancementVariant } from '../game/ids'
import type { GuidePostDetailDto, GuidePostUpsertPayload } from '../network/protocol'
import { useT } from '../i18n'
import { ChatEmojiPicker } from './ChatEmojiPicker'
import { FloatingPopover } from './FloatingPopover'
import { GuideRichContent } from './GuideRichContent'
import { Icon } from './Icon'
import { ItemIcon } from './ItemDisplay'
import { Modal } from './Modal'

export interface GuideEditorModalProps {
  open: boolean
  editingPost: GuidePostDetailDto | null
  submitting: boolean
  onClose: () => void
  onSubmit: (payload: GuidePostUpsertPayload) => Promise<void>
}

interface GuideLocalDraft {
  title: string
  summary: string
  body: string
  paidMode: boolean
  priceInput: string
}

const PRICE_PRESETS: readonly number[] = [10, 50, 100, 500, 1000]

function draftStorageKey(playerId: string | null): string {
  return `daohua:guide-draft:${playerId || 'anon'}`
}

function loadDraft(playerId: string | null): GuideLocalDraft | null {
  try {
    const raw = window.localStorage.getItem(draftStorageKey(playerId))
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<GuideLocalDraft>
    return {
      title: typeof parsed.title === 'string' ? parsed.title : '',
      summary: typeof parsed.summary === 'string' ? parsed.summary : '',
      body: typeof parsed.body === 'string' ? parsed.body : '',
      paidMode: Boolean(parsed.paidMode),
      priceInput: typeof parsed.priceInput === 'string' ? parsed.priceInput : '50',
    }
  } catch {
    return null
  }
}

function saveDraft(playerId: string | null, draft: GuideLocalDraft): void {
  try {
    window.localStorage.setItem(draftStorageKey(playerId), JSON.stringify(draft))
  } catch {
    // 忽略隐私浏览模式存储配额限制
  }
}

export function clearGuideLocalDraft(playerId: string | null): void {
  try {
    window.localStorage.removeItem(draftStorageKey(playerId))
  } catch {
    // 忽略
  }
}

/**
 * 独立攻略富文本创作工作台 (GuideEditorModal)
 *
 * 核心特性：
 * 1. 横竖屏全适配：宽屏默认提供「编辑 + 实时预览」分栏对照（支持切纯编辑/纯预览），竖屏提供「编辑 / 预览」顶部分段切换；
 * 2. 完整编辑工具栏：撤销/重做、H1~H3 标题、加粗、行内代码、引用、列表、代码块、分割线、
 *    游戏自带表情与个人云表情包（ChatEmojiPicker）、分类颜文字面板、图片链接插入/上传与随身物品胶囊插入；
 * 3. 支持付费解锁门槛设定（免费公开 vs 1~100,000 灵石解锁）与本地草稿自动防丢。
 */
export const GuideEditorModal = memo(function GuideEditorModal({
  open,
  editingPost,
  submitting,
  onClose,
  onSubmit,
}: GuideEditorModalProps): React.JSX.Element | null {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const inventory = useGameStore(state => state.inventory)
  const itemsCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const equippedItems = useGameStore(state => state.combat?.equipment)

  const [title, setTitle] = useState('')
  const [summary, setSummary] = useState('')
  const [body, setBody] = useState('')
  const [paidMode, setPaidMode] = useState(false)
  const [priceInput, setPriceInput] = useState('50')
  const [viewMode, setViewMode] = useState<'split' | 'write' | 'preview'>('split')
  const [mobilePane, setMobilePane] = useState<'write' | 'preview'>('write')

  const [emojiOpen, setEmojiOpen] = useState(false)
  const [kaomojiOpen, setKaomojiOpen] = useState(false)
  const [kaomojiTab, setKaomojiTab] = useState<KaomojiCategoryId>('joy')
  const [imageOpen, setImageOpen] = useState(false)
  const [imageUrlInput, setImageUrlInput] = useState('')
  const [imageAltInput, setImageAltInput] = useState('')
  const [uploadingImage, setUploadingImage] = useState(false)
  const [itemOpen, setItemOpen] = useState(false)
  const [itemFilter, setItemFilter] = useState('')
  const [formError, setFormError] = useState<string | null>(null)

  const textareaRef = useRef<HTMLTextAreaElement | null>(null)
  const fileInputRef = useRef<HTMLInputElement | null>(null)
  const historyRef = useRef<{ stack: string[]; index: number }>({ stack: [''], index: 0 })

  // 打开弹窗时初始化编辑态或恢复本地草稿
  useEffect(() => {
    if (!open) return
    setFormError(null)
    setMobilePane('write')
    if (editingPost) {
      const initBody = editingPost.body ?? ''
      setTitle(editingPost.title)
      setSummary(editingPost.summary ?? '')
      setBody(initBody)
      const isPaid = editingPost.unlockPrice > 0
      setPaidMode(isPaid)
      setPriceInput(isPaid ? String(Math.floor(editingPost.unlockPrice)) : '50')
      historyRef.current = { stack: [initBody], index: 0 }
    } else {
      const saved = loadDraft(playerId)
      const initBody = saved?.body ?? ''
      setTitle(saved?.title ?? '')
      setSummary(saved?.summary ?? '')
      setBody(initBody)
      setPaidMode(saved?.paidMode ?? false)
      setPriceInput(saved?.priceInput ?? '50')
      historyRef.current = { stack: [initBody], index: 0 }
    }
  }, [open, editingPost, playerId])

  // 新建攻略时自动防抖保存草稿
  useEffect(() => {
    if (!open || editingPost) return undefined
    const timer = window.setTimeout(() => {
      saveDraft(playerId, { title, summary, body, paidMode, priceInput })
    }, 350)
    return () => window.clearTimeout(timer)
  }, [open, editingPost, playerId, title, summary, body, paidMode, priceInput])

  const pushBodyHistory = useCallback((nextBody: string) => {
    setBody(nextBody)
    const hist = historyRef.current
    if (hist.stack[hist.index] === nextBody) return
    const nextStack = hist.stack.slice(0, hist.index + 1)
    nextStack.push(nextBody)
    if (nextStack.length > 60) nextStack.shift()
    historyRef.current = { stack: nextStack, index: nextStack.length - 1 }
  }, [])

  const handleUndo = useCallback(() => {
    const hist = historyRef.current
    if (hist.index <= 0) return
    hist.index -= 1
    setBody(hist.stack[hist.index])
  }, [])

  const handleRedo = useCallback(() => {
    const hist = historyRef.current
    if (hist.index >= hist.stack.length - 1) return
    hist.index += 1
    setBody(hist.stack[hist.index])
  }, [])

  const insertAtCursor = useCallback((options: {
    prefix: string
    suffix?: string
    placeholder?: string
    blockNewline?: boolean
  }) => {
    const el = textareaRef.current
    const start = el ? el.selectionStart : body.length
    const end = el ? el.selectionEnd : body.length
    const applied = applyEditorInsertion(body, start, end, options)
    pushBodyHistory(applied.value)
    setMobilePane('write')
    window.requestAnimationFrame(() => {
      const target = textareaRef.current
      if (!target) return
      target.focus()
      target.setSelectionRange(applied.nextStart, applied.nextEnd)
    })
  }, [body, pushBodyHistory])

  const handleUploadFile = useCallback(async (file: File) => {
    if (uploadingImage) return
    setUploadingImage(true)
    setFormError(null)
    try {
      const uploaded = await uploadChatImage(file)
      insertAtCursor({ prefix: `![${file.name.replace(/\.[^.]+$/, '')}](${uploaded.url})`, blockNewline: true })
      setImageOpen(false)
    } catch (err) {
      setFormError(err instanceof Error ? err.message : t('social.guide.uploadFailed'))
    } finally {
      setUploadingImage(false)
    }
  }, [insertAtCursor, t, uploadingImage])

  const handlePaste = useCallback((e: React.ClipboardEvent<HTMLTextAreaElement>) => {
    const items = e.clipboardData?.items
    if (!items) return
    for (let i = 0; i < items.length; i += 1) {
      const item = items[i]
      if (item.type.startsWith('image/')) {
        const file = item.getAsFile()
        if (file) {
          e.preventDefault()
          void handleUploadFile(file)
          return
        }
      }
    }
  }, [handleUploadFile])

  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if ((e.ctrlKey || e.metaKey) && !e.altKey) {
      const key = e.key.toLowerCase()
      if (key === 'b') {
        e.preventDefault()
        insertAtCursor({ prefix: '**', suffix: '**', placeholder: t('social.guide.boldPlaceholder') })
      } else if (key === 'z' && e.shiftKey) {
        e.preventDefault()
        handleRedo()
      } else if (key === 'y') {
        e.preventDefault()
        handleRedo()
      }
    }
  }, [handleRedo, insertAtCursor, t])

  // 汇总玩家已穿戴装备与背包物品供快捷插入胶囊
  const candidateItems = useMemo(() => {
    const seen = new Set<string>()
    const list: Array<{ key: string; itemId: string; enhancementLevel: number; label: string }> = []

    for (const eq of equippedItems ?? []) {
      if (!eq?.itemId) continue
      const lvl = Math.max(0, Math.floor(eq.enhancementLevel ?? 0))
      const key = `${eq.itemId}:${lvl}`
      if (seen.has(key)) continue
      seen.add(key)
      const def = findItemInCatalog(itemsCatalog, eq.itemId)
      const eqDef = findEquipmentInCatalog(equipmentCatalog, eq.itemId)
      const base = resolveAssetName(t, eq.itemId, def?.name, eqDef?.name)
      list.push({ key, itemId: eq.itemId, enhancementLevel: lvl, label: lvl > 0 ? `${base} +${lvl}` : base })
    }

    for (const inv of inventory) {
      if (!inv.itemId || inv.amount <= 0) continue
      const lvl = parseEnhancementVariant(inv.variant)
      const key = `${inv.itemId}:${lvl}`
      if (seen.has(key)) continue
      seen.add(key)
      const def = findItemInCatalog(itemsCatalog, inv.itemId)
      const eqDef = findEquipmentInCatalog(equipmentCatalog, inv.itemId)
      const base = resolveAssetName(t, inv.itemId, def?.name, eqDef?.name)
      list.push({ key, itemId: inv.itemId, enhancementLevel: lvl, label: lvl > 0 ? `${base} +${lvl}` : base })
    }

    const q = itemFilter.trim().toLowerCase()
    return q ? list.filter(entry => entry.label.toLowerCase().includes(q) || entry.itemId.toLowerCase().includes(q)) : list.slice(0, 60)
  }, [equippedItems, equipmentCatalog, inventory, itemFilter, itemsCatalog, t])

  const titleCount = useMemo(() => countUnicodeRunes(title.trim()), [title])
  const summaryCount = useMemo(() => countUnicodeRunes(summary.trim()), [summary])
  const bodyCount = useMemo(() => countUnicodeRunes(body.trim()), [body])

  if (!open) return null

  const handleInsertImageUrl = (): void => {
    const safe = sanitizeSafeHttpImageUrl(imageUrlInput)
    if (!safe) {
      setFormError(t('social.guide.invalidImageUrl'))
      return
    }
    const cleanAlt = imageAltInput.trim().replace(/[\[\]\n\r]/g, '')
    insertAtCursor({ prefix: `![${cleanAlt}](${safe})`, blockNewline: true })
    setImageUrlInput('')
    setImageAltInput('')
    setImageOpen(false)
    setFormError(null)
  }

  const handleClearDraft = (): void => {
    setTitle('')
    setSummary('')
    pushBodyHistory('')
    setPaidMode(false)
    setPriceInput('50')
    if (!editingPost) clearGuideLocalDraft(playerId)
  }

  const handleFormSubmit = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault()
    setFormError(null)

    if (titleCount < 1 || titleCount > GUIDE_MAX_TITLE_RUNES) {
      setFormError(t('social.guide.errorTitleLength', { max: GUIDE_MAX_TITLE_RUNES }))
      return
    }
    if (summaryCount > GUIDE_MAX_SUMMARY_RUNES) {
      setFormError(t('social.guide.errorSummaryLength', { max: GUIDE_MAX_SUMMARY_RUNES }))
      return
    }
    if (bodyCount < 1 || bodyCount > GUIDE_MAX_BODY_RUNES) {
      setFormError(t('social.guide.errorBodyLength', { max: GUIDE_MAX_BODY_RUNES }))
      return
    }

    let unlockPrice = 0
    if (paidMode) {
      const parsed = Number(priceInput)
      if (!Number.isFinite(parsed) || !Number.isInteger(parsed) || parsed < 1 || parsed > GUIDE_MAX_PRICE_STONES) {
        setFormError(t('social.guide.errorPriceRange', { max: GUIDE_MAX_PRICE_STONES }))
        return
      }
      unlockPrice = parsed
    }

    try {
      await onSubmit({
        title: title.trim(),
        summary: summary.trim(),
        body: body.trim(),
        unlockPrice,
      })
    } catch (err) {
      setFormError(err instanceof Error ? err.message : t('social.guide.submitFailed'))
    }
  }

  const activeKaomojiGroup = GUIDE_KAOMOJI_CATEGORIES.find(c => c.id === kaomojiTab) ?? GUIDE_KAOMOJI_CATEGORIES[0]

  return (
    <Modal
      className="guide-editor-modal"
      labelledBy="guide-editor-title"
      onClose={submitting ? () => {} : onClose}
      open={open}
      size="large"
    >
      <form className="guide-editor-shell" onSubmit={handleFormSubmit}>
        <header className="guide-editor-header">
          <div className="guide-editor-heading">
            <Icon name="codex" size={18} />
            <h3 id="guide-editor-title">
              {editingPost ? t('social.guide.editModalTitle') : t('social.guide.createModalTitle')}
            </h3>
          </div>

          {/* 桌面横屏视图切换（分栏 / 编辑 / 预览） */}
          <div className="insight-sub-tabs guide-editor-desktop-modes" role="tablist">
            {(['split', 'write', 'preview'] as const).map(mode => (
              <button
                aria-selected={viewMode === mode}
                className={`insight-sub-tab-btn${viewMode === mode ? ' active' : ''}`}
                key={mode}
                onClick={() => setViewMode(mode)}
                role="tab"
                type="button"
              >
                {t(`social.guide.mode.${mode}`)}
              </button>
            ))}
          </div>

          {/* 移动竖屏视图切换（编辑 / 预览） */}
          <div className="insight-sub-tabs guide-editor-mobile-modes" role="tablist">
            {(['write', 'preview'] as const).map(pane => (
              <button
                aria-selected={mobilePane === pane}
                className={`insight-sub-tab-btn${mobilePane === pane ? ' active' : ''}`}
                key={pane}
                onClick={() => setMobilePane(pane)}
                role="tab"
                type="button"
              >
                {t(`social.guide.mode.${pane}`)}
              </button>
            ))}
          </div>

          <button
            aria-label={t('social.zongmen.cancel')}
            className="guide-editor-close-btn"
            disabled={submitting}
            onClick={onClose}
            type="button"
          >
            <Icon name="close" size={16} />
          </button>
        </header>

        {/* 元数据栏：标题、导语与付费门槛 */}
        <section className="guide-editor-meta">
          <div className="guide-editor-meta-row">
            <label className="guide-editor-field guide-editor-field--title">
              <span className="guide-editor-label">
                {t('social.guide.fieldTitle')}
                <small className={titleCount > GUIDE_MAX_TITLE_RUNES ? 'is-over' : ''}>
                  {titleCount}/{GUIDE_MAX_TITLE_RUNES}
                </small>
              </span>
              <input
                maxLength={120}
                onChange={e => setTitle(e.target.value.replace(/[\r\n]/g, ''))}
                placeholder={t('social.guide.titlePlaceholder')}
                required
                type="text"
                value={title}
              />
            </label>

            <div className="guide-editor-field guide-editor-field--price">
              <span className="guide-editor-label">{t('social.guide.fieldUnlockPrice')}</span>
              <div className="guide-editor-price-controls">
                <div className="presence-options guide-price-mode-toggle" role="group">
                  <button
                    aria-pressed={!paidMode}
                    className={!paidMode ? 'active' : ''}
                    onClick={() => setPaidMode(false)}
                    type="button"
                  >
                    {t('social.guide.freePublic')}
                  </button>
                  <button
                    aria-pressed={paidMode}
                    className={paidMode ? 'active' : ''}
                    onClick={() => setPaidMode(true)}
                    type="button"
                  >
                    {t('social.guide.paidUnlock')}
                  </button>
                </div>
                {paidMode && (
                  <div className="guide-editor-price-input-wrap">
                    <ItemIcon itemId="currency.spirit-stone" />
                    <input
                      aria-label={t('social.guide.fieldUnlockPrice')}
                      max={GUIDE_MAX_PRICE_STONES}
                      min={1}
                      onChange={e => setPriceInput(e.target.value)}
                      step={1}
                      type="number"
                      value={priceInput}
                    />
                    <div className="guide-price-presets">
                      {PRICE_PRESETS.map(preset => (
                        <button
                          className={Number(priceInput) === preset ? 'active' : ''}
                          key={preset}
                          onClick={() => setPriceInput(String(preset))}
                          type="button"
                        >
                          {preset}
                        </button>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            </div>
          </div>

          <label className="guide-editor-field guide-editor-field--summary">
            <span className="guide-editor-label">
              {t('social.guide.fieldSummary')}
              <small className={summaryCount > GUIDE_MAX_SUMMARY_RUNES ? 'is-over' : ''}>
                {summaryCount}/{GUIDE_MAX_SUMMARY_RUNES}
              </small>
            </span>
            <input
              maxLength={360}
              onChange={e => setSummary(e.target.value.replace(/[\r\n]/g, ' '))}
              placeholder={paidMode ? t('social.guide.summaryPaidPlaceholder') : t('social.guide.summaryFreePlaceholder')}
              type="text"
              value={summary}
            />
          </label>
        </section>

        {/* 多功能富文本插入工具栏 */}
        <div aria-label={t('social.guide.toolbarAria')} className="guide-editor-toolbar" role="toolbar">
          <button className="guide-tool-btn" onClick={handleUndo} title={t('social.guide.toolUndo')} type="button">↶</button>
          <button className="guide-tool-btn" onClick={handleRedo} title={t('social.guide.toolRedo')} type="button">↷</button>
          <span className="guide-tool-sep" />
          <button className="guide-tool-btn" onClick={() => insertAtCursor({ prefix: '# ', placeholder: t('social.guide.headingPlaceholder'), blockNewline: true })} type="button">H1</button>
          <button className="guide-tool-btn" onClick={() => insertAtCursor({ prefix: '## ', placeholder: t('social.guide.headingPlaceholder'), blockNewline: true })} type="button">H2</button>
          <button className="guide-tool-btn" onClick={() => insertAtCursor({ prefix: '### ', placeholder: t('social.guide.headingPlaceholder'), blockNewline: true })} type="button">H3</button>
          <button className="guide-tool-btn guide-tool-btn--bold" onClick={() => insertAtCursor({ prefix: '**', suffix: '**', placeholder: t('social.guide.boldPlaceholder') })} type="button">B</button>
          <button className="guide-tool-btn" onClick={() => insertAtCursor({ prefix: '> ', placeholder: t('social.guide.quotePlaceholder'), blockNewline: true })} type="button">{t('social.guide.toolQuote')}</button>
          <button className="guide-tool-btn" onClick={() => insertAtCursor({ prefix: '- ', placeholder: t('social.guide.listPlaceholder'), blockNewline: true })} type="button">• {t('social.guide.toolList')}</button>
          <button className="guide-tool-btn" onClick={() => insertAtCursor({ prefix: '1. ', placeholder: t('social.guide.listPlaceholder'), blockNewline: true })} type="button">1.</button>
          <button className="guide-tool-btn" onClick={() => insertAtCursor({ prefix: '`', suffix: '`', placeholder: 'code' })} type="button">{'</>'}</button>
          <button className="guide-tool-btn" onClick={() => insertAtCursor({ prefix: '---', blockNewline: true })} type="button">—</button>
          <span className="guide-tool-sep" />

          {/* 游戏自带动态表情与个人云表情包选择器 */}
          <FloatingPopover
            content={
              <div className="guide-emoji-popover">
                <ChatEmojiPicker
                  onClose={() => setEmojiOpen(false)}
                  onSelectEmoji={emojiToken => {
                    insertAtCursor({ prefix: emojiToken })
                    setEmojiOpen(false)
                  }}
                  onSelectSticker={stickerUrl => {
                    insertAtCursor({ prefix: `![](${stickerUrl})`, blockNewline: true })
                    setEmojiOpen(false)
                  }}
                />
              </div>
            }
            interactive
            onOpenChange={setEmojiOpen}
            open={emojiOpen}
            placement="bottom-start"
            size="auto"
            trigger="click"
            triggerAs="button"
            triggerClassName="guide-tool-btn guide-tool-btn--accent"
          >
            <Icon name="smile" size={14} />
            <span>{t('social.guide.toolEmoji')}</span>
          </FloatingPopover>

          {/* 内置分类颜文字快捷插入器 */}
          <FloatingPopover
            content={({ close }) => (
              <div className="guide-kaomoji-popover">
                <div className="insight-sub-tabs guide-kaomoji-tabs">
                  {GUIDE_KAOMOJI_CATEGORIES.map(cat => (
                    <button
                      className={`insight-sub-tab-btn${kaomojiTab === cat.id ? ' active' : ''}`}
                      key={cat.id}
                      onClick={() => setKaomojiTab(cat.id)}
                      type="button"
                    >
                      {t(`social.guide.kaomoji.${cat.id}`)}
                    </button>
                  ))}
                </div>
                <div className="guide-kaomoji-grid">
                  {activeKaomojiGroup.items.map(km => (
                    <button
                      className="guide-kaomoji-item"
                      key={km}
                      onClick={() => {
                        insertAtCursor({ prefix: ` ${km} ` })
                        close()
                      }}
                      type="button"
                    >
                      {km}
                    </button>
                  ))}
                </div>
              </div>
            )}
            interactive
            onOpenChange={setKaomojiOpen}
            open={kaomojiOpen}
            placement="bottom-start"
            size="auto"
            trigger="click"
            triggerAs="button"
            triggerClassName="guide-tool-btn guide-tool-btn--accent"
          >
            <span>(๑•̀ㅂ•́)و✧</span>
            <span>{t('social.guide.toolKaomoji')}</span>
          </FloatingPopover>

          {/* 图片链接插入与本地上传 */}
          <FloatingPopover
            content={
              <div className="guide-image-popover">
                <label className="guide-popover-field">
                  <span>{t('social.guide.imageUrlLabel')}</span>
                  <input
                    onChange={e => setImageUrlInput(e.target.value)}
                    placeholder="https://example.com/guide.webp"
                    type="url"
                    value={imageUrlInput}
                  />
                </label>
                <label className="guide-popover-field">
                  <span>{t('social.guide.imageAltLabel')}</span>
                  <input
                    onChange={e => setImageAltInput(e.target.value)}
                    placeholder={t('social.guide.imageAltPlaceholder')}
                    type="text"
                    value={imageAltInput}
                  />
                </label>
                <div className="guide-popover-actions">
                  <button
                    className="inline-modify-btn"
                    disabled={uploadingImage}
                    onClick={() => fileInputRef.current?.click()}
                    type="button"
                  >
                    {uploadingImage ? t('social.guide.uploadingImage') : t('social.guide.uploadLocalImage')}
                  </button>
                  <button className="primary-btn" onClick={handleInsertImageUrl} type="button">
                    {t('social.guide.insertImageBtn')}
                  </button>
                </div>
              </div>
            }
            interactive
            onOpenChange={setImageOpen}
            open={imageOpen}
            placement="bottom-start"
            size="auto"
            trigger="click"
            triggerAs="button"
            triggerClassName="guide-tool-btn guide-tool-btn--accent"
          >
            <Icon name="image" size={14} />
            <span>{t('social.guide.toolImage')}</span>
          </FloatingPopover>

          {/* 快捷插入装备/物品图鉴胶囊 */}
          <FloatingPopover
            content={({ close }) => (
              <div className="guide-item-popover">
                <input
                  className="guide-item-search"
                  onChange={e => setItemFilter(e.target.value)}
                  placeholder={t('social.guide.searchItemPlaceholder')}
                  type="search"
                  value={itemFilter}
                />
                <div className="guide-item-list">
                  {candidateItems.length === 0 ? (
                    <p className="guide-item-empty">{t('social.guide.noCandidateItems')}</p>
                  ) : (
                    candidateItems.map(entry => (
                      <button
                        className="guide-item-option"
                        key={entry.key}
                        onClick={() => {
                          insertAtCursor({ prefix: buildItemChatMarkup(entry.itemId, entry.enhancementLevel) })
                          close()
                        }}
                        type="button"
                      >
                        <ItemIcon itemId={entry.itemId} />
                        <span>{entry.label}</span>
                      </button>
                    ))
                  )}
                </div>
              </div>
            )}
            interactive
            onOpenChange={setItemOpen}
            open={itemOpen}
            placement="bottom-start"
            size="auto"
            trigger="click"
            triggerAs="button"
            triggerClassName="guide-tool-btn"
          >
            <Icon name="inventory" size={14} />
            <span>{t('social.guide.toolItem')}</span>
          </FloatingPopover>

          <input
            accept="image/png,image/jpeg,image/webp,image/gif"
            hidden
            onChange={e => {
              const file = e.target.files?.[0]
              e.target.value = ''
              if (file) void handleUploadFile(file)
            }}
            ref={fileInputRef}
            type="file"
          />
        </div>

        {/* 主体区：横屏分栏 / 竖屏单栏切换 */}
        <div className={`guide-editor-workspace is-desktop-${viewMode} is-mobile-${mobilePane}`}>
          <div className="guide-editor-pane guide-editor-pane--write">
            <textarea
              aria-label={t('social.guide.bodyAria')}
              className="guide-editor-textarea"
              onChange={e => pushBodyHistory(e.target.value)}
              onKeyDown={handleKeyDown}
              onPaste={handlePaste}
              placeholder={t('social.guide.bodyPlaceholder')}
              ref={textareaRef}
              value={body}
            />
          </div>

          <div className="guide-editor-pane guide-editor-pane--preview">
            <div className="guide-editor-preview-header">
              <span>{t('social.guide.livePreview')}</span>
              {paidMode && (
                <span className="guide-preview-paid-tag">
                  <Icon name="lock" size={11} />
                  <ItemIcon itemId="currency.spirit-stone" />
                  <span>{priceInput || '0'}</span>
                </span>
              )}
            </div>
            <div className="guide-editor-preview-scroll">
              <h2 className="guide-detail-title">{title.trim() || t('social.guide.untitledPreview')}</h2>
              {summary.trim() && <p className="guide-detail-summary">{summary.trim()}</p>}
              {body.trim() ? (
                <GuideRichContent body={body} />
              ) : (
                <p className="guide-preview-empty">{t('social.guide.previewEmptyHint')}</p>
              )}
            </div>
          </div>
        </div>

        {formError && <p className="guide-editor-error" role="alert">{formError}</p>}

        <footer className="guide-editor-footer">
          <div className="guide-editor-footer-meta">
            <span className={`guide-char-counter${bodyCount > GUIDE_MAX_BODY_RUNES ? ' is-over' : ''}`}>
              {t('social.guide.charCount', { current: bodyCount, max: GUIDE_MAX_BODY_RUNES })}
            </span>
            {paidMode && <span className="guide-tax-hint">{t('social.guide.taxHint')}</span>}
          </div>

          <div className="guide-editor-footer-actions">
            <button
              className="inline-modify-btn"
              disabled={submitting}
              onClick={handleClearDraft}
              type="button"
            >
              {t('social.guide.clearBtn')}
            </button>
            <button
              className="inline-modify-btn"
              disabled={submitting}
              onClick={onClose}
              type="button"
            >
              {t('social.zongmen.cancel')}
            </button>
            <button
              className="primary-btn guide-publish-btn"
              disabled={submitting || titleCount < 1 || bodyCount < 1 || bodyCount > GUIDE_MAX_BODY_RUNES}
              type="submit"
            >
              {submitting
                ? t('social.guide.submitting')
                : editingPost
                  ? t('social.guide.saveEditBtn')
                  : t('social.guide.publishBtn')}
            </button>
          </div>
        </footer>
      </form>
    </Modal>
  )
})
