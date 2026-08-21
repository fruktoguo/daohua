import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useId,
  useImperativeHandle,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
  type RefObject,
} from 'react'
import { createPortal } from 'react-dom'

import { formatChineseSplitNumber } from '../game/format'

/**
 * 悬浮位置偏好
 */
export type FloatingPlacement =
  | 'top'
  | 'top-start'
  | 'top-end'
  | 'bottom'
  | 'bottom-start'
  | 'bottom-end'
  | 'left'
  | 'left-start'
  | 'left-end'
  | 'right'
  | 'right-start'
  | 'right-end'
  | 'auto'

/**
 * 气泡与菜单的修仙视觉色调
 */
export type FloatingTone = 'gold' | 'jade' | 'qi' | 'danger' | 'purple' | 'muted'

/**
 * 气泡标准预设尺寸
 * - small: 240px (紧凑型，适合属性/简述)
 * - medium: 300px (标准型，适合装备/物品/技能/根基，视觉工整统一)
 * - large: 380px (宽阔型，适合战报/复杂对比/多列数据)
 * - auto: 自适应宽度 (根据内容自动收缩展开)
 */
export type FloatingSize = 'small' | 'medium' | 'large' | 'auto'

/**
 * 附属卡片定义（支持多气泡组合）
 */
export interface FloatingAsideCard {
  id?: string
  mark?: string
  title: string
  subtitle?: string
  tone?: FloatingTone
  lines?: readonly string[]
  content?: ReactNode
  /** 是否以单行模式展示 (左侧标题，右侧具体数值) */
  isSingleRow?: boolean
}

/**
 * 通用数量详细副气泡构建工具
 * 标题：数量（或自定义）
 * 内容：具体数值（中文四位分段）
 * 结构：1 行展示
 */
export function createQuantityAsideCard(
  quantity: number | bigint | string,
  title = '数量'
): FloatingAsideCard {
  return {
    id: 'quantity-aside-card',
    title,
    tone: 'gold',
    isSingleRow: true,
    lines: [formatChineseSplitNumber(quantity)],
  }
}

/**
 * 检查当前是否偏好触屏/点击交互（移动端或无 hover 能力的设备）
 */
export function isTouchPointerDevice(): boolean {
  if (typeof window === 'undefined') return false
  if (typeof window.matchMedia === 'function') {
    return window.matchMedia('(pointer: coarse), (hover: none)').matches
  }
  return 'ontouchstart' in window || navigator.maxTouchPoints > 0
}

/**
 * 视口边界与位置计算结果
 */
interface PositionResult {
  left: number
  top: number
  actualPlacement: FloatingPlacement
}

/**
 * 计算浮层在视口内的安全坐标，绝对避免被裁剪、遮挡或超出屏幕
 */
export function computeSafeFloatingPosition(
  anchorRect: DOMRect,
  floatingRect: DOMRect,
  placement: FloatingPlacement = 'bottom',
  options: {
    gap?: number
    padding?: number
  } = {}
): PositionResult {
  const { gap = 8, padding = 10 } = options

  const viewportWidth = typeof window !== 'undefined' ? window.innerWidth : 1920
  const viewportHeight = typeof window !== 'undefined' ? window.innerHeight : 1080

  const floatingW = floatingRect.width
  const floatingH = floatingRect.height

  // 1. 判断主要方向
  let primary: 'top' | 'bottom' | 'left' | 'right' = 'bottom'
  let align: 'start' | 'center' | 'end' = 'center'

  if (placement === 'auto') {
    const spaceBelow = viewportHeight - anchorRect.bottom
    const spaceAbove = anchorRect.top
    primary = spaceBelow >= floatingH + gap || spaceBelow >= spaceAbove ? 'bottom' : 'top'
  } else {
    const parts = placement.split('-')
    primary = parts[0] as 'top' | 'bottom' | 'left' | 'right'
    if (parts[1] === 'start') align = 'start'
    else if (parts[1] === 'end') align = 'end'
    else align = 'center'
  }

  // 2. 上下翻转检测
  if (primary === 'bottom') {
    const spaceBelow = viewportHeight - anchorRect.bottom
    const spaceAbove = anchorRect.top
    if (spaceBelow < floatingH + gap && spaceAbove > spaceBelow) {
      primary = 'top'
    }
  } else if (primary === 'top') {
    const spaceAbove = anchorRect.top
    const spaceBelow = viewportHeight - anchorRect.bottom
    if (spaceAbove < floatingH + gap && spaceBelow > spaceAbove) {
      primary = 'bottom'
    }
  }

  // 3. 左右翻转检测
  if (primary === 'right') {
    const spaceRight = viewportWidth - anchorRect.right
    const spaceLeft = anchorRect.left
    if (spaceRight < floatingW + gap && spaceLeft > spaceRight) {
      primary = 'left'
    }
  } else if (primary === 'left') {
    const spaceLeft = anchorRect.left
    const spaceRight = viewportWidth - anchorRect.right
    if (spaceLeft < floatingW + gap && spaceRight > spaceLeft) {
      primary = 'right'
    }
  }

  let rawLeft = 0
  let rawTop = 0

  // 4. 根据决定的主方向和对齐方式计算初始坐标
  if (primary === 'top' || primary === 'bottom') {
    rawTop = primary === 'top' ? anchorRect.top - floatingH - gap : anchorRect.bottom + gap

    if (align === 'start') {
      rawLeft = anchorRect.left
    } else if (align === 'end') {
      rawLeft = anchorRect.right - floatingW
    } else {
      rawLeft = anchorRect.left + (anchorRect.width - floatingW) / 2
    }
  } else {
    rawLeft = primary === 'left' ? anchorRect.left - floatingW - gap : anchorRect.right + gap

    if (align === 'start') {
      rawTop = anchorRect.top
    } else if (align === 'end') {
      rawTop = anchorRect.bottom - floatingH
    } else {
      rawTop = anchorRect.top + (anchorRect.height - floatingH) / 2
    }
  }

  // 5. 严格夹持在可视区域内 (Clamp to Viewport Safe Bounds)
  const maxSafeLeft = Math.max(padding, viewportWidth - floatingW - padding)
  const maxSafeTop = Math.max(padding, viewportHeight - floatingH - padding)

  const safeLeft = Math.min(Math.max(padding, rawLeft), maxSafeLeft)
  const safeTop = Math.min(Math.max(padding, rawTop), maxSafeTop)

  const actualPlacement: FloatingPlacement = align === 'center' ? primary : (`${primary}-${align}` as FloatingPlacement)

  return {
    left: Math.round(safeLeft),
    top: Math.round(safeTop),
    actualPlacement,
  }
}

/**
 * 获取或创建专用的全局 Floating Portal 容器，确保 z-index 并在任何界面顶层
 */
let floatingPortalRoot: HTMLElement | null = null

function getOrCreateFloatingPortalRoot(): HTMLElement {
  if (typeof document === 'undefined') return null as unknown as HTMLElement
  if (!floatingPortalRoot || !floatingPortalRoot.isConnected) {
    let existing = document.getElementById('xiuxian-floating-root')
    if (!existing) {
      existing = document.createElement('div')
      existing.id = 'xiuxian-floating-root'
      existing.className = 'xiuxian-floating-portal-root'
      document.body.appendChild(existing)
    }
    floatingPortalRoot = existing
  }
  return floatingPortalRoot
}

// 上下文：支持嵌套浮层与关闭传递
interface FloatingContextValue {
  close: () => void
}

const FloatingContext = createContext<FloatingContextValue | null>(null)

export function useFloatingContext(): FloatingContextValue | null {
  return useContext(FloatingContext)
}

/**
 * 通用 FloatingPopover 属性
 */
export interface FloatingPopoverProps {
  /** 触发元素，支持 JSX 或渲染函数 */
  children: ReactNode | ((props: { isOpen: boolean; toggle: () => void }) => ReactNode)
  /** 浮层内容，支持 JSX 或渲染函数 */
  content: ReactNode | ((props: { close: () => void }) => ReactNode)
  /** 触发方式：hover (桌面端默认带延时), click, 或 manual (受控) */
  trigger?: 'hover' | 'click' | 'manual'
  /** 优选悬浮方向，默认 'bottom' */
  placement?: FloatingPlacement
  /** 悬浮卡片预设标准尺寸，默认 'medium' (固定 300px 规整宽度) */
  size?: FloatingSize
  /** 自定义精确宽度 (支持 number 如 320，或 string 如 '340px') */
  width?: number | string
  /** 悬浮窗间隙，默认 8px */
  gap?: number
  /** 视口内安全边距，默认 10px */
  padding?: number
  /** hover 展开延时 (ms)，默认 80ms */
  openDelay?: number
  /** hover 关闭延时 (ms)，默认 120ms */
  closeDelay?: number
  /** 浮层额外 CSS 类名 */
  className?: string
  /** 触发器包裹层额外 CSS 类名 */
  triggerClassName?: string
  /** 触发器标签类型，默认 'span' */
  triggerAs?: 'span' | 'div' | 'button'
  /** 是否启用 display: contents，使包裹层对父级 flex/grid 布局完全透明 */
  displayContents?: boolean
  /** 浮层是否支持内部交互（为 false 时 pointer-events: none，完全穿透，0 影响其他任何 UI 操作） */
  interactive?: boolean
  /** 是否阻止触发器点击事件冒泡，默认 false（绝不吞掉业务点击事件） */
  stopClickPropagation?: boolean
  /** 是否禁用 */
  disabled?: boolean
  /** 受控状态：是否展开 */
  open?: boolean
  /** 受控状态变化回调 */
  onOpenChange?: (open: boolean) => void
  /** 是否在点击浮层内部时自动关闭，默认 false */
  closeOnContentClick?: boolean
  /** 是否在按 Escape 时关闭，默认 true */
  closeOnEsc?: boolean
  /** 自定义样式 */
  style?: CSSProperties
  /** tabIndex */
  tabIndex?: number
  /** 附属气泡列表（支持多气泡同时显示组合） */
  asideCards?: readonly FloatingAsideCard[]
  /** 可选关联数值：若提供，自动生成独立的数量详细副气泡（标题：数量，内容：具体数值） */
  quantity?: number | bigint | string
  /** 数量副气泡标题，默认 '数量' */
  quantityTitle?: string
}

export interface FloatingPopoverHandle {
  open: () => void
  close: () => void
  toggle: () => void
  isOpen: boolean
}

/**
 * 通用的修仙风悬浮气泡/菜单组件
 */
export const FloatingPopover = React.forwardRef<FloatingPopoverHandle, FloatingPopoverProps>(
  function FloatingPopover(
    {
      children,
      content,
      trigger = 'hover',
      placement = 'bottom',
      size = 'medium',
      width,
      gap = 8,
      padding = 10,
      openDelay = 80,
      closeDelay = 120,
      className = '',
      triggerClassName = '',
      triggerAs = 'span',
      displayContents = false,
      interactive = true,
      stopClickPropagation = false,
      disabled = false,
      open: controlledOpen,
      onOpenChange,
      closeOnContentClick = false,
      closeOnEsc = true,
      style,
      tabIndex,
      asideCards,
      quantity,
      quantityTitle,
    },
    ref
  ) {
    const isControlled = controlledOpen !== undefined
    const [internalOpen, setInternalOpen] = useState(false)
    const isOpen = isControlled ? controlledOpen : internalOpen

    const id = useId()
    const anchorRef = useRef<HTMLElement>(null)
    const floatingRef = useRef<HTMLDivElement>(null)
    const openTimerRef = useRef<number | null>(null)
    const closeTimerRef = useRef<number | null>(null)

    const [pos, setPos] = useState<PositionResult>({
      left: -9999,
      top: -9999,
      actualPlacement: placement,
    })

    const setOpen = useCallback(
      (nextOpen: boolean) => {
        if (disabled) return
        if (openTimerRef.current) {
          window.clearTimeout(openTimerRef.current)
          openTimerRef.current = null
        }
        if (closeTimerRef.current) {
          window.clearTimeout(closeTimerRef.current)
          closeTimerRef.current = null
        }
        if (!isControlled) {
          setInternalOpen(nextOpen)
        }
        onOpenChange?.(nextOpen)
      },
      [disabled, isControlled, onOpenChange]
    )

    const handleOpen = useCallback(() => {
      if (disabled) return
      if (closeTimerRef.current) {
        window.clearTimeout(closeTimerRef.current)
        closeTimerRef.current = null
      }
      if (openDelay <= 0) {
        setOpen(true)
      } else {
        openTimerRef.current = window.setTimeout(() => {
          setOpen(true)
        }, openDelay)
      }
    }, [disabled, openDelay, setOpen])

    const handleClose = useCallback(() => {
      if (openTimerRef.current) {
        window.clearTimeout(openTimerRef.current)
        openTimerRef.current = null
      }
      if (closeDelay <= 0) {
        setOpen(false)
      } else {
        closeTimerRef.current = window.setTimeout(() => {
          setOpen(false)
        }, closeDelay)
      }
    }, [closeDelay, setOpen])

    const handleToggle = useCallback(() => {
      if (disabled) return
      if (isOpen) {
        setOpen(false)
      } else {
        setOpen(true)
      }
    }, [disabled, isOpen, setOpen])

    useImperativeHandle(
      ref,
      () => ({
        open: () => setOpen(true),
        close: () => setOpen(false),
        toggle: handleToggle,
        isOpen,
      }),
      [handleToggle, isOpen, setOpen]
    )

    // 精确重新计算并更新位置
    const updatePosition = useCallback(() => {
      if (!isOpen || !anchorRef.current || !floatingRef.current) return
      const anchorRect = anchorRef.current.getBoundingClientRect()
      const floatingRect = floatingRef.current.getBoundingClientRect()
      const result = computeSafeFloatingPosition(anchorRect, floatingRect, placement, {
        gap,
        padding,
      })
      setPos((prev) => {
        if (
          prev.left === result.left &&
          prev.top === result.top &&
          prev.actualPlacement === result.actualPlacement
        ) {
          return prev
        }
        return result
      })
    }, [gap, isOpen, padding, placement])

    useLayoutEffect(() => {
      if (isOpen) {
        updatePosition()
      }
    }, [isOpen, updatePosition])

    // 监听视口变化、滚动、手势滑动和浮层大小变动
    useEffect(() => {
      if (!isOpen) return undefined

      const handleReposition = () => {
        updatePosition()
      }

      // 触屏滑动页面时平滑收起提示，防止遮挡正在滚动的视线
      const handleScrollOrTouchMove = () => {
        if (isTouchPointerDevice() && trigger === 'hover') {
          setOpen(false)
        } else {
          updatePosition()
        }
      }

      window.addEventListener('resize', handleReposition)
      window.addEventListener('scroll', handleScrollOrTouchMove, true)
      window.addEventListener('touchmove', handleScrollOrTouchMove, { passive: true })

      let resizeObserver: ResizeObserver | null = null
      if (typeof ResizeObserver !== 'undefined' && floatingRef.current) {
        resizeObserver = new ResizeObserver(() => {
          updatePosition()
        })
        resizeObserver.observe(floatingRef.current)
        if (anchorRef.current) {
          resizeObserver.observe(anchorRef.current)
        }
      }

      return () => {
        window.removeEventListener('resize', handleReposition)
        window.removeEventListener('scroll', handleScrollOrTouchMove, true)
        window.removeEventListener('touchmove', handleScrollOrTouchMove)
        resizeObserver?.disconnect()
      }
    }, [isOpen, setOpen, trigger, updatePosition])

    // 处理全局点击空白处关闭 (Outside PointerDown / Click) 以及 Escape 键关闭
    // 采用非侵入式被动关闭，绝对不拦截用户对其他按钮的点击
    useEffect(() => {
      if (!isOpen) return undefined

      const handlePointerDownOutside = (event: PointerEvent | MouseEvent) => {
        const target = event.target as Node | null
        if (!target) return
        if (anchorRef.current?.contains(target) || floatingRef.current?.contains(target)) {
          return
        }
        // 静默关闭气泡，不阻止任何原生事件传递
        setOpen(false)
      }

      const handleKeyDown = (event: KeyboardEvent) => {
        if (closeOnEsc && event.key === 'Escape') {
          event.preventDefault()
          setOpen(false)
        }
      }

      document.addEventListener('pointerdown', handlePointerDownOutside, true)
      document.addEventListener('keydown', handleKeyDown, true)

      return () => {
        document.removeEventListener('pointerdown', handlePointerDownOutside, true)
        document.removeEventListener('keydown', handleKeyDown, true)
      }
    }, [closeOnEsc, isOpen, setOpen])

    // 清理定时器
    useEffect(() => {
      return () => {
        if (openTimerRef.current) window.clearTimeout(openTimerRef.current)
        if (closeTimerRef.current) window.clearTimeout(closeTimerRef.current)
      }
    }, [])

    // 交互事件处理：智能兼容 Touch / Mouse，绝不破坏业务点击
    const handleTriggerMouseEnter = () => {
      if (trigger === 'hover' && !isTouchPointerDevice()) {
        handleOpen()
      }
    }

    const handleTriggerMouseLeave = () => {
      if (trigger === 'hover' && !isTouchPointerDevice()) {
        handleClose()
      }
    }

    const handleTriggerClick = (e: React.MouseEvent) => {
      if (disabled) return
      if (stopClickPropagation) {
        e.stopPropagation()
      }
      if (trigger === 'click') {
        handleToggle()
      } else if (isTouchPointerDevice()) {
        // 触屏模式下，如果触发器没有被其他更深层按钮处理，做轻量展示切换，不阻止默认动作与冒泡
        handleToggle()
      }
    }

    const handleFloatingMouseEnter = () => {
      if (interactive && trigger === 'hover' && !isTouchPointerDevice()) {
        if (closeTimerRef.current) {
          window.clearTimeout(closeTimerRef.current)
          closeTimerRef.current = null
        }
      }
    }

    const handleFloatingMouseLeave = () => {
      if (interactive && trigger === 'hover' && !isTouchPointerDevice()) {
        handleClose()
      }
    }

    const handleFloatingContentClick = (e: React.MouseEvent) => {
      if (closeOnContentClick) {
        setOpen(false)
      }
      if (interactive) {
        e.stopPropagation()
      }
    }

    const TriggerTag = triggerAs as 'span'

    const triggerElement = (
      <TriggerTag
        aria-controls={isOpen ? id : undefined}
        aria-expanded={isOpen}
        aria-haspopup="dialog"
        className={`xiuxian-floating-trigger ${displayContents ? 'xiuxian-floating-trigger--contents' : ''} ${triggerClassName}`.trim()}
        onClick={handleTriggerClick}
        onMouseEnter={handleTriggerMouseEnter}
        onMouseLeave={handleTriggerMouseLeave}
        ref={anchorRef as RefObject<HTMLSpanElement>}
        tabIndex={tabIndex}
      >
        {typeof children === 'function'
          ? children({ isOpen, toggle: handleToggle })
          : children}
      </TriggerTag>
    )

    const portalRoot = typeof document !== 'undefined' ? getOrCreateFloatingPortalRoot() : null

    // 组合多气泡渲染 (主气泡 + 附属气泡群，自动集成 quantity 独立数量副气泡)
    const mergedAsideCards = useMemo<FloatingAsideCard[]>(() => {
      const list: FloatingAsideCard[] = asideCards ? [...asideCards] : []
      if (quantity !== undefined) {
        list.unshift(createQuantityAsideCard(quantity, quantityTitle ?? '数量'))
      }
      return list
    }, [asideCards, quantity, quantityTitle])

    const hasAside = mergedAsideCards.length > 0

    const renderedContentNode =
      typeof content === 'function' ? content({ close: () => setOpen(false) }) : content

    const customCardStyle: CSSProperties = {}
    if (width !== undefined) {
      customCardStyle.width = typeof width === 'number' ? `${width}px` : width
    }

    const floatingElement =
      isOpen && portalRoot
        ? createPortal(
            <FloatingContext.Provider value={{ close: () => setOpen(false) }}>
              <div
                className={`xiuxian-floating-layer ${hasAside ? 'xiuxian-floating-layer--composite' : ''} ${
                  interactive ? 'xiuxian-floating-layer--interactive' : 'xiuxian-floating-layer--passthrough'
                }`}
                data-placement={pos.actualPlacement}
                id={id}
                onClick={handleFloatingContentClick}
                onMouseEnter={handleFloatingMouseEnter}
                onMouseLeave={handleFloatingMouseLeave}
                ref={floatingRef}
                style={{
                  position: 'fixed',
                  left: `${pos.left}px`,
                  top: `${pos.top}px`,
                  zIndex: 10000,
                  ...style,
                }}
              >
                {/* 主气泡面板 */}
                <div
                  className={`xiuxian-floating-card xiuxian-floating-card--main xiuxian-floating-card--${size} ${className}`.trim()}
                  role="tooltip"
                  style={customCardStyle}
                >
                  {/* 四角修仙古典折角装饰 */}
                  <span className="xiuxian-floating-corner xiuxian-floating-corner--tl" />
                  <span className="xiuxian-floating-corner xiuxian-floating-corner--tr" />
                  <span className="xiuxian-floating-corner xiuxian-floating-corner--bl" />
                  <span className="xiuxian-floating-corner xiuxian-floating-corner--br" />

                  {/* 顶部金华流光线 */}
                  <div className="xiuxian-floating-shimmer-bar" />

                  {/* 卡片主体内容 */}
                  <div className="xiuxian-floating-body">{renderedContentNode}</div>
                </div>

                {/* 附属卡片气泡群（支持多气泡联动组合） */}
                {hasAside && (
                  <div className="xiuxian-floating-aside-group">
                    {mergedAsideCards.map((aside, idx) => (
                      <div
                        className={`xiuxian-floating-card xiuxian-floating-aside-card xiuxian-floating-aside-card--${
                          aside.tone ?? 'gold'
                        }`}
                        data-aside-id={aside.id}
                        key={aside.id ?? `${aside.title}-${idx}`}
                      >
                        <span className="xiuxian-floating-corner xiuxian-floating-corner--tl" />
                        <span className="xiuxian-floating-corner xiuxian-floating-corner--tr" />
                        <span className="xiuxian-floating-corner xiuxian-floating-corner--bl" />
                        <span className="xiuxian-floating-corner xiuxian-floating-corner--br" />

                        {aside.isSingleRow ? (
                          <div className="xiuxian-floating-aside-single-row">
                            <strong className="xiuxian-floating-aside-title">{aside.title}</strong>
                            <span className="xiuxian-floating-aside-val">
                              {aside.lines?.[0] ?? aside.subtitle ?? ''}
                            </span>
                          </div>
                        ) : (
                          <>
                            <div className="xiuxian-floating-aside-head">
                              {aside.mark && (
                                <span className="xiuxian-floating-aside-mark">{aside.mark}</span>
                              )}
                              <strong className="xiuxian-floating-aside-title">{aside.title}</strong>
                              {aside.subtitle && (
                                <span className="xiuxian-floating-aside-sub">{aside.subtitle}</span>
                              )}
                            </div>

                            {aside.lines && aside.lines.length > 0 && (
                              <ul className="xiuxian-floating-aside-lines">
                                {aside.lines.map((line, lineIdx) => (
                                  <li key={lineIdx}>{line}</li>
                                ))}
                              </ul>
                            )}

                            {aside.content && (
                              <div className="xiuxian-floating-aside-custom">{aside.content}</div>
                            )}
                          </>
                        )}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </FloatingContext.Provider>,
            portalRoot
          )
        : null

    return (
      <>
        {triggerElement}
        {floatingElement}
      </>
    )
  }
)

/**
 * 标准轻量 Tooltip 组件（用于简单文本提示）
 * 默认 interactive=false，pointer-events: none，鼠标划过与点击绝对穿透，不影响任何其他 UI 交互
 */
export function FloatingTooltip({
  title,
  children,
  placement = 'top',
  tone = 'gold',
  size = 'auto',
  width,
  disabled,
}: {
  title: ReactNode
  children: ReactNode
  placement?: FloatingPlacement
  tone?: FloatingTone
  size?: FloatingSize
  width?: number | string
  disabled?: boolean
}): React.JSX.Element {
  if (!title || disabled) return <>{children}</>

  return (
    <FloatingPopover
      className={`xiuxian-floating-tooltip-simple xiuxian-floating-tooltip--${tone}`}
      closeDelay={60}
      content={<div className="xiuxian-floating-tooltip-text">{title}</div>}
      gap={6}
      interactive={false}
      openDelay={100}
      placement={placement}
      size={size}
      trigger="hover"
      width={width}
    >
      {children}
    </FloatingPopover>
  )
}

/**
 * 标准修仙信息卡片气泡组件 (FloatingInfoCard)
 * 具备精致标题、徽记、副标、多行属性条目、富文本及可选操作项
 */
export interface FloatingInfoCardProps {
  title: ReactNode
  badge?: ReactNode
  badgeTone?: FloatingTone
  subtitle?: ReactNode
  description?: ReactNode
  /** 词条 / 属性行 */
  lines?: readonly (string | ReactNode)[]
  /** 键值对网格，如 攻击 +120 / 品阶 极品 */
  stats?: readonly { label: string; value: ReactNode; tone?: FloatingTone }[]
  /** 底部操作按钮或说明 */
  footer?: ReactNode
  /** 附属气泡群（如装备词缀气泡、套装效果气泡等） */
  asideCards?: readonly FloatingAsideCard[]
  className?: string
}

export function FloatingInfoCard({
  title,
  badge,
  badgeTone = 'gold',
  subtitle,
  description,
  lines,
  stats,
  footer,
  className = '',
}: FloatingInfoCardProps): React.JSX.Element {
  return (
    <div className={`xiuxian-floating-info-card ${className}`.trim()}>
      {/* 头部区 */}
      <header className="xiuxian-floating-card-header">
        <div className="xiuxian-floating-card-title-row">
          <strong className="xiuxian-floating-card-title">{title}</strong>
          {badge && (
            <span className={`xiuxian-floating-badge xiuxian-floating-badge--${badgeTone}`}>
              {badge}
            </span>
          )}
        </div>
        {subtitle && <div className="xiuxian-floating-card-subtitle">{subtitle}</div>}
      </header>

      {/* 描述段落 */}
      {description && <div className="xiuxian-floating-card-desc">{description}</div>}

      {/* 键值对网格 */}
      {stats && stats.length > 0 && (
        <div className="xiuxian-floating-card-stats">
          {stats.map((stat, idx) => (
            <div className="xiuxian-floating-stat-item" key={idx}>
              <span className="xiuxian-floating-stat-label">{stat.label}</span>
              <span
                className={`xiuxian-floating-stat-value xiuxian-floating-stat-value--${
                  stat.tone ?? 'gold'
                }`}
              >
                {stat.value}
              </span>
            </div>
          ))}
        </div>
      )}

      {/* 词条条目列表 */}
      {lines && lines.length > 0 && (
        <ul className="xiuxian-floating-card-lines">
          {lines.map((line, idx) => (
            <li className="xiuxian-floating-card-line-item" key={idx}>
              {line}
            </li>
          ))}
        </ul>
      )}

      {/* 底部区 */}
      {footer && <footer className="xiuxian-floating-card-footer">{footer}</footer>}
    </div>
  )
}

/**
 * 悬浮操作菜单组件 (FloatingMenu)
 */
export interface FloatingMenuItemProps {
  label: ReactNode
  icon?: ReactNode
  hint?: ReactNode
  tone?: FloatingTone
  disabled?: boolean
  onClick?: (e: React.MouseEvent) => void
}

export function FloatingMenuItem({
  label,
  icon,
  hint,
  tone = 'gold',
  disabled = false,
  onClick,
}: FloatingMenuItemProps): React.JSX.Element {
  const context = useFloatingContext()

  const handleClick = (e: React.MouseEvent) => {
    if (disabled) return
    onClick?.(e)
    context?.close()
  }

  return (
    <button
      className={`xiuxian-floating-menu-item xiuxian-floating-menu-item--${tone}`}
      disabled={disabled}
      onClick={handleClick}
      type="button"
    >
      {icon && <span className="xiuxian-floating-menu-icon">{icon}</span>}
      <span className="xiuxian-floating-menu-label">{label}</span>
      {hint && <span className="xiuxian-floating-menu-hint">{hint}</span>}
    </button>
  )
}

export function FloatingMenuDivider(): React.JSX.Element {
  return <div className="xiuxian-floating-menu-divider" />
}

export function FloatingMenuGroup({
  title,
  children,
}: {
  title?: ReactNode
  children: ReactNode
}): React.JSX.Element {
  return (
    <div className="xiuxian-floating-menu-group">
      {title && <div className="xiuxian-floating-menu-group-title">{title}</div>}
      <div className="xiuxian-floating-menu-group-items">{children}</div>
    </div>
  )
}
