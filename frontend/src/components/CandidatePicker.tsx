import { Fragment, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useT } from '../i18n'
import { FloatingPopover, type FloatingPlacement } from './FloatingPopover'

/**
 * 通用待选列表候选项描述。
 * tile 由调用方渲染（ItemTile / 功法瓦片等视觉单元）；
 * wrapCell 可选地为单元格按钮追加悬浮详情包装（ItemTooltipTarget / EffectTooltipTarget）。
 */
export interface CandidateOption {
  /** 候选唯一键（itemId / abilityId 等，选中时经 onSelect 回传） */
  key: string
  /** 瓦片内容 */
  tile: ReactNode
  /** 可选悬浮卡片包装 */
  wrapCell?: (cell: ReactNode) => ReactNode
  /** 搜索匹配文本（名称/品类/描述等拼接，缺省不参与搜索命中） */
  searchText?: string
  /** 禁用候选（如持存为 0 的物品） */
  disabled?: boolean
  /** 无障碍标签 */
  ariaLabel?: string
}
/**
 * 待选列表顶部伴随条件操作配置
 */
export interface CandidateConditionAction {
  /** 按钮文案（如「条件」「条件2」） */
  label: ReactNode
  /** 点击回调 */
  onClick: () => void
  /** 是否已配置有效条件 */
  hasTriggers?: boolean
  /** 提示文案 */
  title?: string
  /** 无障碍说明 */
  ariaLabel?: string
  /** 是否禁用 */
  disabled?: boolean
}

/** 候选数超过该阈值时待选列表才显示搜索框（默认 12，短列表保持纯净） */
const DEFAULT_SEARCH_THRESHOLD = 12
interface CandidatePickerProps {
  /** 待选列表展开状态 */
  open: boolean
  /** 设置目标锚点元素（正在配置的槽位按钮，面板贴靠其定位） */
  anchor: HTMLElement | null
  /** 目标标题（面板顶部展示，如「术法槽 2」「头部」） */
  title: ReactNode
  /** 所有列表候选 */
  options: readonly CandidateOption[]
  /** 当前选中键（null 表示空选/卸下） */
  value: string | null
  /** 关闭回调 */
  onClose: () => void
  /** 选中候选回调（选中后自动关闭） */
  onSelect: (key: string | null) => void
  /** 是否提供空选/卸下首格（默认 true） */
  allowEmpty?: boolean
  /** 候选数超过该阈值才显示搜索框（默认 12） */
  searchThreshold?: number
  /** 浮层优选方向（默认 'top'：候选框浮于槽位上方，空间不足自动翻转） */
  placement?: FloatingPlacement
  /** 待选列表顶部伴随的触发条件大按钮配置 */
  conditionAction?: CandidateConditionAction | null
}
/**
 * 通用待选列表组件 (CandidatePicker)
 *
 * 贴靠目标槽位的浮层候选网格，取代全屏模态挑选：
 * - 设置目标：anchor 指向正在配置的槽位元素，面板经 FloatingPopover 贴靠其上方
 *   （空间不足自动翻转），点击外部/ESC 关闭，重复点击锚点槽位由调用方切换关闭；
 * - 所有列表：options 统一渲染为 auto-fill 自动判列的瓦片网格，首格可带「空选/移除」；
 * - 候选数超过 searchThreshold 时才出现搜索框；选中即回调并关闭。
 */
export function CandidatePicker({
  open,
  anchor,
  title,
  options,
  value,
  onClose,
  onSelect,
  allowEmpty = true,
  searchThreshold = DEFAULT_SEARCH_THRESHOLD,
  placement = 'top',
  conditionAction,
}: CandidatePickerProps): React.JSX.Element | null {
  const t = useT()
  const [query, setQuery] = useState('')
  const normalizedQuery = query.trim().toLocaleLowerCase('zh-CN')
  const searchable = options.length > searchThreshold

  useEffect(() => {
    if (open) setQuery('')
  }, [open])

  const visibleOptions = useMemo(() => {
    if (!open || anchor === null) return []
    if (!normalizedQuery) return options
    return options.filter(option =>
      (option.searchText ?? '').toLocaleLowerCase('zh-CN').includes(normalizedQuery))
  }, [anchor, open, options, normalizedQuery])

  if (!open || anchor === null) {
    return null
  }

  const emptyText = t('common.pickerEmpty')
  const titleText = typeof title === 'string' ? title : undefined
  const select = (key: string | null) => {
    onSelect(key)
    onClose()
  }

  return (
    <FloatingPopover
      anchorEl={anchor}
      className="candidate-picker-card"
      closeOnEsc
      content={(
        <div className="candidate-picker">
          <header className="candidate-picker-header">
            <div className="candidate-picker-header-main">
              <span className="candidate-picker-title">{title}</span>
              {searchable && (
                <input
                  aria-label={titleText ? t('market.picker.searchTitleTemplate', { title: titleText }) : undefined}
                  autoComplete="off"
                  autoFocus
                  className="candidate-picker-search"
                  onChange={event => setQuery(event.target.value)}
                  placeholder={t('market.browse.searchItems')}
                  type="search"
                  value={query}
                />
              )}
            </div>
            {conditionAction && (
              <button
                aria-label={conditionAction.ariaLabel}
                className={`candidate-picker-condition-btn${conditionAction.hasTriggers ? ' has-triggers' : ''}`}
                disabled={conditionAction.disabled}
                onClick={() => {
                  conditionAction.onClick()
                  onClose()
                }}
                title={conditionAction.title}
                type="button"
              >
                <span aria-hidden="true" className="candidate-picker-condition-icon">⚙</span>
                <span className="candidate-picker-condition-label">{conditionAction.label}</span>
              </button>
            )}
          </header>
          <div aria-label={titleText} className="candidate-picker-grid" role="listbox">
            {allowEmpty && (
              <button
                aria-selected={value === null}
                className={`item-grid-picker-card item-grid-picker-card--empty${value === null ? ' is-selected' : ''}`}
                onClick={() => select(null)}
                role="option"
                type="button"
              >
                <span className="item-grid-picker-empty-cell">
                  <strong>{emptyText}</strong>
                </span>
              </button>
            )}
            {visibleOptions.map(option => {
              const isSelected = value === option.key
              const cell = (
                <button
                  aria-label={option.ariaLabel}
                  aria-selected={isSelected}
                  className={`item-grid-picker-card${isSelected ? ' is-selected' : ''}${option.disabled ? ' is-empty' : ''}`}
                  disabled={option.disabled}
                  onClick={() => select(option.key)}
                  role="option"
                  type="button"
                >
                  {option.tile}
                </button>
              )
              const node = option.wrapCell ? option.wrapCell(cell) : cell
              return <Fragment key={option.key}>{node}</Fragment>
            })}
          </div>
          {visibleOptions.length === 0 && (
            <p className="item-grid-picker-empty">{t('market.picker.noAvailableMatch')}</p>
          )}
        </div>
      )}
      displayContents
      interactive
      onOpenChange={next => {
        if (!next) onClose()
      }}
      open={open && anchor !== null}
      placement={placement}
      size="auto"
      trigger="manual"
    >
      {null}
    </FloatingPopover>
  )
}
