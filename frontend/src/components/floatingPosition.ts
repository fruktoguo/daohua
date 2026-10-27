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
 * 悬浮间距配置：支持单一数值，或针对上下左右各方向的独立间隙
 */
export type FloatingGap = number | { top?: number; bottom?: number; left?: number; right?: number }

/**
 * 视口边界与位置计算结果（含自适应缩放比例）
 */
export interface PositionResult {
  left: number
  top: number
  actualPlacement: FloatingPlacement
  scale: number
}

/**
 * 视口安全坐标与缩放计算参数
 */
export interface ComputeSafeFloatingPositionOptions {
  gap?: FloatingGap
  padding?: number
  /** 最小缩放比例下限，默认 0.5（即最多缩小至 50%） */
  minScale?: number
  /** 自定义视口尺寸（默认取 window.innerWidth/innerHeight，单测或特定容器可覆盖） */
  viewport?: { width: number; height: number }
}

/**
 * 计算浮层在视口内的安全坐标与自适应等比缩放比例，绝对避免被裁剪、遮挡或超出屏幕
 * 当浮层（尤其是竖屏多卡片纵向堆叠）自然高度超出视口可用高度时，按比例缩小（最多缩小到 50%）
 */
export function computeSafeFloatingPosition(
  anchorRect: { left: number; top: number; right: number; bottom: number; width: number; height: number },
  floatingSize: { width: number; height: number },
  placement: FloatingPlacement = 'bottom',
  options: ComputeSafeFloatingPositionOptions = {}
): PositionResult {
  const { gap = 8, padding = 10, minScale = 0.5, viewport } = options
  const gapObj = typeof gap === 'number'
    ? { top: gap, bottom: gap, left: gap, right: gap }
    : { top: 8, bottom: 8, left: 8, right: 8, ...gap }

  const viewportWidth = viewport?.width ?? (typeof window !== 'undefined' ? window.innerWidth : 1920)
  const viewportHeight = viewport?.height ?? (typeof window !== 'undefined' ? window.innerHeight : 1080)

  const floatingW = floatingSize.width
  const floatingH = floatingSize.height

  // 0. 视口可用空间与自适应等比缩放解算
  // 确保至少保留安全边距，可用空间过小时保底 100px
  const availableWidth = Math.max(100, viewportWidth - padding * 2)
  const availableHeight = Math.max(100, viewportHeight - padding * 2)

  // 当自然尺寸超出视口可用尺寸时计算所需缩放比例（竖屏多卡片主要受限高度，宽大面板亦兼顾宽度）
  const scaleH = floatingH > availableHeight ? availableHeight / floatingH : 1
  const scaleW = floatingW > availableWidth ? availableWidth / floatingW : 1
  const neededScale = Math.min(scaleH, scaleW)
  const clampedScale = Math.max(minScale, Math.min(1, neededScale))
  // 消除微小浮点误差，若 >= 0.995 则规整为 1，其余保留 3 位小数
  const scale = clampedScale >= 0.995 ? 1 : Math.round(clampedScale * 1000) / 1000

  // 物理有效缩放尺寸（在 transform-origin: top left 下对应渲染包围盒）
  const scaledW = floatingW * scale
  const scaledH = floatingH * scale

  // 1. 判断主要方向
  let primary: 'top' | 'bottom' | 'left' | 'right' = 'bottom'
  let align: 'start' | 'center' | 'end' = 'center'

  if (placement === 'auto') {
    const spaceBelow = viewportHeight - anchorRect.bottom
    const spaceAbove = anchorRect.top
    primary = spaceBelow >= scaledH + gapObj.bottom || spaceBelow >= spaceAbove ? 'bottom' : 'top'
  } else {
    const parts = placement.split('-')
    primary = parts[0] as 'top' | 'bottom' | 'left' | 'right'
    if (parts[1] === 'start') align = 'start'
    else if (parts[1] === 'end') align = 'end'
    else align = 'center'
  }

  // 2. 上下翻转检测（基于缩放后的物理高度）
  if (primary === 'bottom') {
    const spaceBelow = viewportHeight - anchorRect.bottom
    const spaceAbove = anchorRect.top
    if (spaceBelow < scaledH + gapObj.bottom && spaceAbove > spaceBelow) {
      primary = 'top'
    }
  } else if (primary === 'top') {
    const spaceAbove = anchorRect.top
    const spaceBelow = viewportHeight - anchorRect.bottom
    if (spaceAbove < scaledH + gapObj.top && spaceBelow > spaceAbove) {
      primary = 'bottom'
    }
  }

  // 3. 左右翻转检测（基于缩放后的物理宽度）
  if (primary === 'right') {
    const spaceRight = viewportWidth - anchorRect.right
    const spaceLeft = anchorRect.left
    if (spaceRight < scaledW + gapObj.right && spaceLeft > spaceRight) {
      primary = 'left'
    }
  } else if (primary === 'left') {
    const spaceLeft = anchorRect.left
    const spaceRight = viewportWidth - anchorRect.right
    if (spaceLeft < scaledW + gapObj.left && spaceRight > spaceLeft) {
      primary = 'right'
    }
  }

  let rawLeft = 0
  let rawTop = 0

  // 4. 根据决定的主方向和对齐方式计算初始坐标（基于缩放后的尺寸物理对齐）
  if (primary === 'top' || primary === 'bottom') {
    const currentGap = primary === 'top' ? gapObj.top : gapObj.bottom
    rawTop = primary === 'top' ? anchorRect.top - scaledH - currentGap : anchorRect.bottom + currentGap

    if (align === 'start') {
      rawLeft = anchorRect.left
    } else if (align === 'end') {
      rawLeft = anchorRect.right - scaledW
    } else {
      rawLeft = anchorRect.left + (anchorRect.width - scaledW) / 2
    }
  } else {
    const currentGap = primary === 'left' ? gapObj.left : gapObj.right
    rawLeft = primary === 'left' ? anchorRect.left - scaledW - currentGap : anchorRect.right + currentGap

    if (align === 'start') {
      rawTop = anchorRect.top
    } else if (align === 'end') {
      rawTop = anchorRect.bottom - scaledH
    } else {
      rawTop = anchorRect.top + (anchorRect.height - scaledH) / 2
    }
  }

  // 5. 严格夹持在可视区域内 (Clamp to Viewport Safe Bounds，使用缩放物理边界)
  const maxSafeLeft = Math.max(padding, viewportWidth - scaledW - padding)
  const maxSafeTop = Math.max(padding, viewportHeight - scaledH - padding)

  const safeLeft = Math.min(Math.max(padding, rawLeft), maxSafeLeft)
  const safeTop = Math.min(Math.max(padding, rawTop), maxSafeTop)

  const actualPlacement: FloatingPlacement = align === 'center' ? primary : (`${primary}-${align}` as FloatingPlacement)

  return {
    left: Math.round(safeLeft),
    top: Math.round(safeTop),
    actualPlacement,
    scale,
  }
}
