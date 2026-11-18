import { CARD_ASPECT, isRed } from './doudizhuArt'

interface Point { x: number; y: number }
interface Rect { left: number; top: number; width: number; height: number }
interface FlightFace { card: number; svg: SVGSVGElement }

const SVG_NS = 'http://www.w3.org/2000/svg'
function symbolArt(id: string, className: string, viewBox: string): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, 'svg')
  svg.setAttribute('class', className)
  svg.setAttribute('viewBox', viewBox)
  const use = document.createElementNS(SVG_NS, 'use')
  use.setAttribute('href', `#${id}`)
  svg.append(use)
  return svg
}

function createCardBack(): HTMLElement {
  const back = document.createElement('i')
  back.className = 'ddz-card-back'
  return back
}

/** 只操作独立效果层，不持有或推演牌局状态；组件卸载时统一取消所有动画与预约。 */
export class DouDiZhuMotion {
  private animations = new Set<Animation>()
  private timers = new Set<number>()
  private arena: HTMLElement
  private layer: HTMLElement
  private onArrival: () => void
  constructor(arena: HTMLElement, layer: HTMLElement, onArrival: () => void) {
    this.arena = arena; this.layer = layer; this.onArrival = onArrival
  }

  schedule(at: number, fn: (elapsed: number) => void): void {
    const timer = window.setTimeout(() => { this.timers.delete(timer); fn(Math.max(0, Date.now() - at)) }, Math.max(0, at - Date.now()))
    this.timers.add(timer)
  }
  animate(el: HTMLElement, frames: Keyframe[], duration: number, done: () => void = () => el.remove(), elapsed = 0): void {
    const animation = el.animate(frames, { duration, fill: 'both', easing: 'linear' })
    animation.currentTime = Math.min(duration, elapsed)
    this.animations.add(animation)
    animation.onfinish = () => { this.animations.delete(animation); done(); animation.cancel() }
  }
  dispose(): void {
    this.timers.forEach(timer => window.clearTimeout(timer)); this.timers.clear()
    this.animations.forEach(animation => animation.cancel()); this.animations.clear()
    this.layer.replaceChildren()
  }
  private point(seat: number): Point {
    const origin = this.layer.getBoundingClientRect(), avatar = this.arena.querySelector<HTMLElement>(`[data-seat="${seat}"] .ddz-avatar`)!
    const r = avatar.getBoundingClientRect()
    return { x: r.left + r.width / 2 - origin.left, y: r.top + r.height * .46 - origin.top }
  }
  shuffle(stack: HTMLElement, startedAt: number): void {
    const elapsed = Date.now() - startedAt
    if (elapsed >= 2600) {
      stack.replaceChildren(...Array.from({ length: 4 }, createCardBack))
      return
    }
    stack.replaceChildren()
    for (let i = 0; i < 24; i++) {
      const el = createCardBack(), side = i % 2 ? 1 : -1, depth = i / 24
      el.style.zIndex = String(i)
      const square = `translate(${depth * 3}px,${-depth * 6}px) rotate(0deg)`
      el.style.transform = square; stack.append(el)
      this.animate(el, [
        { offset: 0, transform: square, opacity: 0 }, { offset: .08, transform: square, opacity: 1 },
        { offset: .24, transform: `translate(${side * 47}px,${-12 - depth * 8}px) rotate(${side * 13}deg)`, opacity: 1 },
        { offset: .38, transform: `translate(${side * 40}px,${-18 - depth * 16}px) rotate(${side * 9}deg)`, opacity: 1 },
        { offset: .45 + Math.floor(i / 2) * .014, transform: `translate(${side * 9}px,${-depth * 10}px) rotate(${side * 2}deg)`, opacity: 1 },
        { offset: .73, transform: `translate(${(depth - .5) * 25}px,${-28 * Math.sin(depth * Math.PI) - 4}px) rotate(${(depth - .5) * 35}deg) rotateX(18deg)`, opacity: 1 },
        { offset: .87, transform: `translate(${depth * 4}px,${-depth * 7}px) rotate(1deg)`, opacity: 1 },
        { offset: 1, transform: square, opacity: 1 },
      ], 2600, () => { el.style.transform = square }, elapsed)
    }
  }
  /** 从牌堆或底牌飞到实际牌槽；自己的牌在空中翻开，对手只展示背面。 */
  fly(seat: number, face: FlightFace | null, source: Rect, to: Rect, duration: number, elapsed = 0, alreadyFaceUp = false): void {
    if (elapsed >= duration) return
    const origin = this.layer.getBoundingClientRect(), scaleX = origin.width / this.layer.clientWidth, scaleY = origin.height / this.layer.clientHeight
    const w = (face === null ? Math.max(40, to.width) : to.width) / scaleX, h = w * CARD_ASPECT
    const from = { x: (source.left + source.width / 2 - origin.left) / scaleX, y: (source.top + source.height / 2 - origin.top) / scaleY }
    const end = { x: (to.left + to.width / 2 - origin.left) / scaleX, y: (to.top + to.height / 2 - origin.top) / scaleY }
    const control = { x: (from.x + end.x) / 2, y: Math.min(from.y, end.y) - 60 }
    const el = document.createElement('div'); el.className = 'ddz-card-flight'
    el.style.cssText = `width:${w}px;height:${h}px;--w:${w}px;--h:${h}px`
    // 未知牌不生成正面，任何动画节点都不会泄露对手手牌。
    if (face !== null) {
      const front = document.createElement('span')
      front.className = `ddz-card ddz-flight-front${isRed(face.card) ? ' ddz-red' : ''}${face.card >= 52 ? ' ddz-joker' : ''}`
      front.append(face.svg.cloneNode(true)); el.append(front)
    }
    const back = createCardBack(); back.classList.add('ddz-flight-back'); el.append(back); this.layer.append(el)
    const frames = Array.from({ length: 21 }, (_, i) => {
      const t = i / 20, p = 1 - (1 - t) ** 2, q = 1 - p, flip = Math.min(1, Math.max(0, (t - .24) / .64))
      const x = q * q * from.x + 2 * q * p * control.x + p * p * end.x
      const y = q * q * from.y + 2 * q * p * control.y + p * p * end.y
      const angle = alreadyFaceUp ? face !== null ? 0 : 180 * flip : face !== null ? 180 * (1 - flip) : 180
      return { offset: t, transform: `translate(${x - w / 2}px,${y - h / 2}px) perspective(700px) rotateZ(${Math.sin(Math.PI * t) * (seat === 1 ? -12 : 12)}deg) rotateY(${angle}deg) scale(${(source.width + (to.width - source.width) * p) / scaleX / w})` }
    })
    this.animate(el, frames, duration, () => { this.onArrival(); el.remove() }, elapsed)
  }
  bubble(seat: number, text: string): void {
    this.layer.querySelector(`[data-speaker="${seat}"]`)?.remove()
    const point = this.point(seat), el = document.createElement('div')
    el.className = 'ddz-social-bubble'; el.dataset.speaker = String(seat); el.textContent = text; this.layer.append(el)
    el.style.left = `${Math.max(8, Math.min(this.layer.clientWidth - el.offsetWidth - 8, point.x - el.offsetWidth / 2))}px`
    el.style.top = `${Math.max(8, point.y - el.offsetHeight - 55)}px`
    this.animate(el, [{ offset: 0, opacity: 0, transform: 'translateY(9px) scale(.85)' }, { offset: .07, opacity: 1, transform: 'scale(1.035)' }, { offset: .12, opacity: 1, transform: 'scale(1)' }, { offset: .88, opacity: 1, transform: 'scale(1)' }, { offset: 1, opacity: 0, transform: 'translateY(-6px) scale(.96)' }], 3200)
  }
  prop(sender: number, target: number, kind: string, elapsed: number): void {
    if (elapsed >= 820) { if (elapsed < 1600) this.impact(target, kind); return }
    const from = this.point(sender), to = this.point(target), el = document.createElement('div'), shadow = document.createElement('i')
    el.className = 'ddz-social-projectile'; el.append(symbolArt(`ddz-prop-${kind}`, 'ddz-prop-art', '0 0 64 64'))
    shadow.className = 'ddz-social-shadow'; this.layer.append(shadow, el)
    const size = el.offsetWidth, frames: Keyframe[] = [], shadows: Keyframe[] = []
    for (let i = 0; i <= 24; i++) {
      const t = i / 24, p = t * t * (3 - 2 * t), arc = Math.sin(Math.PI * p) * Math.min(145, Math.abs(from.y - to.y) * .3 + 70)
      const x = from.x + (to.x - from.x) * p, y = from.y + (to.y - from.y) * p
      frames.push({ offset: t, transform: `translate(${x - size / 2}px,${y - size / 2 - arc}px) rotate(${p * (kind === 'tomato' ? 640 : 85)}deg) scale(${.72 + Math.sin(Math.PI * p) * .3})`, opacity: Math.min(1, t / .08) })
      shadows.push({ offset: t, transform: `translate(${x - 16}px,${y + 18}px) scale(${1 - Math.sin(Math.PI * p) * .48})`, opacity: .6 - Math.sin(Math.PI * p) * .3 })
    }
    this.animate(shadow, shadows, 820, () => shadow.remove(), elapsed)
    this.animate(el, frames, 820, () => { el.remove(); this.impact(target, kind) }, elapsed)
  }
  private impact(seat: number, kind: string): void {
    const p = this.point(seat), el = document.createElement('div')
    el.className = 'ddz-social-impact'; el.style.left = `${p.x - 40}px`; el.style.top = `${p.y - 40}px`
    el.append(kind === 'tomato'
      ? symbolArt('ddz-impact-tomato', 'ddz-impact-svg', '0 0 100 100')
      : symbolArt(`ddz-prop-${kind}`, 'ddz-prop-art', '0 0 64 64'))
    this.layer.append(el)
    this.animate(el, [{ offset: 0, transform: 'scale(.3) rotate(-8deg)', opacity: 0 }, { offset: .09, transform: 'scale(1.15) rotate(5deg)', opacity: 1 }, { offset: .17, transform: 'scale(1)', opacity: 1 }, { offset: .65, transform: 'translateY(2px)', opacity: .9 }, { offset: 1, transform: `translateY(${kind === 'tomato' ? 34 : -24}px) scale(1.1)`, opacity: 0 }], 1600)
    const actor = this.arena.querySelector<HTMLElement>(`[data-seat="${seat}"] .ddz-avatar-row`)!
    this.animate(actor, [{ transform: 'rotate(0)' }, { offset: .24, transform: 'rotate(-5deg)' }, { offset: .52, transform: 'rotate(4deg)' }, { transform: 'rotate(0)' }], 420, () => {})
    for (let i = 0; i < 12; i++) {
      const particle = document.createElement('i'), angle = i / 12 * Math.PI * 2, distance = 25 + Math.random() * 40
      particle.className = kind === 'applause' ? 'ddz-social-spark' : 'ddz-social-particle'
      particle.style.setProperty('--particle-color', kind === 'tomato' ? '#d56546' : kind === 'flower' ? '#e49ab1' : '#dfd6a0')
      this.layer.append(particle)
      const x = p.x + Math.cos(angle) * distance, y = p.y + Math.sin(angle) * distance
      this.animate(particle, [{ transform: `translate(${p.x}px,${p.y}px) scale(.3)`, opacity: 1 }, { offset: .32, transform: `translate(${x}px,${y}px) rotate(${i * 28}deg)`, opacity: .9 }, { transform: `translate(${x}px,${y + (kind === 'tea' ? -45 : 50)}px) rotate(${i * 53}deg) scale(.3)`, opacity: 0 }], 850 + i * 35)
    }
  }
}
