/**
 * 正式战斗前端 Canvas 2D V2 特效引擎桥接层。
 *
 * 通过 Vite `import.meta.glob` 直接挂载 `docs/design/战斗演武画廊/vfx/` 唯一真源：
 * - 零复制：全部 55 个注册特效（通用反馈 / 21 主动功法 22 态 / 10 内功心法 / 特质反应 / 符箓阵盘丹药）与演武画廊完全同源；
 * - 零侧效导入报错：规避 `tsconfig.app.json` 的 `noUncheckedSideEffectImports` 限制；
 * - 宿主隔离：以 `mode: 'production'` 挂载，关闭画廊模拟浮动数字与假死亡样式，严格服从服务端权威结算。
 */

const SHARED_CORE_MODULES = import.meta.glob('../../../docs/design/战斗演武画廊/vfx/core.js', { eager: true })
const SHARED_FEEDBACK_MODULES = import.meta.glob('../../../docs/design/战斗演武画廊/vfx/feedback.js', { eager: true })
const SHARED_ABILITY_MODULES = import.meta.glob('../../../docs/design/战斗演武画廊/vfx/abilities/*.js', { eager: true })
const SHARED_CONSUMABLE_MODULES = import.meta.glob('../../../docs/design/战斗演武画廊/vfx/consumables/*.js', { eager: true })

export interface VfxSkillDescriptor {
  readonly id?: string
  readonly name?: string
  readonly element?: string
  readonly skillGroup?: string
  readonly targetType?: string
  readonly castMilliseconds?: number
  readonly cooldownMilliseconds?: number
}

export interface VfxPlayContext {
  readonly side?: 'ally' | 'enemy'
  readonly attackerKey?: string
  readonly attackVariant?: number | string
  readonly casterEl?: HTMLElement | null
  readonly targetEl?: HTMLElement | null
  readonly targetEls?: readonly HTMLElement[]
  readonly allyEls?: readonly HTMLElement[]
  readonly enemyEls?: readonly HTMLElement[]
  readonly ability?: string
  readonly skill?: VfxSkillDescriptor
  readonly element?: string
  readonly castMs?: number
  readonly durationMs?: number
  readonly crit?: boolean
  readonly broken?: boolean
  readonly parried?: boolean
  readonly hit?: boolean
  readonly damage?: number
  readonly perTargetDamage?: number
  readonly heal?: number
  readonly lifesteal?: number
  readonly morphed?: boolean
  readonly formationHandle?: VfxPersistentHandle | null
}

export interface VfxPersistentHandle {
  readonly stop?: (fadeMs?: number) => void
  readonly pulse?: (color?: string, intensity?: number) => void
  readonly moveTo?: (x: number, y: number) => void
}

export interface DaohuaVfxMountOptions {
  readonly mode?: 'gallery' | 'production'
}

export interface DaohuaVfxGlobalApi {
  readonly isCanvasV2?: boolean
  readonly version?: string
  readonly mount: (
    stageEl: HTMLElement,
    skyEl: HTMLElement | null,
    groundEl?: HTMLElement | null,
    opts?: DaohuaVfxMountOptions,
  ) => unknown
  readonly unmount?: (expectedStageEl?: HTMLElement | null) => void
  readonly dispose?: (expectedStageEl?: HTMLElement | null) => void
  readonly resize?: (notify?: boolean) => void
  readonly clear: () => void
  readonly has: (id: string) => boolean
  readonly listRegistered: () => string[]
  readonly play: (id: string, ctx?: VfxPlayContext) => unknown
  readonly flushModules?: () => void
}

declare global {
  interface Window {
    DaohuaVFX?: DaohuaVfxGlobalApi
    DaohuaVFXModules?: Array<(api: DaohuaVfxGlobalApi) => void>
  }
}

let sharedModulesEnsured = false

/**
 * 确认全部共享真源模块已被打包导入并完成队列刷写。
 * 返回全局 `window.DaohuaVFX` 实例。
 */
export function resolveSharedDaohuaVfx(): DaohuaVfxGlobalApi | null {
  if (!sharedModulesEnsured) {
    // 显式读取各 glob 键计数，防止开启 noUnusedLocals 或摇树时裁剪侧效模块
    const totalLoaded =
      Object.keys(SHARED_CORE_MODULES).length +
      Object.keys(SHARED_FEEDBACK_MODULES).length +
      Object.keys(SHARED_ABILITY_MODULES).length +
      Object.keys(SHARED_CONSUMABLE_MODULES).length
    if (totalLoaded > 0) {
      sharedModulesEnsured = true
    }
  }
  const api = typeof window !== 'undefined' ? (window.DaohuaVFX ?? null) : null
  if (api && typeof api.flushModules === 'function') {
    api.flushModules()
  }
  return api
}

/** 轻量判断持久句柄（如施法阵 / 常驻阵盘） */
export function isVfxPersistentHandle(value: unknown): value is VfxPersistentHandle {
  if (!value || typeof value !== 'object') return false
  const candidate = value as { stop?: unknown; pulse?: unknown; moveTo?: unknown }
  return (
    typeof candidate.stop === 'function' ||
    typeof candidate.pulse === 'function' ||
    typeof candidate.moveTo === 'function'
  )
}

/**
 * 正式战斗双层 Canvas VFX 引擎包装类。
 * 管理对共享 `DaohuaVFX` 核心的挂载、尺寸刷新、事件触发与按宿主引用安全卸载。
 */
export class VfxEngine {
  private readonly stageEl: HTMLElement
  private readonly skyOverlayEl: HTMLElement
  private readonly groundOverlayEl: HTMLElement | null
  private readonly api: DaohuaVfxGlobalApi | null
  private disposed = false

  constructor(
    stageEl: HTMLElement,
    skyOverlayEl: HTMLElement,
    groundOverlayEl: HTMLElement | null = null,
  ) {
    this.stageEl = stageEl
    this.skyOverlayEl = skyOverlayEl
    this.groundOverlayEl = groundOverlayEl
    this.api = resolveSharedDaohuaVfx()
    this.api?.mount(this.stageEl, this.skyOverlayEl, this.groundOverlayEl, {
      mode: 'production',
    })
  }

  has(id: string): boolean {
    if (this.disposed || !this.api) return false
    return this.api.has(id)
  }

  listRegistered(): string[] {
    if (!this.api) return []
    return this.api.listRegistered()
  }

  play(id: string, ctx?: VfxPlayContext): unknown {
    if (this.disposed || !this.api) return null
    return this.api.play(id, ctx)
  }

  resize(notify = true): void {
    if (this.disposed || !this.api) return
    this.api.resize?.(notify)
  }

  clear(): void {
    if (this.disposed || !this.api) return
    this.api.clear()
  }

  dispose(): void {
    if (this.disposed) return
    this.disposed = true
    if (this.api?.unmount) {
      this.api.unmount(this.stageEl)
      return
    }
    this.api?.dispose?.(this.stageEl)
  }
}
