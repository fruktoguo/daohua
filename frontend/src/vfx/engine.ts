/**
 * 道化太初 · 战斗特效引擎 (VfxEngine)
 *
 * 职责：
 * - 维护特效覆盖层坐标系（以 .vfx-stage-overlay 为原点，全部节点绝对定位，零回流）；
 * - 提供粒子/弹道/光束/冲击环/持续发射器/路径拖尾等通用原语；
 * - 注册表 register(abilityId, handler)：每个功法一个独立模块文件，
 *   handler(ctx) 内仅负责视觉演出——结算、跳字、受击、出手动作全部由
 *   服务端快照驱动的既有真实组件（ArenaResource / ArenaActor）承担；
 * - injectStyles(id, css)：技能模块自含专属关键帧，按 id 去重注入；
 * - dispose()：组件卸载时清理全部特效节点与计时器。
 *
 * 移植自 docs/design/战斗演武画廊/vfx/core.js（已 headless 自检验证）。
 */
import type { CombatAbilityDefinition } from '../game/types'

/** 粒子喷发参数 */
export interface VfxParticleOptions {
  /** 粒子元素类名（粒子基元 vfx-p-* 或技能专属类） */
  cls?: string
  /** 粒子数量 */
  count?: number
  /** 源点散落半径（px，在源点周围随机偏移） */
  spread?: number
  /** 粒子尺寸范围 [min,max] px */
  size?: [number, number]
  /** 粒子寿命范围 [min,max] ms */
  dur?: [number, number]
  /** 飞行距离范围 [min,max] px */
  dist?: [number, number]
  /** 主方向（弧度），缺省全向 */
  angle?: number
  /** 扇形弧度（围绕 angle 的张角） */
  arc?: number
  /** 向上偏置（px，正数抬升） */
  rise?: number
  /** 方向性粒子（火花条/光针）：--rot 对齐飞行方向沿长轴飞出 */
  directional?: boolean
  /** 自旋角变量 --spin */
  spin?: boolean
  /** 向心收束：粒子在外圈 spawnRadius 半径取点，沿径向飞向圆心（聚气/汇聚演出） */
  inward?: boolean
  /** 向心模式的外圈半径（px） */
  spawnRadius?: number
  /** 向心轨迹垂直压扁比（<1 呈贴地椭圆） */
  squash?: number
  /** 粒子主色相（写入宿主 --c，供粒子基元/专属类继承） */
  c?: string
  /** 粒子副色相（写入宿主 --c2） */
  c2?: string
}

/** 持续发射器参数（interval 间隔喷一小簇，duration 后停止） */
export interface VfxEmitOptions extends VfxParticleOptions {
  interval?: number
  duration?: number
  perBurst?: number
}

/** 路径拖尾参数（沿线段播撒粒子，垂直方向飞散） */
export interface VfxTrailOptions {
  cls?: string
  count?: number
  /** 垂直路径的随机抖动幅度 px */
  jitter?: number
  /** 粒子向法线两侧飞散距离 px */
  scatter?: number
  size?: [number, number]
  dur?: [number, number]
}

/** 弹道投射物附加参数 */
export interface VfxProjectileOptions {
  /** 上抛弧度偏移 px */
  lift?: number
  easing?: string
}

/** 技能演出运行时上下文：由桥接层在出手时刻解析好，技能模块只消费 */
export interface VfxPlayContext {
  /** 出手方所属侧（目标解析的阵营基准） */
  side: 'ally' | 'enemy'
  /** 出手方 .arena-actor 元素（可能已离场为 null，模块内自行判空） */
  casterEl: HTMLElement | null
  /** 目标 .arena-actor 元素列表（按 ability.targetType 解析） */
  targetEls: readonly HTMLElement[]
  /** 功法配表定义（未知 abilityId 时为 null） */
  ability?: CombatAbilityDefinition | null
  /** 本次吟唱时长 ms（供法阵等前摇特效对齐节奏） */
  castMs?: number
  /** 注册延时句柄（dispose 时统一清理） */
  after: (ms: number, fn: () => void) => void
}

/**
 * 画廊共享特效脚本的 ctx 契约（docs/design/战斗演武画廊 单一真源）：
 * 脚本内部使用 {caster:{el}, targets:[{el}], castMs, after, impact/heal/...} 形状；
 * 前端播放时由 toGalleryContext 适配——结算类回调全部为 noop（真实结算由
 * 服务端快照驱动的既有组件承担，特效纯表现层）。
 */
interface GalleryActorRef {
  el: HTMLElement | null
  side: 'ally' | 'enemy'
}

interface GalleryVfxContext {
  caster: GalleryActorRef
  targets: GalleryActorRef[]
  castMs?: number
  ability?: CombatAbilityDefinition | null
  after: (ms: number, fn: () => void) => void
  /** 以下结算回调在正式前端均为 noop（沙箱语义，仅供脚本调用不生效） */
  impact: () => void
  heal: () => void
  threat: () => void
  buffApply: () => void
  kill: () => void
  regen: () => void
  reviveActor: () => void
  manaDelta: () => void
}

/** 前端 ctx → 画廊脚本 ctx 适配 */
function toGalleryContext(ctx: VfxPlayContext): GalleryVfxContext {
  const noop = (): void => {}
  return {
    caster: { el: ctx.casterEl, side: ctx.side },
    targets: ctx.targetEls.map(el => ({ el, side: ctx.side === 'ally' ? 'enemy' as const : 'ally' as const })),
    castMs: ctx.castMs,
    ability: ctx.ability ?? null,
    after: ctx.after,
    impact: noop,
    heal: noop,
    threat: noop,
    buffApply: noop,
    kill: noop,
    regen: noop,
    reviveActor: noop,
    manaDelta: noop,
  }
}

/** 技能演出 handler：收画廊形状 ctx（与共享脚本契约一致），返回演出总时长 ms（可选） */
export type VfxHandler = (ctx: GalleryVfxContext) => number | void

/** 共享技能脚本队列条目形态：`window.DaohuaVFXModules` 元素 */
export type VfxModuleRegister = (vfx: VfxEngine) => void

const INJECTED_STYLES = new Set<string>()

/** 技能模块专属样式注入（按 id 全局去重，模块加载即调用） */
export function injectVfxStyles(id: string, cssText: string): void {
  if (INJECTED_STYLES.has(id)) return
  INJECTED_STYLES.add(id)
  const style = document.createElement('style')
  style.dataset.vfx = id
  style.textContent = cssText
  document.head.appendChild(style)
}

interface Point {
  x: number
  y: number
}

/**
 * 战斗特效播放引擎（表现层，不参与结算）。
 * 向覆盖层挂载特效节点并托管其生命周期；画廊共享脚本经 register 注册为
 * 同名 handler，结算类方法在前端为 noop。dispose 时统一清理节点与计时器。
 */
export class VfxEngine {
  private readonly shellEl: HTMLElement
  private readonly overlayEl: HTMLElement
  private readonly registry = new Map<string, VfxHandler>()
  private readonly liveNodes = new Set<HTMLElement>()
  private readonly timers = new Set<ReturnType<typeof setTimeout>>()
  private disposed = false

  /**
   * @param shellEl .combat-arena-shell 元素；overlay 由 React 渲染传入
   * @param overlayEl .vfx-stage-overlay 元素
   */
  constructor(shellEl: HTMLElement, overlayEl: HTMLElement) {
    this.shellEl = shellEl
    this.overlayEl = overlayEl
  }

  /** 元素中心点在覆盖层坐标系中的位置 */
  center(el: Element | null | undefined): Point {
    if (!el) return { x: 0, y: 0 }
    const o = this.overlayEl.getBoundingClientRect()
    const r = el.getBoundingClientRect()
    return { x: r.left - o.left + r.width / 2, y: r.top - o.top + r.height / 2 }
  }

  /** 元素矩形在覆盖层坐标系中的位置 */
  rect(el: Element | null | undefined): { left: number; top: number; width: number; height: number } {
    if (!el) return { left: 0, top: 0, width: 0, height: 0 }
    const o = this.overlayEl.getBoundingClientRect()
    const r = el.getBoundingClientRect()
    return { left: r.left - o.left, top: r.top - o.top, width: r.width, height: r.height }
  }

  /** 在覆盖层 (x, y) 处生成特效宿主节点（宽高 0 锚点，子元素自行展开） */
  node(cls: string | null, x: number, y: number): HTMLElement {
    const el = document.createElement('div')
    el.className = 'vfx-node' + (cls ? ' ' + cls : '')
    el.style.left = x + 'px'
    el.style.top = y + 'px'
    this.overlayEl.appendChild(el)
    this.liveNodes.add(el)
    return el
  }

  /** 生成子元素 */
  child(parent: HTMLElement, cls: string, tag = 'div'): HTMLElement {
    const el = document.createElement(tag)
    el.className = cls
    parent.appendChild(el)
    return el
  }

  /** 注册延时句柄（dispose 时统一清理） */
  after(ms: number, fn: () => void): ReturnType<typeof setTimeout> {
    const t = setTimeout(() => {
      this.timers.delete(t)
      fn()
    }, ms)
    this.timers.add(t)
    return t
  }

  /** 节点生命周期：ms 后移除 */
  retire(el: HTMLElement, ms: number): void {
    this.after(ms, () => {
      el.remove()
      this.liveNodes.delete(el)
    })
  }

  /** 一次性特效：创建宿主节点 + 一个子元素 + 到期自动清理（杜绝匿名宿主泄漏） */
  spawn(cls: string, x: number, y: number, dur: number): HTMLElement {
    const host = this.node(null, x, y)
    const c = this.child(host, cls)
    this.retire(host, (dur || 600) + 200)
    return c
  }

  /**
   * 通用粒子喷发：以 (x, y) 为源点生成 count 个粒子。
   * 粒子 CSS 消费 --dx --dy --dur（--rot 方向性）变量。
   */
  particles(x: number, y: number, opts: VfxParticleOptions): HTMLElement {
    const host = this.node(null, x, y)
    if (opts.c) host.style.setProperty('--c', opts.c)
    if (opts.c2) host.style.setProperty('--c2', opts.c2)
    const count = opts.count ?? 12
    const spread = opts.spread ?? 0
    const sizeMin = opts.size?.[0] ?? 3
    const sizeMax = opts.size?.[1] ?? 6
    const durMin = opts.dur?.[0] ?? 500
    const durMax = opts.dur?.[1] ?? 900
    const distMin = opts.dist?.[0] ?? 30
    const distMax = opts.dist?.[1] ?? 80
    const hasDir = typeof opts.angle === 'number'
    const arc = opts.arc ?? Math.PI * 2
    const squash = opts.squash ?? 1
    for (let i = 0; i < count; i++) {
      const p = this.child(host, opts.cls ?? 'vfx-p-glow')
      const sz = sizeMin + Math.random() * (sizeMax - sizeMin)
      p.style.width = sz + 'px'
      p.style.height = sz + 'px'
      if (opts.inward) {
        // 向心收束：外圈取点 → 径向飞向圆心
        const aIn = Math.random() * Math.PI * 2
        const r0 = (opts.spawnRadius ?? 80) + (Math.random() - 0.5) * spread
        p.style.left = (Math.cos(aIn) * r0) + 'px'
        p.style.top = (Math.sin(aIn) * r0 * squash) + 'px'
        const dIn = r0 * (0.82 + Math.random() * 0.18)
        if (opts.directional) {
          p.style.setProperty('--rot', (Math.atan2(-Math.sin(aIn) * squash, -Math.cos(aIn)) * 180 / Math.PI) + 'deg')
          p.style.setProperty('--dx', dIn + 'px')
          p.style.setProperty('--dy', ((Math.random() - 0.5) * dIn * 0.12 - (opts.rise ?? 0)) + 'px')
        } else {
          p.style.setProperty('--dx', (-Math.cos(aIn) * dIn) + 'px')
          p.style.setProperty('--dy', (-Math.sin(aIn) * dIn * squash - (opts.rise ?? 0)) + 'px')
        }
      } else {
        p.style.left = (Math.random() - 0.5) * spread + 'px'
        p.style.top = (Math.random() - 0.5) * spread + 'px'
        const a = hasDir ? (opts.angle as number) + (Math.random() - 0.5) * arc : Math.random() * Math.PI * 2
        const d = distMin + Math.random() * (distMax - distMin)
        if (opts.directional) {
          p.style.setProperty('--rot', (a * 180 / Math.PI) + 'deg')
          p.style.setProperty('--dx', d + 'px')
          p.style.setProperty('--dy', ((Math.random() - 0.5) * d * 0.22 - (opts.rise ?? 0)) + 'px')
        } else {
          p.style.setProperty('--dx', (Math.cos(a) * d) + 'px')
          p.style.setProperty('--dy', (Math.sin(a) * d - (opts.rise ?? 0)) + 'px')
        }
      }
      p.style.setProperty('--dur', (durMin + Math.random() * (durMax - durMin)) + 'ms')
      if (opts.spin) p.style.setProperty('--spin', (Math.random() * 360) + 'deg')
    }
    this.retire(host, durMax + 200)
    return host
  }

  /** 持续发射器：每 interval 喷一小簇粒子，duration 后自动停止 */
  emit(x: number, y: number, opts: VfxEmitOptions): { stop: () => void } {
    const interval = opts.interval ?? 90
    const duration = opts.duration ?? 600
    const perBurst = opts.perBurst ?? 3
    const timer = setInterval(() => {
      this.particles(x, y, {
        cls: opts.cls,
        count: perBurst,
        spread: opts.spread,
        size: opts.size,
        dur: opts.dur,
        dist: opts.dist,
        angle: opts.angle,
        arc: opts.arc,
        rise: opts.rise,
        directional: opts.directional,
        inward: opts.inward,
        spawnRadius: opts.spawnRadius,
        squash: opts.squash,
        c: opts.c,
        c2: opts.c2,
      })
    }, interval)
    this.timers.add(timer)
    const stopHandle = this.after(duration, () => {
      clearInterval(timer)
      this.timers.delete(timer)
    })
    return {
      stop: () => {
        clearInterval(timer)
        this.timers.delete(timer)
        clearTimeout(stopHandle)
        this.timers.delete(stopHandle)
      },
    }
  }

  /**
   * 路径拖尾：沿 (x1,y1)→(x2,y2) 线段均匀播撒粒子，向法线两侧飞散。
   * 用于刀光尾迹、光束填充、雷电通道。
   */
  trail(x1: number, y1: number, x2: number, y2: number, opts: VfxTrailOptions): HTMLElement {
    const count = opts.count ?? 24
    const jitter = opts.jitter ?? 6
    const scatter = opts.scatter ?? 30
    const dx = x2 - x1
    const dy = y2 - y1
    const len = Math.hypot(dx, dy) || 1
    const nx = -dy / len
    const ny = dx / len
    const host = this.node(null, x1, y1)
    const sizeMin = opts.size?.[0] ?? 3
    const sizeMax = opts.size?.[1] ?? 6
    const dMin = opts.dur?.[0] ?? 400
    const dMax = opts.dur?.[1] ?? 900
    for (let i = 0; i < count; i++) {
      const t = count === 1 ? 0 : i / (count - 1)
      const p = this.child(host, opts.cls ?? 'vfx-p-glow')
      const sz = sizeMin + Math.random() * (sizeMax - sizeMin)
      p.style.width = sz + 'px'
      p.style.height = sz + 'px'
      const jx = (Math.random() - 0.5) * jitter * 2
      const jy = (Math.random() - 0.5) * jitter * 2
      p.style.left = (dx * t + jx) + 'px'
      p.style.top = (dy * t + jy) + 'px'
      const side = Math.random() < 0.5 ? -1 : 1
      const s = Math.random() * scatter
      p.style.setProperty('--dx', (nx * s * side + jx * 0.6) + 'px')
      p.style.setProperty('--dy', (ny * s * side + jy * 0.6) + 'px')
      p.style.setProperty('--dur', (dMin + Math.random() * (dMax - dMin)) + 'ms')
    }
    this.retire(host, dMax + 200)
    return host
  }

  /** 两点间光束：source→target 的旋转条带 */
  beam(sourceEl: HTMLElement, targetEl: HTMLElement, cls: string, width: number, dur: number): HTMLElement {
    const s = this.center(sourceEl)
    const t = this.center(targetEl)
    const dx = t.x - s.x
    const dy = t.y - s.y
    const dist = Math.hypot(dx, dy)
    const angle = Math.atan2(dy, dx) * 180 / Math.PI
    const host = this.node(null, s.x, s.y)
    const b = this.child(host, cls || 'vfx-beam')
    b.style.width = dist + 'px'
    b.style.height = (width || 4) + 'px'
    b.style.transform = 'rotate(' + angle + 'deg)'
    this.retire(host, dur || 400)
    return host
  }

  /** 弹道投射物：source→target 飞行 dur 毫秒，命中回调 onArrive */
  projectile(
    sourceEl: HTMLElement,
    targetEl: HTMLElement,
    cls: string,
    dur: number,
    opts: VfxProjectileOptions | undefined,
    onArrive?: () => void,
  ): HTMLElement {
    const s = this.center(sourceEl)
    const t = this.center(targetEl)
    const host = this.node(null, s.x, s.y)
    const p = this.child(host, cls || 'vfx-projectile')
    const lift = opts?.lift ?? 0
    const anim = p.animate([
      { transform: 'translate(0,0) scale(1)', offset: 0 },
      { transform: `translate(${(t.x - s.x) * 0.5}px,${(t.y - s.y) * 0.5 - lift}px) scale(1.15)`, offset: 0.55 },
      { transform: `translate(${t.x - s.x}px,${t.y - s.y}px) scale(1)`, offset: 1 },
    ], { duration: dur, easing: opts?.easing ?? 'cubic-bezier(0.3,0.7,0.4,1)', fill: 'forwards' })
    anim.onfinish = () => { onArrive?.() }
    this.retire(host, dur + 400)
    return host
  }

  /** 目标点冲击环（扩散圆环） */
  ring(targetEl: HTMLElement, cls: string, dur: number, scale?: number): HTMLElement {
    const t = this.center(targetEl)
    const host = this.node(null, t.x, t.y)
    const r = this.child(host, cls || 'vfx-ring')
    if (scale) r.style.setProperty('--ring-scale', String(scale))
    this.retire(host, dur || 600)
    return host
  }

  /** 屏幕震动：挂 .combat-arena-shell，仅 transform 不影响布局 */
  quake(heavy?: boolean): void {
    const s = this.shellEl
    const cls = heavy ? 'is-quake-heavy' : 'is-quake'
    s.classList.remove('is-quake', 'is-quake-heavy')
    void s.offsetWidth
    s.classList.add(cls)
    this.after(heavy ? 480 : 360, () => s.classList.remove(cls))
  }

  /** 注册功法演出：id 即真实 abilityId（ability.*）或通用反馈 id（feedback.*） */
  register(id: string, handler: VfxHandler): void {
    this.registry.set(id, handler)
  }

  has(id: string): boolean {
    return this.registry.has(id)
  }

  /** 播放已注册演出，返回演出总时长 ms（未注册或已卸载返回 0）；ctx 自动适配为共享脚本契约 */
  play(id: string, ctx: VfxPlayContext): number {
    if (this.disposed) return 0
    const handler = this.registry.get(id)
    if (!handler) return 0
    return handler(toGalleryContext(ctx)) ?? 0
  }

  /* ---------- DaohuaVFX 兼容门面：共享技能脚本经 window.DaohuaVFXModules 队列拿到本实例 ---------- */

  /** 技能模块专属样式注入（按 id 全局去重） */
  injectStyles(id: string, cssText: string): void {
    injectVfxStyles(id, cssText)
  }

  /** 覆盖层元素（共享脚本契约） */
  overlay(): HTMLElement {
    return this.overlayEl
  }

  /** 战场 shell 元素（共享脚本契约） */
  shell(): HTMLElement {
    return this.shellEl
  }

  /**
   * 以下方法在画廊沙箱中是真实结算/动作驱动，在正式前端全部由
   * 服务端快照驱动的既有组件（ArenaActor 动作/受击态、ArenaResource 跳字碎条）承担，
   * 特效脚本中的调用统一为空操作——同一脚本文件在两种宿主下行为自适配。
   */
  motion(_actorEl: HTMLElement | null, _kind: string): void {}
  impact(_actorEl: HTMLElement | null, _strength?: number): void {}
  floatText(_actorEl: HTMLElement | null, _text: string, _opts?: unknown): void {}
  resourceSlice(_actorEl: HTMLElement | null, _kind?: string, _left?: number, _width?: number): void {}
  defeat(_actorEl: HTMLElement | null): void {}
  revive(_actorEl: HTMLElement | null): void {}

  /** 清理全部特效节点与计时器（画廊 resetAll 契约的实例版） */
  resetAll(): void {
    this.timers.forEach(t => {
      clearTimeout(t)
      clearInterval(t)
    })
    this.timers.clear()
    this.liveNodes.forEach(el => el.remove())
    this.liveNodes.clear()
    this.shellEl.classList.remove('is-quake', 'is-quake-heavy')
  }

  /** 清理全部特效节点与计时器（组件卸载/战斗重置用） */
  dispose(): void {
    if (this.disposed) return
    this.disposed = true
    this.timers.forEach(t => {
      clearTimeout(t)
      clearInterval(t)
    })
    this.timers.clear()
    this.liveNodes.forEach(el => el.remove())
    this.liveNodes.clear()
    this.shellEl.classList.remove('is-quake', 'is-quake-heavy')
    this.overlayEl.replaceChildren()
  }
}
