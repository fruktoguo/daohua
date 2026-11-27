import type { ScheduledCombatEvent } from '../game/combatFeedback'
import type { CombatRecentEvent, ExpeditionDuelEvent } from '../game/types'

/** 持续阵法 Buff 与共享 VFX / 物品的映射元数据 */
export interface FormationBuffVfxMeta {
  readonly vfxId: string
  readonly itemId: string
  /** 是否为全队共享阵法（单阵营只渲染一座主导阵盘） */
  readonly teamWide: boolean
}

/** 战内阵法 Buff -> 共享 VFX 注册 ID（覆盖 4 张常驻阵盘） */
export const FORMATION_BUFF_TO_VFX: Readonly<Record<string, FormationBuffVfxMeta>> = {
  'formation.juling': { vfxId: 'item.juling-formation-disk', itemId: 'item.juling-formation-disk', teamWide: false },
  'formation.mizong': { vfxId: 'item.mizong-formation-disk', itemId: 'item.mizong-formation-disk', teamWide: true },
  'formation.tianlei': { vfxId: 'item.tianlei-formation-disk', itemId: 'item.tianlei-formation-disk', teamWide: false },
  'formation.xuesha': { vfxId: 'item.xuesha-formation-disk', itemId: 'item.xuesha-formation-disk', teamWide: false },
}

/** 6 种即时战斗丹药 Buff -> 消耗品物品 ID */
export const PILL_BUFF_TO_ITEM: Readonly<Record<string, string>> = {
  'buff.zhixue': 'item.zhixue-powder',
  'buff.shunqi': 'item.shunqi-pill',
  'buff.shengji': 'item.shengji-balm',
  'buff.ningqi': 'item.ningqi-powder',
  'buff.huangya': 'item.huangya-pill',
  'buff.heqi': 'item.heqi-pill',
}

/** 阵法反击/吸血触发事件 abilityId -> 触发态 VFX 注册 ID */
export const FORMATION_PROC_MAP: Readonly<Record<string, string>> = {
  'item.tianlei-formation-disk': 'item.tianlei-formation-disk.proc',
  'item.xuesha-formation-disk': 'item.xuesha-formation-disk.proc',
}

/** 阵法物品 ID -> 阵法 Buff ID（用于联动当前激活阵盘 handle） */
export const FORMATION_ITEM_TO_BUFF: Readonly<Record<string, string>> = {
  'item.juling-formation-disk': 'formation.juling',
  'item.mizong-formation-disk': 'formation.mizong',
  'item.tianlei-formation-disk': 'formation.tianlei',
  'item.xuesha-formation-disk': 'formation.xuesha',
}

/** 即时消耗品中文显示名 -> 消耗品物品 ID（用于原始日志流解析） */
export const INSTANT_CONSUMABLE_NAME_TO_ITEM: Readonly<Record<string, string>> = {
  雷击符: 'item.leiji-talisman',
  神行符: 'item.shenxing-talisman',
  定神符: 'item.dingshen-talisman',
  剑符: 'item.jian-talisman',
  怒焰符: 'item.nuyan-talisman',
  隐身符: 'item.yinshen-talisman',
  止血散: 'item.zhixue-powder',
  顺气丸: 'item.shunqi-pill',
  生肌膏: 'item.shengji-balm',
  凝气散: 'item.ningqi-powder',
  黄芽丹: 'item.huangya-pill',
  合气丹: 'item.heqi-pill',
}

/** 支持独立即时演出特效的全部消耗品物品 ID 集合 */
export const INSTANT_CONSUMABLE_ITEM_IDS: ReadonlySet<string> = new Set(
  Object.values(INSTANT_CONSUMABLE_NAME_TO_ITEM),
)

const AUTO_USE_ITEM_REF_RE = /\{\{item:(item\.[a-z0-9-]+)\|/i
const AUTO_USE_TEXT_NAME_RE = /^自动使用\s+([^\s，,。！!（(]+)/

/**
 * 将事件或 DOM 中的角色标识符归一为视角身份键（用于批处理分组与去重）。
 * - 本机玩家在组队讨伐中的服务端事件 ID 可能是 'player' 或 'ally:${selfIndex}'，统一归为 'player'；
 * - 其余队友归一为 'ally:${memberIndex}'；
 * - 敌方野怪与斗法镜像归一为 'enemy:${index}'。
 */
export function normalizeActorCanonicalKey(entityId: string, selfIndex?: number | null): string {
  const trimmed = entityId.trim()
  if (!trimmed || trimmed === 'player') return trimmed ? 'player' : ''
  const selfAllyId = typeof selfIndex === 'number' && selfIndex >= 0 ? `ally:${selfIndex}` : 'ally:0'
  if (trimmed === selfAllyId) return 'player'
  if (trimmed.startsWith('ally:')) {
    const parts = trimmed.split(':')
    if (parts.length >= 2) return `ally:${parts[1]}`
  }
  if (trimmed === 'enemy' || trimmed.startsWith('enemy:duel:')) return 'enemy:0'
  if (trimmed.startsWith('enemy:')) {
    const parts = trimmed.split(':')
    // 远征野怪 DOM actorKey 格式为 enemy:waveNumber:index:enemyId
    if (parts.length >= 4) return `enemy:${parts[2]}`
    if (parts.length >= 2) return `enemy:${parts[1]}`
  }
  return trimmed
}

/**
 * 判断服务端战斗事件中的实体 ID 是否匹配正式战场 `<ArenaActor data-actor-key="...">`。
 * 完整兼容单人/组队本机（player / ally:selfIndex）、队友（ally:idx:name）、
 * 远征野怪（enemy:wave:idx:enemyId）与斗法对手（enemy:duel:name）。
 */
export function isActorIdMatchingDomKey(
  entityId: string,
  domActorKey: string,
  selfIndex?: number | null,
): boolean {
  if (!entityId || !domActorKey) return false
  if (domActorKey === entityId) return true
  if (domActorKey === 'player') {
    const selfAlly = typeof selfIndex === 'number' && selfIndex >= 0 ? `ally:${selfIndex}` : 'ally:0'
    return entityId === 'player' || entityId === selfAlly
  }
  if (domActorKey.startsWith('ally:')) {
    const domParts = domActorKey.split(':')
    if (typeof selfIndex === 'number' && selfIndex >= 0 && Number(domParts[1]) === selfIndex && entityId === 'player') {
      return true
    }
    if (!entityId.startsWith('ally:')) return false
    const entityParts = entityId.split(':')
    return domParts.length >= 2 && entityParts.length >= 2 && domParts[1] === entityParts[1]
  }
  if (domActorKey.startsWith('enemy:')) {
    if (entityId !== 'enemy' && !entityId.startsWith('enemy:')) return false
    if (domActorKey.startsWith('enemy:duel:')) {
      return entityId === 'enemy:0' || entityId === 'enemy'
    }
    const domParts = domActorKey.split(':')
    const entityParts = entityId === 'enemy' ? ['enemy', '0'] : entityId.split(':')
    if (domParts.length >= 4 && entityParts.length >= 2) {
      return domParts[2] === entityParts[1]
    }
    if (domParts.length >= 2 && entityParts.length >= 2) {
      return domParts[1] === entityParts[1]
    }
  }
  return false
}

/**
 * 从服务端增量日志模板中识别「自动使用 雷击符 / 丹药」对应的消耗品 itemId。
 * 未命中即时战斗消耗品时返回 null（过滤强化/炼丹等持续消耗品自动续费日志）。
 */
export function parseAutoUseActivatedItemId(template: string): string | null {
  if (!template.startsWith('自动使用 ')) return null
  const refMatch = AUTO_USE_ITEM_REF_RE.exec(template)
  if (refMatch) {
    const id = refMatch[1]
    return INSTANT_CONSUMABLE_ITEM_IDS.has(id) ? id : null
  }
  const textMatch = AUTO_USE_TEXT_NAME_RE.exec(template)
  if (textMatch) {
    return INSTANT_CONSUMABLE_NAME_TO_ITEM[textMatch[1]] ?? null
  }
  return null
}

/** 聚合同一时刻、同一出手方、同一技能（AoE 群攻/群疗/群Buff）后的 VFX 播放批次 */
export interface CombatEventBatch {
  readonly key: string
  readonly at: number
  readonly delayMs: number
  readonly attackerId: string
  readonly canonicalAttackerKey: string
  readonly abilityId: string | null
  readonly targetIds: readonly string[]
  readonly events: readonly CombatRecentEvent[]
  readonly totalDamage: number
  readonly perTargetDamage: number | undefined
  readonly anyHit: boolean
  readonly anyCrit: boolean
  readonly anyBroken: boolean
  readonly anyParried: boolean
  readonly consumeKeys: readonly string[]
}

interface MutableCombatEventBatch {
  key: string
  at: number
  delayMs: number
  attackerId: string
  canonicalAttackerKey: string
  abilityId: string | null
  targetIds: string[]
  canonicalTargets: Set<string>
  events: CombatRecentEvent[]
  totalDamage: number
  anyHit: boolean
  anyCrit: boolean
  anyBroken: boolean
  anyParried: boolean
  consumeKeys: string[]
}

/**
 * 将 `collectScheduledCombatEvents` 输出的事件流聚合为 VFX 播放批次：
 * - 同一出手方、同一战斗时刻（ms 精度）且同一技能 ID 打向多个不同目标时（如火焰风暴、甘露、跃迁、踏云），
 *   合并为单个群攻/群辅批次，确保 Canvas AoE 特效完整覆盖 `targetEls` 而不重复叠加全屏爆炸；
 * - 若同一技能组内已存在非自身目标或伤害事件，则同组的自我回血/吸血（`targetId === attackerId` 且 `damage === 0`）
 *   仅并入事件与去重键消耗，不污染 `targetIds`，避免吸血词条让技能特效误打向施法者自身；
 * - 普攻或同一目标同一时刻的多段连击保持拆批，以保留错峰打击节奏。
 */
export function groupCombatEventBatches(
  scheduled: readonly ScheduledCombatEvent[],
  selfIndex?: number | null,
): CombatEventBatch[] {
  const batches: MutableCombatEventBatch[] = []
  const byGroupKey = new Map<string, MutableCombatEventBatch[]>()
  const groupHasPrimaryTarget = new Set<string>()

  for (const item of scheduled) {
    const ev = item.event
    if (!ev.abilityId) continue
    const canonicalAttacker = normalizeActorCanonicalKey(ev.attackerId, selfIndex)
    const canonicalTarget = normalizeActorCanonicalKey(ev.targetId, selfIndex)
    if (canonicalTarget !== canonicalAttacker || ev.damage > 0) {
      groupHasPrimaryTarget.add(`${Math.round(ev.at * 1000)}|${canonicalAttacker}|${ev.abilityId}`)
    }
  }

  for (const item of scheduled) {
    const ev = item.event
    const canonicalAttacker = normalizeActorCanonicalKey(ev.attackerId, selfIndex)
    const canonicalTarget = normalizeActorCanonicalKey(ev.targetId, selfIndex)
    const abilityId = ev.abilityId ?? null
    const atBucket = Math.round(ev.at * 1000)
    const groupKey = abilityId ? `${atBucket}|${canonicalAttacker}|${abilityId}` : ''
    const isSelfHealEcho = Boolean(
      abilityId &&
      groupHasPrimaryTarget.has(groupKey) &&
      canonicalTarget === canonicalAttacker &&
      ev.damage === 0,
    )

    let targetBatch: MutableCombatEventBatch | undefined
    if (abilityId) {
      const candidates = byGroupKey.get(groupKey)
      if (candidates) {
        targetBatch = isSelfHealEcho
          ? candidates[0]
          : candidates.find(candidate => !candidate.canonicalTargets.has(canonicalTarget))
      }
      if (!targetBatch) {
        targetBatch = {
          key: item.key,
          at: ev.at,
          delayMs: item.delay,
          attackerId: ev.attackerId,
          canonicalAttackerKey: canonicalAttacker,
          abilityId,
          targetIds: [],
          canonicalTargets: new Set(),
          events: [],
          totalDamage: 0,
          anyHit: false,
          anyCrit: false,
          anyBroken: false,
          anyParried: false,
          consumeKeys: [],
        }
        batches.push(targetBatch)
        if (candidates) candidates.push(targetBatch)
        else byGroupKey.set(groupKey, [targetBatch])
      }
    } else {
      targetBatch = {
        key: item.key,
        at: ev.at,
        delayMs: item.delay,
        attackerId: ev.attackerId,
        canonicalAttackerKey: canonicalAttacker,
        abilityId: null,
        targetIds: [],
        canonicalTargets: new Set(),
        events: [],
        totalDamage: 0,
        anyHit: false,
        anyCrit: false,
        anyBroken: false,
        anyParried: false,
        consumeKeys: [],
      }
      batches.push(targetBatch)
    }

    targetBatch.delayMs = Math.min(targetBatch.delayMs, item.delay)
    if (!isSelfHealEcho && !targetBatch.canonicalTargets.has(canonicalTarget)) {
      targetBatch.canonicalTargets.add(canonicalTarget)
      targetBatch.targetIds.push(ev.targetId)
    }
    targetBatch.events.push(ev)
    if (!isSelfHealEcho) {
      targetBatch.totalDamage += ev.damage
      targetBatch.anyHit = targetBatch.anyHit || ev.hit
      targetBatch.anyCrit = targetBatch.anyCrit || Boolean(ev.critical)
      targetBatch.anyBroken = targetBatch.anyBroken || Boolean(ev.broken)
      targetBatch.anyParried = targetBatch.anyParried || Boolean(ev.parried)
    }
    targetBatch.consumeKeys.push(item.key)
  }

  return batches.map((batch) => ({
    key: batch.key,
    at: batch.at,
    delayMs: batch.delayMs,
    attackerId: batch.attackerId,
    canonicalAttackerKey: batch.canonicalAttackerKey,
    abilityId: batch.abilityId,
    targetIds: batch.targetIds,
    events: batch.events,
    totalDamage: batch.totalDamage,
    perTargetDamage: batch.targetIds.length > 0 ? Math.round(batch.totalDamage / batch.targetIds.length) : undefined,
    anyHit: batch.anyHit,
    anyCrit: batch.anyCrit,
    anyBroken: batch.anyBroken,
    anyParried: batch.anyParried,
    consumeKeys: batch.consumeKeys,
  }))
}

/** 斩玉 -> 踏云 化形判定上下文 */
export interface ZhanyuMorphContext {
  readonly targetCount: number
  readonly hasYixingBuff: boolean
  readonly yixingWasConsumed: boolean
  readonly hasRecentYueqian: boolean
  readonly hasRecentTayunLog: boolean
}

/** 判断远征/实时战中 `ability.zhanyu` 是否因身负「移形」而化态为「踏云」 */
export function detectZhanyuTayunMorph(ctx: ZhanyuMorphContext): boolean {
  return ctx.targetCount > 1
    || ctx.hasRecentTayunLog
    || ctx.hasYixingBuff
    || ctx.yixingWasConsumed
    || ctx.hasRecentYueqian
}

const YIXING_CONSUME_DAMAGE_ABILITIES = new Set([
  'ability.zhanyu',
  'ability.heavy-slash',
  'ability.chain-strike',
  'ability.heavy-clouds',
  'ability.star-piercer',
  'ability.palm-lightning',
  'ability.blazing-blast',
  'ability.lightning-attraction',
  'ability.tide',
  'ability.huoyan-fengbao',
  'ability.tianlei-po',
  'ability.zhuoxin',
  'ability.benglie',
  'ability.tiangang-duanyue',
])

/** 斗法剧本中推断 `ability.zhanyu` 是否处于前置「移形」5 秒窗内化态为「踏云」 */
export function detectDuelZhanyuMorph(
  events: readonly ExpeditionDuelEvent[],
  targetIndex: number,
): boolean {
  const ev = events[targetIndex]
  if (!ev || ev.abilityId !== 'ability.zhanyu') return false
  let sameTickCount = 0
  for (let i = 0; i < events.length; i++) {
    const other = events[i]
    if (
      other.abilityId === 'ability.zhanyu'
      && other.attackerIsInitiator === ev.attackerIsInitiator
      && Math.abs(other.atSeconds - ev.atSeconds) < 1e-4
    ) {
      sameTickCount += 1
    }
  }
  if (sameTickCount > 1) return true

  const side = ev.attackerIsInitiator
  let stacks = 0
  let lastYixingAt = -Infinity
  let startIndex = targetIndex
  for (let i = targetIndex - 1; i >= 0; i--) {
    if (ev.atSeconds - events[i].atSeconds > 30) break
    startIndex = i
  }
  for (let i = startIndex; i < targetIndex; i++) {
    const prev = events[i]
    if (prev.attackerIsInitiator !== side) continue
    if (stacks > 0 && prev.atSeconds - lastYixingAt > 5.05) {
      stacks = 0
    }
    const id = prev.abilityId
    if (id === 'ability.yueqian') {
      stacks = Math.min(99, stacks + 1)
      lastYixingAt = prev.atSeconds
    } else if (id === 'ability.pojun') {
      if (stacks > 0) stacks -= 1
      stacks = Math.min(99, stacks + 1)
      lastYixingAt = prev.atSeconds
    } else if (id === 'ability.shunshan') {
      if (stacks > 0) {
        stacks -= 1
      } else {
        stacks = 1
        lastYixingAt = prev.atSeconds
      }
    } else if (id && YIXING_CONSUME_DAMAGE_ABILITIES.has(id)) {
      if (stacks > 0) stacks -= 1
    }
  }
  return stacks > 0 && ev.atSeconds - lastYixingAt <= 5.05
}

/** 将服务端 abilityId（含阵盘反击与斩玉化形）解析为注册在 `DaohuaVFX` 中的最终特效 ID */
export function resolveRegisteredVfxId(
  abilityId: string | null | undefined,
  morphed: boolean,
  hasVfx: (id: string) => boolean,
): string {
  if (!abilityId) return 'feedback.attack'
  const procId = FORMATION_PROC_MAP[abilityId]
  if (procId && hasVfx(procId)) return procId
  if (abilityId === 'ability.zhanyu' && morphed && hasVfx('ability.zhanyu.tayun')) {
    return 'ability.zhanyu.tayun'
  }
  if (hasVfx(abilityId)) return abilityId
  return hasVfx('feedback.burst') ? 'feedback.burst' : 'feedback.attack'
}

/**
 * 判定零伤害单占位批（`targetId === attackerId` 且 `damage === 0`）是否需按技能
 * `targetType`（兼容服务端 wire kebab-case 与配表 camelCase）展开为同侧全员或最低气血比例友方目标。
 */
export function resolveZeroDamageSupportExpansion(
  batch: Pick<CombatEventBatch, 'totalDamage' | 'targetIds' | 'canonicalAttackerKey'>,
  targetType: string | null | undefined,
  selfIndex?: number | null,
): 'all-allies' | 'lowest-hp-ally' | null {
  if (batch.totalDamage !== 0 || batch.targetIds.length !== 1) return null
  if (normalizeActorCanonicalKey(batch.targetIds[0], selfIndex) !== batch.canonicalAttackerKey) return null
  if (targetType === 'all-allies' || targetType === 'allAllies') return 'all-allies'
  if (targetType === 'lowest-hp-ally' || targetType === 'lowestHitpointsAlly') return 'lowest-hp-ally'
  return null
}
