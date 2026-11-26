import { CHANGELOG_ENTRIES } from '../data/changelog'
import { useT } from '../i18n'
import { ChangelogEntrySections } from './ChangelogEntrySections'
import { Icon } from './Icon'

export { ChangelogEntrySections } from './ChangelogEntrySections'

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
