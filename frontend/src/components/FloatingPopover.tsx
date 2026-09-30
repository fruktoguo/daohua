import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useId,
  useImperativeHandle,
  useLayoutEffect,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
  type RefObject,
} from 'react'
import { createPortal } from 'react-dom'

import { formatChineseSplitNumber } from '../game/format'
import { getT } from '../i18n'

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
function createQuantityAsideCard(
  quantity: number | bigint | string,
  title = getT()('common.quantity')
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
function isTouchPointerDevice(): boolean {
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
function computeSafeFloatingPosition(
  anchorRect: DOMRect,
  floatingSize: { width: number; height: number },
  placement: FloatingPlacement = 'bottom',
  options: {
    gap?: number
    padding?: number
  } = {}
): PositionResult {
  const { gap = 8, padding = 10 } = options

  const viewportWidth = typeof window !== 'undefined' ? window.innerWidth : 1920
  const viewportHeight = typeof window !== 'undefined' ? window.innerHeight : 1080

  const floatingW = floatingSize.width
  const floatingH = floatingSize.height

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
    let existing = document.getElementById('daohua-floating-root')
    if (!existing) {
      existing = document.createElement('div')
      existing.id = 'daohua-floating-root'
      existing.className = 'daohua-floating-portal-root'
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

/** 供浮层内容（如 FloatingMenuItem）读取的关闭句柄；不在浮层内渲染时返回 null */
export function useFloatingContext(): FloatingContextValue | null {
  return useContext(FloatingContext)
}

/**
 * 通用 FloatingPopover 属性定义
 */
export interface FloatingPopoverProps {
  /** 触发元素，支持 JSX 或渲染函数 (props: { isOpen, toggle }) */
  children: ReactNode | ((props: { isOpen: boolean; toggle: () => void }) => ReactNode)
  /** 浮层内容，支持 JSX 或渲染函数 (props: { close }) */
  content: ReactNode | ((props: { close: () => void }) => ReactNode)
  /** 触发方式：hover (桌面端默认带延时), click, 或 manual (受控模式) */
  trigger?: 'hover' | 'click' | 'manual'
  /** 优选悬浮方向，默认 'bottom' */
  placement?: FloatingPlacement
  /** 悬浮卡片预设标准尺寸，默认 'medium' (固定 300px 规整宽度) */
  size?: FloatingSize
  /** 自定义精确宽度 (支持 number 如 320，或 string 如 '340px') */
  width?: number | string
  /** 悬浮窗与触发器间隙，默认 8px */
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
  /** 附属气泡列表（支持多气泡同时显示组合，支持数组或惰性计算函数） */
  asideCards?: readonly FloatingAsideCard[] | (() => readonly FloatingAsideCard[])
  /** 可选关联数值：若提供，自动生成独立的数量详细副气泡（标题：数量，内容：具体数值） */
  quantity?: number | bigint | string
  /** 数量副气泡标题，默认取 i18n 词条 common.quantity */
  quantityTitle?: string
  /** 触发器右键（contextmenu）回调：供「右键直接使用」等快捷交互挂载，回调内自行 preventDefault */
  onContextMenu?: (e: React.MouseEvent) => void
  /** 外部锚点元素：提供后浮层以该元素为锚点做定位、判外点击排除与滚动跟随
      （manual 受控模式下让待选列表等浮层贴靠任意槽位元素）；缺省用自身触发器 */
  anchorEl?: HTMLElement | null
}

/**
 * 外部命令式控制句柄定义
 */
export interface FloatingPopoverHandle {
  /** 打开浮层 */
  open: () => void
  /** 关闭浮层 */
  close: () => void
  /** 切换开闭 */
  toggle: () => void
  /** 当前是否处于展开状态 */
  isOpen: boolean
}

/**
 * 通用修仙风悬浮气泡/菜单核心组件 (FloatingPopover)
 *
 * 核心机制与特性：
 * 1. 独立 Portal 挂载：通过全局专用 #daohua-floating-root 挂载到 body，避免受父级 overflow: hidden 裁剪与 z-index 层叠上下文限制；
 * 2. 视口边缘避让 (Safe Viewport Clamping)：自动进行上下/左右翻转检测并严格夹持在安全可视区域内；
 * 3. 触屏/鼠标双模适配：桌面端默认带防抖延时 hover 打开；移动端/触控设备自动切为轻量点击展示；
 * 4. 非侵入式点击穿透：支持 interactive=false (pointer-events: none) 零阻碍背景点击，以及点击外部静默被动关闭；
 * 5. 多气泡组合排布：支持主卡片 + 附属气泡群 (asideCards) 以及数量副气泡 (createQuantityAsideCard) 联动展示。
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
      onContextMenu,
      anchorEl,
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
    const repositionRafRef = useRef<number | null>(null)

    // 初始坐标先放到屏幕外：浮层挂载后由 useLayoutEffect 立即完成首次测量定位，避免在 (0,0) 闪一帧。
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

    const getEffectiveAnchorElement = useCallback((): HTMLElement | null => {
      if (anchorEl) return anchorEl
      if (!anchorRef.current) return null
      const el: HTMLElement = anchorRef.current
      if (displayContents && el.firstElementChild) {
        return el.firstElementChild as HTMLElement
      }
      const rect = el.getBoundingClientRect()
      if ((rect.width === 0 || rect.height === 0) && el.firstElementChild) {
        return el.firstElementChild as HTMLElement
      }
      return el
    }, [anchorEl, displayContents])

    // 精确重新计算并更新位置
    const updatePosition = useCallback(() => {
      if (!isOpen || !anchorRef.current || !floatingRef.current) return
      const targetAnchor = getEffectiveAnchorElement()
      if (!targetAnchor) return
      // 锚点已被 React 重挂载/移除时直接关闭：沿用游离矩形会把浮层夹到视口角隅，
      // 表现为无征兆地跳到角落。
      if (!targetAnchor.isConnected) {
        setOpen(false)
        return
      }
      const anchorRect = targetAnchor.getBoundingClientRect()
      // 浮层尺寸必须取未变换布局尺寸：入场动画 scale(0.96→1) 会污染
      // getBoundingClientRect，首次定位按缩小态算出的坐标要等下一次重测量
      // （滚动/ResizeObserver/resize 触发）才跳回正确位置——即"打开一秒后突然移动"。
      const floatingEl = floatingRef.current
      const floatingSize = { width: floatingEl.offsetWidth, height: floatingEl.offsetHeight }
      const result = computeSafeFloatingPosition(anchorRect, floatingSize, placement, {
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
    }, [gap, getEffectiveAnchorElement, isOpen, padding, placement, setOpen])

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

      // 滚动与手势处理：
      // 1. 只响应「包含锚点的滚动容器」的 scroll（文档/窗口滚动时 target=document，
      //    document.contains(anchor) 同样成立）。其他面板自滚——例如底部日志
      //    append 后自动 scrollTop 到底、侧栏滚动——与锚点无关，必须忽略，
      //    否则会误关已打开的气泡，造成类似全局刷新的联动；
      // 2. 非交互 hover 气泡（tooltip 语义）：锚点所在容器滚动即收起，等同原生
      //    title 提示。否则每个 scroll 事件都同步 getBoundingClientRect +
      //    React 重渲染整个气泡子树 + backdrop-filter 重绘背景，
      //    密集列表（坊市网格）悬停滚动必卡。滚动停稳后悬停会重新弹出；
      // 3. click / interactive 浮层跟随锚点重定位，但用 rAF 节流——
      //    一帧最多测量一次，scroll 高频事件不再逐事件重渲染。
      const handleScrollOrTouchMove = (event: Event) => {
        if (event.type === 'scroll') {
          const target = event.target
          const anchor = anchorEl ?? anchorRef.current
          if (!(target instanceof Node) || !anchor || !target.contains(anchor)) {
            return
          }
          if (trigger === 'hover' && !interactive) {
            setOpen(false)
            return
          }
        } else if (isTouchPointerDevice() && trigger === 'hover') {
          // 触屏滑动页面时平滑收起提示，防止遮挡正在滚动的视线
          setOpen(false)
          return
        }
        if (repositionRafRef.current === null) {
          repositionRafRef.current = window.requestAnimationFrame(() => {
            repositionRafRef.current = null
            updatePosition()
          })
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
        const targetAnchor = getEffectiveAnchorElement()
        if (targetAnchor) {
          resizeObserver.observe(targetAnchor)
        }
      }

      return () => {
        window.removeEventListener('resize', handleReposition)
        window.removeEventListener('scroll', handleScrollOrTouchMove, true)
        window.removeEventListener('touchmove', handleScrollOrTouchMove)
        if (repositionRafRef.current !== null) {
          window.cancelAnimationFrame(repositionRafRef.current)
          repositionRafRef.current = null
        }
        resizeObserver?.disconnect()
      }
    }, [anchorEl, isOpen, setOpen, trigger, interactive, updatePosition])

    // 处理全局点击空白处关闭 (Outside PointerDown / Click) 以及 Escape 键关闭
    // 采用非侵入式被动关闭，绝对不拦截用户对其他按钮的点击
    useEffect(() => {
      if (!isOpen) return undefined

      const handlePointerDownOutside = (event: PointerEvent | MouseEvent) => {
        const target = event.target as Node | null
        if (!target) return
        if (
          anchorRef.current?.contains(target)
          || anchorEl?.contains(target)
          || floatingRef.current?.contains(target)
        ) {
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
    }, [anchorEl, closeOnEsc, isOpen, setOpen])

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
        className={`daohua-floating-trigger ${displayContents ? 'daohua-floating-trigger--contents' : ''} ${triggerClassName}`.trim()}
        onClick={handleTriggerClick}
        onContextMenu={onContextMenu}
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

    let floatingElement: ReactNode = null

    // 仅在浮层实际展开且挂载节点存在时，才进行内容求值、附属卡片构建与 Portal 挂载（彻底懒求值）
    if (isOpen && portalRoot) {
      const resolvedAsideCards = typeof asideCards === 'function' ? asideCards() : asideCards
      const mergedAsideCards: FloatingAsideCard[] = resolvedAsideCards ? [...resolvedAsideCards] : []
      if (quantity !== undefined) {
        mergedAsideCards.unshift(createQuantityAsideCard(quantity, quantityTitle))
      }
      const hasAside = mergedAsideCards.length > 0

      const renderedContentNode =
        typeof content === 'function' ? content({ close: () => setOpen(false) }) : content

      const customCardStyle: CSSProperties = {}
      if (width !== undefined) {
        customCardStyle.width = typeof width === 'number' ? `${width}px` : width
      }

      floatingElement = createPortal(
        <FloatingContext.Provider value={{ close: () => setOpen(false) }}>
          <div
            className={`daohua-floating-layer ${hasAside ? 'daohua-floating-layer--composite' : ''} ${
              interactive ? 'daohua-floating-layer--interactive' : 'daohua-floating-layer--passthrough'
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
              className={`daohua-floating-card daohua-floating-card--main daohua-floating-card--${size} ${className}`.trim()}
              role="tooltip"
              style={customCardStyle}
            >
              {/* 四角修仙古典折角装饰 */}
              <span className="daohua-floating-corner daohua-floating-corner--tl" />
              <span className="daohua-floating-corner daohua-floating-corner--tr" />
              <span className="daohua-floating-corner daohua-floating-corner--bl" />
              <span className="daohua-floating-corner daohua-floating-corner--br" />

              {/* 顶部金华流光线 */}
              <div className="daohua-floating-shimmer-bar" />

              {/* 卡片主体内容 */}
              <div className="daohua-floating-body">{renderedContentNode}</div>
            </div>

            {/* 附属卡片气泡群（支持多气泡联动组合） */}
            {hasAside && (
              <div className="daohua-floating-aside-group">
                {mergedAsideCards.map((aside, idx) => (
                  <div
                    className={`daohua-floating-card daohua-floating-aside-card daohua-floating-aside-card--${
                      aside.tone ?? 'gold'
                    }`}
                    data-aside-id={aside.id}
                    key={aside.id ?? `${aside.title}-${idx}`}
                  >
                    <span className="daohua-floating-corner daohua-floating-corner--tl" />
                    <span className="daohua-floating-corner daohua-floating-corner--tr" />
                    <span className="daohua-floating-corner daohua-floating-corner--bl" />
                    <span className="daohua-floating-corner daohua-floating-corner--br" />

                    {aside.isSingleRow ? (
                      <div className="daohua-floating-aside-single-row">
                        <strong className="daohua-floating-aside-title">{aside.title}</strong>
                        <span className="daohua-floating-aside-val">
                          {aside.lines?.[0] ?? aside.subtitle ?? ''}
                        </span>
                      </div>
                    ) : (
                      <>
                        <div className="daohua-floating-aside-head">
                          {aside.mark && (
                            <span className="daohua-floating-aside-mark">{aside.mark}</span>
                          )}
                          <strong className="daohua-floating-aside-title">{aside.title}</strong>
                          {aside.subtitle && (
                            <span className="daohua-floating-aside-sub">{aside.subtitle}</span>
                          )}
                        </div>

                        {aside.lines && aside.lines.length > 0 && (
                          <ul className="daohua-floating-aside-lines">
                            {aside.lines.map((line, lineIdx) => (
                              <li key={lineIdx}>{line}</li>
                            ))}
                          </ul>
                        )}

                        {aside.content && (
                          <div className="daohua-floating-aside-custom">{aside.content}</div>
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
    }

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
  displayContents,
}: {
  title: ReactNode
  children: ReactNode
  placement?: FloatingPlacement
  tone?: FloatingTone
  size?: FloatingSize
  width?: number | string
  disabled?: boolean
  displayContents?: boolean
}): React.JSX.Element {
  if (!title || disabled) return <>{children}</>

  return (
    <FloatingPopover
      className={`daohua-floating-tooltip-simple daohua-floating-tooltip--${tone}`}
      closeDelay={60}
      content={<div className="daohua-floating-tooltip-text">{title}</div>}
      displayContents={displayContents}
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

/** FloatingInfoCard 卡片 Props：标题、徽记、副标、词条行、属性网格与底部操作 */
export interface FloatingInfoCardProps {
  title: ReactNode
  /** 标题左侧图标地址（物品类卡片传入物品图标，缺省时不渲染） */
  titleIcon?: string
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
  className?: string
}

/**
 * 标准修仙信息卡片气泡组件 (FloatingInfoCard)
 *
 * 具备精致标题、徽记、副标、多行属性条目、富文本及可选操作项；
 * 作为 FloatingPopover 的 content/aside 卡片统一模板使用。
 */
export function FloatingInfoCard({
  title,
  titleIcon,
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
    <div className={`daohua-floating-info-card ${className}`.trim()}>
      {/* 头部区 */}
      <header className="daohua-floating-card-header">
        <div className="daohua-floating-card-title-row">
          <span className="daohua-floating-card-title-group">
            {titleIcon && (
              <img alt="" aria-hidden="true" className="daohua-floating-card-title-icon" draggable={false} src={titleIcon} />
            )}
            <strong className="daohua-floating-card-title">{title}</strong>
          </span>
          {badge && (
            <span className={`daohua-floating-badge daohua-floating-badge--${badgeTone}`}>
              {badge}
            </span>
          )}
        </div>
        {subtitle && <div className="daohua-floating-card-subtitle">{subtitle}</div>}
      </header>

      {/* 描述段落 */}
      {description && <div className="daohua-floating-card-desc">{description}</div>}

      {/* 键值对网格 */}
      {stats && stats.length > 0 && (
        <div className="daohua-floating-card-stats">
          {stats.map((stat, idx) => (
            <div className="daohua-floating-stat-item" key={idx}>
              <span className="daohua-floating-stat-label">{stat.label}</span>
              <span
                className={`daohua-floating-stat-value daohua-floating-stat-value--${
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
        <ul className="daohua-floating-card-lines">
          {lines.map((line, idx) => (
            <li className="daohua-floating-card-line-item" key={idx}>
              {line}
            </li>
          ))}
        </ul>
      )}

      {/* 底部区 */}
      {footer && <footer className="daohua-floating-card-footer">{footer}</footer>}
    </div>
  )
}

/** 悬浮菜单单条目 Props */
export interface FloatingMenuItemProps {
  /** 条目主文案 */
  label: ReactNode
  /** 可选左侧图标 */
  icon?: ReactNode
  /** 可选右侧浅灰提示（如快捷键/余量） */
  hint?: ReactNode
  /** 修仙色调主题 */
  tone?: FloatingTone
  /** 是否禁用该条目 */
  disabled?: boolean
  /** 点击回调（触发后自动关闭所属浮层） */
  onClick?: (e: React.MouseEvent) => void
}

/**
 * 悬浮操作菜单单条目 (FloatingMenuItem)
 *
 * 点击执行回调后经 FloatingContext 自动关闭所在浮层，无需调用方手动收尾。
 */
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
      className={`daohua-floating-menu-item daohua-floating-menu-item--${tone}`}
      disabled={disabled}
      onClick={handleClick}
      type="button"
    >
      {icon && <span className="daohua-floating-menu-icon">{icon}</span>}
      <span className="daohua-floating-menu-label">{label}</span>
      {hint && <span className="daohua-floating-menu-hint">{hint}</span>}
    </button>
  )
}

/** 悬浮菜单分隔线 */
export function FloatingMenuDivider(): React.JSX.Element {
  return <div className="daohua-floating-menu-divider" />
}

/** 悬浮菜单分组容器：可选组标题 + 条目列表 */
export function FloatingMenuGroup({
  title,
  children,
}: {
  title?: ReactNode
  children: ReactNode
}): React.JSX.Element {
  return (
    <div className="daohua-floating-menu-group">
      {title && <div className="daohua-floating-menu-group-title">{title}</div>}
      <div className="daohua-floating-menu-group-items">{children}</div>
    </div>
  )
}
