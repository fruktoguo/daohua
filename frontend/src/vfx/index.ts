/**
 * 战斗特效模块入口（表现层，不参与任何战斗结算）
 *
 * 单一真源模型：
 * - 特效脚本真源在 docs/design/战斗演武画廊/vfx/（演武画廊同款文件，
 *   classic script 队列模块，无 import/export）——改一处，demo 与正式页面同时生效；
 * - 每个脚本向 window.DaohuaVFXModules 推入一个注册函数 `(vfx) => {...}`；
 *   本模块经 import.meta.glob eager 引入全部脚本（副作用=入队），
 *   createCombatVfx 创建引擎时 drain 队列完成注册；
 * - 引擎实例即 DaohuaVFX 门面（register/injectStyles/粒子原语同名方法，
 *   画廊结算类方法在前端为 noop——结算由服务端快照驱动的真实组件承担）。
 *
 * DOM 契约：出手方/目标 = .arena-actor 元素；玩家侧 .arena-actor--player、
 * 敌方侧 .arena-actor--enemy（DOM 顺序与 enemyEntries 一致）；.is-dead 视为离场。
 * 目标锚点统一取角色内 .arena-avatar-stage。
 */
import { VfxEngine, type VfxModuleRegister, type VfxPlayContext } from './engine'
import type { CombatAbilityDefinition } from '../game/types'

export { injectVfxStyles } from './engine'
export type { VfxEngine, VfxPlayContext } from './engine'

/** 共享特效脚本：入队 window.DaohuaVFXModules（副作用导入，文件即真源） */
import.meta.glob('../../../docs/design/战斗演武画廊/vfx/feedback.js', { eager: true })
import.meta.glob('../../../docs/design/战斗演武画廊/vfx/abilities/*.js', { eager: true })

declare global {
  interface Window {
    DaohuaVFXModules?: VfxModuleRegister[]
  }
}

/** 出手方立绘台元素（弹道/粒子锚点统一取 .arena-avatar-stage） */
export function actorStageEl(actorEl: HTMLElement | null): HTMLElement | null {
  return (actorEl?.querySelector('.arena-avatar-stage') as HTMLElement | null) ?? actorEl
}

/** 出手方立绘台中心点（覆盖层坐标系） */
export function actorStageCenter(vfx: VfxEngine, actorEl: HTMLElement | null): { x: number; y: number } {
  return vfx.center(actorStageEl(actorEl))
}

/** 一次出手演出请求（abilityId 为 null 表示普攻） */
export interface VfxCastRequest {
  abilityId: string | null
  /** 出手方所属侧 */
  side: 'ally' | 'enemy'
  /** 敌方出手时的 enemyEntries 下标（己方恒为 0） */
  casterIndex?: number
  /** 吟唱时长 ms（供法阵/前摇对齐节奏） */
  castMs?: number
  /** 已解析的功法配表 */
  ability?: CombatAbilityDefinition | null
}

/** 吟唱前摇请求 */
export interface VfxCastRingRequest {
  side: 'ally' | 'enemy'
  casterIndex?: number
  castMs?: number
  /** 已解析的功法配表（法阵变体按 ability.id 取色相/轨迹） */
  ability?: CombatAbilityDefinition | null
}

function aliveActors(shellEl: HTMLElement, cls: string): HTMLElement[] {
  return Array.from(shellEl.querySelectorAll<HTMLElement>(`.arena-actor--${cls}`))
    .filter(el => !el.classList.contains('is-dead'))
}

export class CombatVfxRuntime {
  private readonly engine: VfxEngine
  private readonly shellEl: HTMLElement

  constructor(shellEl: HTMLElement, overlayEl: HTMLElement) {
    this.shellEl = shellEl
    this.engine = new VfxEngine(shellEl, overlayEl)
    // drain 共享脚本队列：画廊 abilities/feedback 脚本已全部入队，逐一向本引擎注册
    for (const registerModule of window.DaohuaVFXModules ?? []) {
      registerModule(this.engine)
    }
  }

  /** 出手方元素定位：己方=玩家卡片；敌方按 enemyEntries 下标取 DOM 序（含已阵亡位次保持一致） */
  private casterElement(side: 'ally' | 'enemy', casterIndex = 0): HTMLElement | null {
    if (side === 'ally') {
      return this.shellEl.querySelector<HTMLElement>('.arena-actor--player')
    }
    return this.shellEl.querySelectorAll<HTMLElement>('.arena-actor--enemy')[casterIndex] ?? null
  }

  /** 按配表 targetType 解析目标元素（演出时刻快照存活状态） */
  private resolveTargets(side: 'ally' | 'enemy', casterEl: HTMLElement | null, ability?: CombatAbilityDefinition | null): HTMLElement[] {
    const allies = aliveActors(this.shellEl, 'player')
    const enemies = aliveActors(this.shellEl, 'enemy')
    const friends = side === 'ally' ? allies : enemies
    const foes = side === 'ally' ? enemies : allies
    switch (ability?.targetType) {
      case 'self':
        return casterEl ? [casterEl] : []
      case 'all-allies':
      case 'lowest-hp-ally':
        // 己方当前仅玩家单位；多单位扩展时 friends 自然覆盖
        return friends.length > 0 ? friends : casterEl ? [casterEl] : []
      case 'all-enemies':
        return foes
      case 'enemy':
      default:
        return foes.slice(0, 1)
    }
  }

  /** 出手演出：已注册功法播专属粒子演出，未注册功法走通用爆发兜底，普攻播命中爆闪 */
  playCast(req: VfxCastRequest): void {
    const casterEl = this.casterElement(req.side, req.casterIndex)
    // 出手时刻出手方已离场/阵亡（残留计时器到点）：不播
    if (!casterEl || casterEl.classList.contains('is-dead') || casterEl.classList.contains('is-dormant')) return
    const ctx: VfxPlayContext = {
      side: req.side,
      casterEl,
      targetEls: this.resolveTargets(req.side, casterEl, req.ability),
      ability: req.ability ?? null,
      castMs: req.castMs,
      after: (ms, fn) => { this.engine.after(ms, fn) },
    }
    if (req.abilityId) {
      const id = this.engine.has(req.abilityId) ? req.abilityId : 'feedback.burst'
      this.engine.play(id, ctx)
    } else {
      this.engine.play('feedback.attack', ctx)
    }
  }

  /** 吟唱开始演出（聚灵法阵，随 castingAbilityId/pendingAbilityId 出现即播） */
  playCastRing(req: VfxCastRingRequest): void {
    const casterEl = this.casterElement(req.side, req.casterIndex)
    if (!casterEl || casterEl.classList.contains('is-dead')) return
    this.engine.play('feedback.castRing', {
      side: req.side,
      casterEl,
      targetEls: [],
      ability: req.ability ?? null,
      castMs: req.castMs,
      after: (ms, fn) => { this.engine.after(ms, fn) },
    })
  }

  /** 卸载：清理全部特效节点与计时器 */
  dispose(): void {
    this.engine.dispose()
  }
}

/** 创建战斗特效运行时（CombatArena 挂载时调用一次） */
export function createCombatVfx(shellEl: HTMLElement, overlayEl: HTMLElement): CombatVfxRuntime {
  return new CombatVfxRuntime(shellEl, overlayEl)
}
