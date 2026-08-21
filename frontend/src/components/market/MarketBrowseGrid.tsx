import { memo, useMemo, useState } from 'react'
import { formatNumber } from '../../game/format'
import { resolveItemGrade, resolveItemMetadata, withItemGrade, type ItemMetadata } from '../../game/ids'
import { useT, type TFunction } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import type { MarketInstrument } from './MarketOrderForm'

// 未注册物品的坊市兜底元数据：名称/品类保留服务端数据，说明文本走 market 域词典。
function getMetadata(item: MarketInstrument, t: TFunction): ItemMetadata {
  const registered = resolveItemMetadata(item.itemId)
  if (!registered) {
    const tier = Math.max(1, item.tier)
    const grade = resolveItemGrade(tier)
    return {
      label: item.name,
      glyph: item.name.slice(0, 1),
      tone: 'gold',
      category: item.category,
      rarity: grade.rarity,
      rarityLabel: grade.label,
      level: tier,
      description: t('market.itemFallback.description'),
      source: t('market.itemFallback.source'),
      usage: t('market.itemFallback.usage'),
    }
  }
  return withItemGrade(registered, item.tier > 0 ? item.tier : registered.level ?? 1)
}

interface MarketBrowseGridProps {
  instruments: readonly MarketInstrument[]
  value?: string | null
  onSelect: (itemId: string) => void
}

export const MarketBrowseGrid = memo(function MarketBrowseGrid({
  instruments,
  value,
  onSelect,
}: MarketBrowseGridProps): React.JSX.Element {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)
  const [query, setQuery] = useState('')
  const [category, setCategory] = useState<string | null>(null)
  const categories = useMemo(
    () => [...new Set(instruments.map(item => item.category))].sort((left, right) => left.localeCompare(right, 'zh-CN')),
    [instruments],
  )
  const normalizedQuery = query.trim().toLocaleLowerCase('zh-CN')
  const visibleItems = useMemo(() => instruments.filter(item => {
    if (category !== null && item.category !== category) return false
    if (!normalizedQuery) return true
    return `${item.name} ${item.category}`.toLocaleLowerCase('zh-CN').includes(normalizedQuery)
  }), [category, instruments, normalizedQuery])

  return (
    <div className="market-browse">
      <div className="market-browse-toolbar">
        <div className="market-category-chips" role="group" aria-label={t('market.browse.filterCategoryAria')}>
          <button
            aria-pressed={category === null}
            className={category === null ? 'active' : ''}
            onClick={() => setCategory(null)}
            type="button"
          >{t('market.browse.categoryAll')}</button>
          {categories.map(name => (
            <button
              aria-pressed={category === name}
              className={category === name ? 'active' : ''}
              key={name}
              onClick={() => setCategory(current => current === name ? null : name)}
              type="button"
            >{name}</button>
          ))}
        </div>
        <input
          aria-label={t('market.browse.searchItems')}
          autoComplete="off"
          onChange={event => setQuery(event.target.value)}
          placeholder={t('market.browse.searchItems')}
          type="search"
          value={query}
        />
      </div>
      <div className="market-browse-grid" role="listbox" aria-label={t('market.browse.gridAria')}>
        {visibleItems.map(item => {
          const metadata = getMetadata(item, t)
          const amount = inventory.find(entry => entry.itemId === item.itemId)?.amount ?? 0
          const selected = value === item.itemId
          return (
            <ItemTooltipTarget
              amount={amount}
              className="market-browse-tooltip"
              definition={item.definition}
              itemId={item.itemId}
              key={item.itemId}
              metadata={metadata}
              tabIndex={-1}
            >
              <button
                aria-label={t('market.browse.itemOwned', { name: item.name, count: formatNumber(amount) })}
                aria-selected={selected}
                className={`market-browse-card${selected ? ' is-selected' : ''}`}
                onClick={() => onSelect(item.itemId)}
                role="option"
                type="button"
              >
                <ItemTile
                  amount={amount}
                  category={item.category}
                  className="inventory-cell"
                  level={metadata.level}
                  name={item.name}
                  rarity={metadata.rarity}
                />
              </button>
            </ItemTooltipTarget>
          )
        })}
      </div>
      {visibleItems.length === 0 && <p className="market-browse-empty">{t('market.browse.noMatch')}</p>}
    </div>
  )
})
