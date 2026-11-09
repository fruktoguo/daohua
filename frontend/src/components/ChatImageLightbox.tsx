import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useT } from '../i18n'

/**
 * 聊天图片灯箱 (ChatImageLightbox)
 *
 * 传音消息内图片左键单击/触屏点按后弹出的全屏查看层，用于放大查看图片细节：
 * 1. PC：滚轮以光标为锚缩放（1x~8x）、按住拖拽平移、双击在 1x 与 2x 间切换、Esc/点击遮罩/关闭按钮退出；
 * 2. 移动端：点按图片打开，双指捏合缩放、单指平移、双击切换、点按遮罩关闭；
 * 3. 缩放与平移统一钳位，图片不会飞出视口；打开时锁定页面滚动并锁定焦点语义。
 */
export interface ChatImageLightboxProps {
  /** 是否打开 */
  open: boolean
  /** 图片地址（调用方传入已解析的缓存直链） */
  src: string
  /** 无障碍替代文本 */
  alt?: string
  /** 关闭回调 */
  onClose: () => void
}

/** 缩放倍率上下限 */
const MIN_SCALE = 1
const MAX_SCALE = 8
/** 双击切换的目标倍率 */
const DOUBLE_TAP_SCALE = 2
/** 双击判定间隔（毫秒） */
const DOUBLE_TAP_WINDOW = 300

/** 缩放/平移状态（平移以视口中心为原点） */
interface TransformState {
  scale: number
  x: number
  y: number
}

const INITIAL_TRANSFORM: TransformState = { scale: 1, x: 0, y: 0 }

/** 钳位数值到 [min, max]（缩放与平移多处共用） */
function clamp(value: number, min: number, max: number): number {
  return value < min ? min : value > max ? max : value
}

/**
 * 钳位平移量：保证缩放后的图片边界始终覆盖视口，不露出遮罩盲区。
 * 以视口中心为原点：允许平移 = (viewport/2) * (scale - 1)。
 */
function clampTranslation(value: number, scale: number, viewport: number): number {
  const overflow = (viewport / 2) * (scale - 1)
  return clamp(value, -overflow, overflow)
}

/**
 * 传音图片灯箱组件（全屏 Portal）
 */
export function ChatImageLightbox({ open, src, alt, onClose }: ChatImageLightboxProps): React.JSX.Element | null {
  const t = useT()
  const [transform, setTransform] = useState<TransformState>(INITIAL_TRANSFORM)
  // 活跃 pointer 状态：单指 = 拖拽/双击判定，双指 = 捏合
  const pointersRef = useRef(new Map<number, { x: number; y: number }>())
  // 捏合基线：初始两指距离、基线 transform、两指中点（以中点为缩放锚）
  const pinchRef = useRef<{ dist: number; transform: TransformState; midX: number; midY: number } | null>(null)
  // 单指拖拽基线
  const dragRef = useRef<{ pointerX: number; pointerY: number; transform: TransformState } | null>(null)
  // 双击判定：上次 tap 时间与坐标
  const lastTapRef = useRef<{ time: number; x: number; y: number } | null>(null)
  const onCloseRef = useRef(onClose)
  useEffect(() => {
    onCloseRef.current = onClose
  }, [onClose])

  // 打开时重置变换、锁定页面滚动、注册 Esc 监听
  useEffect(() => {
    if (!open) return undefined
    setTransform(INITIAL_TRANSFORM)
    pointersRef.current.clear()
    pinchRef.current = null
    dragRef.current = null
    lastTapRef.current = null
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const handleKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        event.preventDefault()
        onCloseRef.current()
      }
    }
    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      document.body.style.overflow = previousOverflow
    }
  }, [open])

  if (!open) return null

  /** 以指定视口锚点（clientX/Y）缩放到 targetScale，并同步钳位平移 */
  const zoomAt = (targetScale: number, anchorX: number, anchorY: number): void => {
    setTransform(prev => {
      const scale = clamp(targetScale, MIN_SCALE, MAX_SCALE)
      const w = window.innerWidth
      const h = window.innerHeight
      // 视口中心坐标（与平移原点一致）
      const cx = anchorX - w / 2
      const cy = anchorY - h / 2
      // 保持锚点下图像内容不动：新平移 = 锚点相对中心偏移 × (1 - scale/prevScale) + prev平移 × (scale/prevScale)
      const ratio = scale / prev.scale
      const x = clampTranslation((prev.x - cx) * ratio + cx, scale, w)
      const y = clampTranslation((prev.y - cy) * ratio + cy, scale, h)
      return { scale, x, y }
    })
  }

  /** 双击/双触切换 1x 与 2x（以点击位置为锚） */
  const handleDoubleTap = (clientX: number, clientY: number): void => {
    if (transform.scale > 1.01) {
      zoomAt(1, clientX, clientY)
    } else {
      zoomAt(DOUBLE_TAP_SCALE, clientX, clientY)
    }
  }

  /** pointer 按下：登记指针并初始化拖拽/捏合基线 */
  const handlePointerDown = (e: React.PointerEvent<HTMLDivElement>): void => {
    e.currentTarget.setPointerCapture(e.pointerId)
    pointersRef.current.set(e.pointerId, { x: e.clientX, y: e.clientY })
    if (pointersRef.current.size === 1) {
      dragRef.current = { pointerX: e.clientX, pointerY: e.clientY, transform }
    } else if (pointersRef.current.size === 2) {
      dragRef.current = null
      const [a, b] = [...pointersRef.current.values()]
      pinchRef.current = {
        dist: Math.hypot(a.x - b.x, a.y - b.y),
        transform,
        midX: (a.x + b.x) / 2,
        midY: (a.y + b.y) / 2,
      }
    }
  }

  /** pointer 移动：单指平移 / 双指捏合缩放（以两指中点为锚） */
  const handlePointerMove = (e: React.PointerEvent<HTMLDivElement>): void => {
    const pointers = pointersRef.current
    if (!pointers.has(e.pointerId)) return
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY })
    if (pointers.size === 2 && pinchRef.current) {
      const [a, b] = [...pointers.values()]
      const dist = Math.hypot(a.x - b.x, a.y - b.y)
      const base = pinchRef.current
      if (base.dist > 0) {
        zoomAt(base.transform.scale * (dist / base.dist), base.midX, base.midY)
      }
    } else if (pointers.size === 1 && dragRef.current) {
      const base = dragRef.current
      const dx = e.clientX - base.pointerX
      const dy = e.clientY - base.pointerY
      setTransform(prev => ({
        scale: prev.scale,
        x: clampTranslation(base.transform.x + dx, prev.scale, window.innerWidth),
        y: clampTranslation(base.transform.y + dy, prev.scale, window.innerHeight),
      }))
    }
  }

  /** pointer 抬起/取消：注销指针并清空对应手势基线；无位移单击关闭（拖拽/捏合不误关） */
  const handlePointerUp = (e: React.PointerEvent<HTMLDivElement>): void => {
    const pointers = pointersRef.current
    const wasMulti = pointers.size >= 2
    const downPoint = dragRef.current
    pointers.delete(e.pointerId)
    if (pointers.size < 2) pinchRef.current = null
    if (pointers.size < 1) {
      dragRef.current = null
      // 拖拽过（位移 >8px）不视为点击
      const isClick = downPoint !== null
        && Math.hypot(e.clientX - downPoint.pointerX, e.clientY - downPoint.pointerY) <= 8
      // 多指手势结束后残留的这次 up 不参与双击判定
      if (!wasMulti && isClick) {
        const now = Date.now()
        const last = lastTapRef.current
        if (last && now - last.time <= DOUBLE_TAP_WINDOW
          && Math.hypot(e.clientX - last.x, e.clientY - last.y) <= 24) {
          lastTapRef.current = null
          handleDoubleTap(e.clientX, e.clientY)
        } else {
          lastTapRef.current = { time: now, x: e.clientX, y: e.clientY }
          // 无双击配对的无位移单击：关闭灯箱（延迟到双击窗口结束后执行，避免双击时先关）
          window.setTimeout(() => {
            if (lastTapRef.current !== null) {
              lastTapRef.current = null
              onCloseRef.current()
            }
          }, DOUBLE_TAP_WINDOW)
        }
      }
    }
  }

  /** PC 滚轮：以光标为锚缩放 */
  const handleWheel = (e: React.WheelEvent<HTMLDivElement>): void => {
    e.preventDefault()
    const factor = e.deltaY < 0 ? 1.2 : 1 / 1.2
    zoomAt(transform.scale * factor, e.clientX, e.clientY)
  }

  return createPortal(
    <div
      className="chat-image-lightbox-backdrop"
      onWheel={handleWheel}
    >
      <div
        aria-label={alt ?? t('combat.chat.stickerAlt')}
        aria-modal="true"
        className="chat-image-lightbox-stage"
        onPointerCancel={handlePointerUp}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        role="dialog"
      >
        <img
          alt={alt ?? t('combat.chat.stickerAlt')}
          className="chat-image-lightbox-image"
          draggable={false}
          src={src}
          style={{ transform: `translate(${transform.x}px, ${transform.y}px) scale(${transform.scale})` }}
        />
        <button
          aria-label={t('common.close')}
          className="chat-image-lightbox-close"
          onClick={onClose}
          title={t('common.close')}
          type="button"
        >
          ×
        </button>
      </div>
    </div>,
    document.body
  )
}
