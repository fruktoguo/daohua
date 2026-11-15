import { useMemo, useState } from 'react'
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

/**
 * 「其他 → 帮助」修行指南页面：
 * 采用「顶部搜索 + 左侧章节目录 + 右侧条目卡片」布局，支持富文本实体悬浮卡与页签一键直达。
 */
export function HelpPage(): React.JSX.Element {
  const t = useT()
  const executeHelpJump = useUiStore(state => state.executeHelpJump)
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

  const hasQuery = query.trim().length > 0

  return (
    <div className="help-page">
      <header className="help-header">
        <div className="help-title-row">
          <div className="help-title-wrap">
            <span aria-hidden="true" className="help-title-icon">
              <Icon name="help" size={18} />
            </span>
            <h2 className="help-title">{t('app.help.title')}</h2>
            <span className="help-count-badge">
              {t('app.help.chapterCountBadge', {
                count: HELP_CHAPTERS.length,
                sectionCount: HELP_TOTAL_SECTIONS,
              })}
            </span>
          </div>
          <p className="help-subtitle">{t('app.help.subtitle')}</p>
        </div>

        <div className="help-hover-banner">
          <Icon name="codex" size={14} />
          <span>{t('app.help.hoverHint')}</span>
        </div>

        <div className="help-search-row">
          <label className="help-search-box">
            <span aria-hidden="true" className="help-search-icon">
              <Icon name="search" size={15} />
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
                  <span className="help-chapter-tab-top">
                    <span className="help-chapter-badge">{t(chapter.badgeKey)}</span>
                    <span className="help-chapter-count">{chapter.sections.length}</span>
                  </span>
                  <span className="help-chapter-tab-title">{t(chapter.titleKey)}</span>
                </button>
              )
            })}
          </nav>
        )}

        <section className="help-content">
          {!activeChapter ? (
            <div className="help-empty">
              <span aria-hidden="true" className="help-empty-icon">
                <Icon name="search" size={28} />
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
              <div className="help-chapter-banner">
                <div className="help-chapter-banner-head">
                  <span className="help-chapter-banner-badge">{t(activeChapter.badgeKey)}</span>
                  <h3 className="help-chapter-banner-title">{t(activeChapter.titleKey)}</h3>
                </div>
                <p className="help-chapter-banner-summary">{t(activeChapter.summaryKey)}</p>
              </div>

              <div className="help-section-list">
                {activeChapter.sections.map((section, index) => {
                  const paragraphs = t(section.bodyKey)
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

                      <div className="help-card-body">
                        {paragraphs.map((paragraph, pIndex) => (
                          <p key={pIndex}>
                            <RichTemplateText template={paragraph} />
                          </p>
                        ))}
                      </div>

                      <div className="help-card-tip">
                        <span aria-hidden="true" className="help-card-tip-icon">
                          <Icon name="alert" size={14} />
                        </span>
                        <div className="help-card-tip-content">
                          <span className="help-card-tip-label">{t('app.help.tipLabel')}</span>
                          <span className="help-card-tip-text">
                            <RichTemplateText template={t(section.tipKey)} />
                          </span>
                        </div>
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
  )
}
