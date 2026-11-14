import { memo } from 'react'
import { CHANGELOG_ENTRIES, type ChangelogCategory, type ChangelogEntry } from '../data/changelog'
import { useT } from '../i18n'
import { RichTemplateText } from './EntityReference'
import { Icon } from './Icon'

const CATEGORY_CLASS_MAP: Record<ChangelogCategory, string> = {
  feature: 'changelog-cat-badge--feature',
  balance: 'changelog-cat-badge--balance',
  fix: 'changelog-cat-badge--fix',
  optimize: 'changelog-cat-badge--optimize',
}

/**
 * 单期更新日志分类段落渲染组件，供更新日志全量页与首次公告弹窗共用，
 * 避免分类徽章及富文本条目列表出现双份重复结构。
 */
export const ChangelogEntrySections = memo(function ChangelogEntrySections({
  entry,
  onNavigate,
}: {
  entry: ChangelogEntry
  onNavigate?: () => void
}): React.JSX.Element {
  const t = useT()

  return (
    <div className="changelog-sections">
      {entry.sections.map(section => (
        <div className="changelog-section" key={`${entry.date}-${section.category}`}>
          <div className="changelog-section-header">
            <span className={`changelog-cat-badge ${CATEGORY_CLASS_MAP[section.category]}`}>
              {t(`app.changelog.categories.${section.category}`)}
            </span>
          </div>
          <ul className="changelog-bullet-list">
            {section.items.map((item, idx) => (
              <li className="changelog-bullet-item" key={idx}>
                <RichTemplateText onNavigate={onNavigate} template={item} />
              </li>
            ))}
          </ul>
        </div>
      ))}
    </div>
  )
})

/**
 * 「更新日志」独立页面（归属于侧边栏「其他」分组及竖屏「更多」分组）。
 * 倒序呈现近期开发者玩家向更新日志，文内所有特质、功法、物品、装备、建筑、妖兽与行动词条均可悬浮展示图鉴卡片。
 */
export function ChangelogPage(): React.JSX.Element {
  const t = useT()

  return (
    <section className="panel changelog-page" aria-label={t('app.changelog.title')}>
      <header className="changelog-page-header">
        <div className="changelog-title-group">
          <h2 className="changelog-title">
            <Icon name="changelog" size={18} />
            <span>{t('app.changelog.title')}</span>
          </h2>
          <p className="changelog-subtitle">{t('app.changelog.subtitle')}</p>
        </div>
        <span className="changelog-count-badge">
          {t('app.changelog.entryCountBadge', { count: CHANGELOG_ENTRIES.length })}
        </span>
      </header>

      <div className="changelog-hover-banner">
        <Icon name="codex" size={15} />
        <span>{t('app.changelog.hoverHint')}</span>
      </div>

      {CHANGELOG_ENTRIES.length === 0 ? (
        <div className="changelog-empty">{t('app.changelog.empty')}</div>
      ) : (
        <div className="changelog-list">
          {CHANGELOG_ENTRIES.map(entry => (
            <article className="changelog-card" key={entry.date}>
              <header className="changelog-card-header">
                <div className="changelog-card-meta">
                  <span className="changelog-date-pill">{entry.date}</span>
                  <span className="changelog-version-pill">{entry.versionLabel}</span>
                  {entry.noticeId && (
                    <span className="changelog-notice-pill">{t('app.changelog.noticeBadge')}</span>
                  )}
                </div>
                <h3 className="changelog-card-title">{entry.title}</h3>
                {entry.summary && <p className="changelog-card-summary">{entry.summary}</p>}
              </header>
              <ChangelogEntrySections entry={entry} />
            </article>
          ))}
        </div>
      )}
    </section>
  )
}
