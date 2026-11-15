import { useEffect, useId, useMemo, useRef, useState } from 'react'
import {
  HELP_CHAPTERS,
  HELP_TOTAL_SECTIONS,
  filterHelpChapters,
  type HelpChapterId,
} from '../data/helpGuide'
import { useT } from '../i18n'
import { useUiStore } from '../uiStore'
import { RichTemplateText } from './EntityReference'
import { Icon } from './Icon'
import { Modal } from './Modal'

export interface HelpModalProps {
  /** 弹窗开启状态 */
  open: boolean
  /** 关闭弹窗回调 */
  onClose: () => void
}

/**
 * 「其他 → 帮助」修行指南独立大尺寸弹窗：
 * - 固定外框与顶栏/目录，仅右侧正文区域（竖屏下方正文区域）独立滚动；
 * - 竖屏（<= 960px）自动将章节目录收拢到顶部横向切换条；
 * - 紧凑分点卡片提升信息密度，保留 RichTemplateText 悬浮与页签一键直达。
 */
export function HelpModal({ open, onClose }: HelpModalProps): React.JSX.Element | null {
  const t = useT()
  const executeHelpJump = useUiStore(state => state.executeHelpJump)
  const titleId = useId()
  const contentRef = useRef<HTMLElement>(null)
  const [query, setQuery] = useState('')
  const [activeChapterId, setActiveChapterId] = useState<HelpChapterId>('intro')

  const filteredChapters = useMemo(
    () => filterHelpChapters(HELP_CHAPTERS, query, t),
    [query, t],
  )

  const matchedSectionCount = useMemo(
    () => filteredChapters.reduce((sum, chapter) => sum + chapter.sections.length, 0),
    [filteredChapters],
  )

  const activeChapter = useMemo(() => {
    if (filteredChapters.length === 0) return null
    return filteredChapters.find(chapter => chapter.id === activeChapterId) ?? filteredChapters[0]
  }, [filteredChapters, activeChapterId])

  // 切换章节或更改检索词时，重置右侧教程正文滚动位置到顶部
  useEffect(() => {
    if (!open || !contentRef.current) return
    contentRef.current.scrollTop = 0
  }, [open, activeChapter?.id, query])

  if (!open) return null

  const hasQuery = query.trim().length > 0

  return (
    <Modal
      className="help-modal"
      labelledBy={titleId}
      onClose={onClose}
      open={open}
      size="large"
    >
      <div className="help-modal-inner">
        <header className="help-header">
          <div className="help-header-brand">
            <span aria-hidden="true" className="help-title-icon">
              <Icon name="help" size={16} />
            </span>
            <h2 className="help-title" id={titleId}>{t('app.help.title')}</h2>
            <span className="help-count-badge">
              {t('app.help.chapterCountBadge', {
                count: HELP_CHAPTERS.length,
                sectionCount: HELP_TOTAL_SECTIONS,
              })}
            </span>
            <span className="help-hover-inline">
              <Icon name="codex" size={13} />
              <span>{t('app.help.hoverHint')}</span>
            </span>
          </div>

          <div className="help-search-row">
            <label className="help-search-box">
              <span aria-hidden="true" className="help-search-icon">
                <Icon name="search" size={14} />
              </span>
              <input
                aria-label={t('app.help.searchPlaceholder')}
                className="help-search-input"
                onChange={event => setQuery(event.target.value)}
                placeholder={t('app.help.searchPlaceholder')}
                type="search"
                value={query}
              />
              {hasQuery && (
                <button
                  className="help-search-clear"
                  onClick={() => setQuery('')}
                  type="button"
                >
                  {t('app.help.searchClear')}
                </button>
              )}
            </label>
            {hasQuery && (
              <span className="help-search-summary">
                {t('app.help.searchResultSummary', { sectionCount: matchedSectionCount })}
              </span>
            )}
          </div>
        </header>

        <div className="help-layout">
          {filteredChapters.length > 0 && (
            <nav aria-label={t('app.help.chapterNavAria')} className="help-sidebar" role="tablist">
              {filteredChapters.map(chapter => {
                const selected = activeChapter?.id === chapter.id
                return (
                  <button
                    aria-selected={selected}
                    className={`help-chapter-tab${selected ? ' is-active' : ''}`}
                    key={chapter.id}
                    onClick={() => setActiveChapterId(chapter.id)}
                    role="tab"
                    type="button"
                  >
                    <span className="help-chapter-tab-main">
                      <span className="help-chapter-badge">{t(chapter.badgeKey)}</span>
                      <span className="help-chapter-tab-title">{t(chapter.titleKey)}</span>
                    </span>
                    <span className="help-chapter-count">{chapter.sections.length}</span>
                  </button>
                )
              })}
            </nav>
          )}

          <section className="help-content" ref={contentRef}>
            {!activeChapter ? (
              <div className="help-empty">
                <span aria-hidden="true" className="help-empty-icon">
                  <Icon name="search" size={24} />
                </span>
                <h3 className="help-empty-title">{t('app.help.searchEmptyTitle')}</h3>
                <p className="help-empty-desc">{t('app.help.searchEmptyDesc')}</p>
                {hasQuery && (
                  <button
                    className="help-btn help-btn--secondary"
                    onClick={() => setQuery('')}
                    type="button"
                  >
                    {t('app.help.searchClear')}
                  </button>
                )}
              </div>
            ) : (
              <>
                <div className="help-chapter-head">
                  <span className="help-chapter-head-badge">{t(activeChapter.badgeKey)}</span>
                  <h3 className="help-chapter-head-title">{t(activeChapter.titleKey)}</h3>
                  <span className="help-chapter-head-summary">{t(activeChapter.summaryKey)}</span>
                </div>

                <div className="help-section-list">
                  {activeChapter.sections.map((section, index) => {
                    const bullets = t(section.bodyKey)
                      .split('\n')
                      .map(line => line.trim())
                      .filter(Boolean)
                    const ordinal = String(index + 1).padStart(2, '0')
                    return (
                      <article className="help-card" key={section.id}>
                        <header className="help-card-header">
                          <div className="help-card-title-group">
                            <span aria-hidden="true" className="help-card-ordinal">{ordinal}</span>
                            <h4 className="help-card-title">{t(section.titleKey)}</h4>
                            <span className="help-card-tag">{t(section.tagKey)}</span>
                          </div>
                          <button
                            className="help-jump-btn"
                            onClick={() => executeHelpJump(section.jumpTarget)}
                            type="button"
                          >
                            <span>{t(section.jumpLabelKey)}</span>
                            <span aria-hidden="true">›</span>
                          </button>
                        </header>

                        <ul className="help-bullet-list">
                          {bullets.map((bullet, bIndex) => (
                            <li className="help-bullet-item" key={bIndex}>
                              <RichTemplateText onNavigate={onClose} template={bullet} />
                            </li>
                          ))}
                        </ul>

                        <div className="help-card-tip">
                          <span aria-hidden="true" className="help-card-tip-icon">
                            <Icon name="alert" size={13} />
                          </span>
                          <span className="help-card-tip-label">{t('app.help.tipLabel')}</span>
                          <span className="help-card-tip-text">
                            <RichTemplateText onNavigate={onClose} template={t(section.tipKey)} />
                          </span>
                        </div>
                      </article>
                    )
                  })}
                </div>
              </>
            )}
          </section>
        </div>
      </div>
    </Modal>
  )
}
