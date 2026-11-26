import type { LogSegment } from '../logging/gameLogService'
import type {
  CombatAbilityDefinition,
  DuelBattleRecordDto,
  DuelRecordEffectDto,
  ItemDefinition,
  TriggerBuffOption,
} from './types'

export interface DuelRecordCatalog {
  abilities: ReadonlyArray<CombatAbilityDefinition>
  items: ReadonlyArray<ItemDefinition>
  buffs?: ReadonlyArray<TriggerBuffOption>
  traits?: Readonly<Record<string, { name: string }>>
}

export type DuelRecordTranslator = (key: string, params?: Record<string, string | number>) => string

export interface FormattedDuelAction {
  index: number
  atSeconds: number
  isAllyActor: boolean
  segments: LogSegment[]
}

const HIT_FLAG_DODGE = 1 << 1
const HIT_FLAG_CRIT = 1 << 2
const HIT_FLAG_BREAK = 1 << 3
const HIT_FLAG_PARRY = 1 << 4

const EFFECT_DAMAGE = 0
const EFFECT_HEAL = 1
const EFFECT_MANA_RESTORE = 2
const EFFECT_BUFF_GRANT = 3
const EFFECT_HP_DRAIN = 4
const EFFECT_MANA_DRAIN = 5
const EFFECT_REFLECT = 6
const EFFECT_TRAIT_BURST = 7
const EFFECT_FORMATION_STRIKE = 8
const EFFECT_UPKEEP_COST = 9
const EFFECT_DOWNED = 10

const ACTION_BASIC_ATTACK = 0
const ACTION_FORMATION = 3
const ACTION_TRAIT = 4
const ACTION_PERIODIC = 5

function formatRecordNumber(value: number): string {
  if (!Number.isFinite(value)) return '0'
  const rounded = Math.round(value * 100) / 100
  return String(rounded)
}

function resolveUnitName(record: DuelBattleRecordDto, index: number): string {
  return record.u[index]?.n || `#${index + 1}`
}

function resolveDefId(record: DuelBattleRecordDto, defIndex: number | null | undefined): string | null {
  if (defIndex === null || defIndex === undefined || defIndex < 0 || defIndex >= record.defs.length) return null
  return record.defs[defIndex] || null
}

function resolveElementLabel(element: number | undefined, t: DuelRecordTranslator): string {
  switch (element) {
    case 1: return t('combat.stats.duelRecordElementMetal')
    case 2: return t('combat.stats.duelRecordElementWood')
    case 3: return t('combat.stats.duelRecordElementWater')
    case 4: return t('combat.stats.duelRecordElementFire')
    case 5: return t('combat.stats.duelRecordElementEarth')
    default: return ''
  }
}

function resolveDamageTypeLabel(damageKind: number | undefined, element: number | undefined, t: DuelRecordTranslator): string {
  const elem = resolveElementLabel(element, t)
  switch (damageKind) {
    case 1: return `${elem}${t('combat.stats.duelRecordDamageTypeSpell')}`
    case 2: return t('combat.stats.duelRecordDamageTypeTrue')
    default: return `${elem}${t('combat.stats.duelRecordDamageTypePhysical')}`
  }
}

function buildEntitySegment(
  defId: string,
  catalog: DuelRecordCatalog,
  overrideLabel?: string | null,
): LogSegment {
  const ability = catalog.abilities.find(a => a.abilityId === defId)
  if (ability) {
    return {
      type: 'reference',
      kind: 'ability',
      id: defId,
      label: overrideLabel || ability.name || defId,
    }
  }
  const item = catalog.items.find(i => i.itemId === defId)
  if (item) {
    return {
      type: 'reference',
      kind: 'item',
      id: defId,
      label: overrideLabel || item.name || defId,
    }
  }
  const trait = catalog.traits?.[defId]
  if (trait) {
    return {
      type: 'reference',
      kind: 'trait',
      id: defId,
      label: overrideLabel || trait.name || defId,
    }
  }
  const buff = catalog.buffs?.find(b => b.buffId === defId)
  if (buff || defId.startsWith('buff.') || defId.startsWith('formation.')) {
    return {
      type: 'reference',
      kind: 'buff',
      id: defId,
      label: overrideLabel || buff?.name || defId,
    }
  }
  if (defId.startsWith('item.')) {
    return { type: 'reference', kind: 'item', id: defId, label: overrideLabel || defId }
  }
  if (defId.startsWith('trait.')) {
    return { type: 'reference', kind: 'trait', id: defId, label: overrideLabel || defId }
  }
  return { type: 'reference', kind: 'ability', id: defId, label: overrideLabel || defId }
}

function appendMechanicSegments(segments: LogSegment[], flags: number, t: DuelRecordTranslator): void {
  if ((flags & HIT_FLAG_BREAK) !== 0) {
    segments.push({ type: 'text', value: ' ' })
    segments.push({ type: 'reference', kind: 'mechanic', id: 'break', label: t('combat.stats.duelRecordMechanicBreak') })
  }
  if ((flags & HIT_FLAG_PARRY) !== 0) {
    segments.push({ type: 'text', value: ' ' })
    segments.push({ type: 'reference', kind: 'mechanic', id: 'parry', label: t('combat.stats.duelRecordMechanicParry') })
  }
  if ((flags & HIT_FLAG_CRIT) !== 0) {
    segments.push({ type: 'text', value: ' ' })
    segments.push({ type: 'reference', kind: 'mechanic', id: 'crit', label: t('combat.stats.duelRecordMechanicCrit') })
  }
}

function appendEffectClause(
  segments: LogSegment[],
  record: DuelBattleRecordDto,
  effect: DuelRecordEffectDto,
  catalog: DuelRecordCatalog,
  t: DuelRecordTranslator,
): void {
  const targetName = resolveUnitName(record, effect.tg)
  const sourceName = resolveUnitName(record, effect.s)
  const flags = effect.fl ?? 0
  const val = formatRecordNumber(effect.v ?? 0)
  const defId = resolveDefId(record, effect.df)

  switch (effect.ek) {
    case EFFECT_DAMAGE: {
      if ((flags & HIT_FLAG_DODGE) !== 0) {
        segments.push({ type: 'text', value: t('combat.stats.duelRecordDodgedBy', { target: targetName }) })
        segments.push({ type: 'reference', kind: 'mechanic', id: 'dodge', label: t('combat.stats.duelRecordMechanicDodge') })
        return
      }
      const dmgType = resolveDamageTypeLabel(effect.dk, effect.el, t)
      segments.push({ type: 'text', value: t('combat.stats.duelRecordDealtDamage', { target: targetName, value: val, damageType: dmgType }) })
      appendMechanicSegments(segments, flags, t)
      return
    }
    case EFFECT_HEAL: {
      segments.push({
        type: 'text',
        value: effect.s === effect.tg
          ? t('combat.stats.duelRecordHealSelf', { value: val })
          : t('combat.stats.duelRecordHealOther', { target: targetName, value: val }),
      })
      return
    }
    case EFFECT_MANA_RESTORE: {
      segments.push({
        type: 'text',
        value: effect.s === effect.tg
          ? t('combat.stats.duelRecordManaSelf', { value: val })
          : t('combat.stats.duelRecordManaOther', { target: targetName, value: val }),
      })
      return
    }
    case EFFECT_BUFF_GRANT: {
      segments.push({
        type: 'text',
        value: effect.s === effect.tg
          ? t('combat.stats.duelRecordBuffSelf')
          : t('combat.stats.duelRecordBuffOther', { target: targetName }),
      })
      if (defId) {
        segments.push(buildEntitySegment(defId, catalog))
      }
      if ((effect.v ?? 0) > 1) {
        segments.push({ type: 'text', value: ` ×${val}` })
      }
      return
    }
    case EFFECT_HP_DRAIN: {
      segments.push({ type: 'text', value: t('combat.stats.duelRecordHpDrain', { source: sourceName, value: val }) })
      return
    }
    case EFFECT_MANA_DRAIN: {
      segments.push({ type: 'text', value: t('combat.stats.duelRecordManaDrain', { target: targetName, value: val }) })
      return
    }
    case EFFECT_REFLECT: {
      const dmgType = resolveDamageTypeLabel(effect.dk, effect.el, t)
      segments.push({ type: 'text', value: t('combat.stats.duelRecordReflect', { source: sourceName, target: targetName, value: val, damageType: dmgType }) })
      return
    }
    case EFFECT_TRAIT_BURST: {
      const dmgType = resolveDamageTypeLabel(effect.dk, effect.el, t)
      if (defId) {
        segments.push(buildEntitySegment(defId, catalog))
        segments.push({ type: 'text', value: t('combat.stats.duelRecordTraitBurstPrefix', { target: targetName, value: val, damageType: dmgType }) })
      } else {
        segments.push({ type: 'text', value: t('combat.stats.duelRecordTraitBurstFallback', { target: targetName, value: val, damageType: dmgType }) })
      }
      return
    }
    case EFFECT_FORMATION_STRIKE: {
      const dmgType = resolveDamageTypeLabel(effect.dk, effect.el, t)
      segments.push({ type: 'text', value: t('combat.stats.duelRecordFormationStrikeSource', { source: sourceName }) })
      if (defId) {
        segments.push(buildEntitySegment(defId, catalog))
      }
      segments.push({ type: 'text', value: t('combat.stats.duelRecordFormationStrikeTarget', { target: targetName, value: val, damageType: dmgType }) })
      return
    }
    case EFFECT_UPKEEP_COST: {
      if (defId) {
        segments.push(buildEntitySegment(defId, catalog))
        segments.push({
          type: 'text',
          value: defId.includes('tianlei')
            ? t('combat.stats.duelRecordUpkeepMana', { value: val })
            : t('combat.stats.duelRecordUpkeepHp', { value: val }),
        })
      } else {
        segments.push({ type: 'text', value: t('combat.stats.duelRecordUpkeepFallback', { value: val }) })
      }
      return
    }
    case EFFECT_DOWNED: {
      segments.push({ type: 'text', value: t('combat.stats.duelRecordDowned', { target: targetName }) })
      return
    }
  }
}

/**
 * 将单场紧凑斗法记录格式化为带图元引用的前端渲染条目列表。
 * 绝不丢失实体 ID 标识：技能/物品/阵法/特质/状态/机制一律生成 reference 段。
 */
export function formatDuelBattleRecord(
  record: DuelBattleRecordDto,
  catalog: DuelRecordCatalog,
  t: DuelRecordTranslator,
): FormattedDuelAction[] {
  return record.acts.map((act, index) => {
    const actorName = resolveUnitName(record, act.a)
    const defId = resolveDefId(record, act.d)
    const overrideName = resolveDefId(record, act.o)
    const segments: LogSegment[] = []

    if (act.k === ACTION_BASIC_ATTACK) {
      segments.push({ type: 'text', value: t('combat.stats.duelRecordVerbBasic', { actor: actorName }) })
    } else if (act.k === ACTION_PERIODIC) {
      segments.push({ type: 'text', value: `${actorName} ${t('combat.stats.duelRecordPeriodicAction')}` })
    } else if (defId) {
      const verbKey = act.k === ACTION_TRAIT
        ? 'combat.stats.duelRecordVerbTrigger'
        : act.k === ACTION_FORMATION
          ? 'combat.stats.duelRecordVerbActivate'
          : 'combat.stats.duelRecordVerbUse'
      segments.push({ type: 'text', value: t(verbKey, { actor: actorName }) })
      segments.push(buildEntitySegment(defId, catalog, overrideName))
    } else {
      segments.push({ type: 'text', value: actorName })
    }

    if (act.fx.length > 0) {
      segments.push({ type: 'text', value: t('combat.stats.duelRecordSentenceComma') })
      for (let i = 0; i < act.fx.length; i++) {
        if (i > 0) segments.push({ type: 'text', value: t('combat.stats.duelRecordClauseJoin') })
        const current = act.fx[i]
        if (current.ek === EFFECT_DAMAGE && ((current.fl ?? 0) & HIT_FLAG_DODGE) === 0) {
          const sameGroupTargets: number[] = [current.tg]
          let sameCount = 1
          while (i + sameCount < act.fx.length) {
            const next = act.fx[i + sameCount]
            if (
              next.ek === EFFECT_DAMAGE
              && next.s === current.s
              && (next.dk ?? 0) === (current.dk ?? 0)
              && (next.el ?? 0) === (current.el ?? 0)
              && (next.fl ?? 0) === (current.fl ?? 0)
              && Math.abs((next.v ?? 0) - (current.v ?? 0)) < 1e-6
            ) {
              sameGroupTargets.push(next.tg)
              sameCount++
            } else {
              break
            }
          }
          if (sameCount > 1) {
            const targetNames = sameGroupTargets.map(idx => resolveUnitName(record, idx)).join(t('combat.stats.duelRecordListSep'))
            const dmgType = resolveDamageTypeLabel(current.dk, current.el, t)
            segments.push({
              type: 'text',
              value: t('combat.stats.duelRecordDealtDamage', {
                target: targetNames,
                value: formatRecordNumber(current.v ?? 0),
                damageType: dmgType,
              }),
            })
            appendMechanicSegments(segments, current.fl ?? 0, t)
            i += sameCount - 1
            continue
          }
        }
        appendEffectClause(segments, record, current, catalog, t)
      }
    }

    segments.push({ type: 'text', value: t('combat.stats.duelRecordSentenceEnd') })
    return {
      index: index + 1,
      atSeconds: act.t,
      isAllyActor: Boolean(record.u[act.a]?.ally),
      segments,
    }
  })
}
