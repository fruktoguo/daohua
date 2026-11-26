import { memo } from 'react'
import type { ChangelogCategory, ChangelogEntry } from '../data/changelog'
import { useT } from '../i18n'
import { RichTemplateText } from './EntityReference'

const CATEGORY_CLASS_MAP: Record<ChangelogCategory, string> = {
  feature: 'changelog-cat-badge--feature',
  balance: 'changelog-cat-badge--balance',
  fix: 'changelog-cat-badge--fix',
  optimize: 'changelog-cat-badge--optimize',
}

/**
 * 单期更新日志分类段落渲染组件，供更新日志全量页与首次公告弹窗共用，
 * 独立拆出以保证 ChangelogPage 可被动态分包按需加载，不拖入首屏主包。
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
