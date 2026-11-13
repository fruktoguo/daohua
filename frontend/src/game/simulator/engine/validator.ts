import type { GameDataCatalog } from './catalog.ts';
import { EMPTY_GUID, tryParseGuid } from './math.ts';
import {
  ALL_TRAITS,
  AutoUseBar,
  BUFF_CHANNEL_BY_KEY,
  CombatElement,
  isZongmenResidenceNodeId,
  ItemCategories,
  parseCombatElement,
  SlotGroupGeneral,
  SlotGroupPotion,
} from './rules.ts';
import type {
  AbilityJson,
  SimulatorEnvironmentDto,
  SimulatorProfileDto,
  TriggerConditionJson,
} from './types.ts';

export class SimulatorValidationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'SimulatorValidationError';
  }
}

const MAX_TRIGGERS_PER_LIST = 4;
const MAX_ENHANCEMENT_LEVEL = 20;

const UNIT_DEPS = new Set([
  'self',
  'target-enemy',
  'target',
  'ally-lowest-hp',
  'ally-highest-hp',
  'enemy-lowest-hp',
  'enemy-highest-hp',
]);
const SET_DEPS = new Set(['ally', 'enemy', 'allies', 'enemies']);
const NUMERIC_SET_CONDS = new Set([
  'alive-count',
  'dead-count',
  'total-count',
  'total-hp-percentage',
  'total-mana-percentage',
  'lowest-hp-percentage',
  'highest-hp-percentage',
]);
const BOOL_SET_CONDS = new Set(['has-player', 'has-elite', 'has-boss', 'has-summon']);
const NUMERIC_UNIT_CONDS = new Set([
  'current-hp',
  'missing-hp',
  'current-hp-percentage',
  'missing-hp-percentage',
  'current-mana',
  'missing-mana',
  'current-mana-percentage',
  'missing-mana-percentage',
  'buff-stacks',
]);
const BOOL_UNIT_CONDS = new Set([
  'is-player',
  'is-elite',
  'is-boss',
  'is-summon',
  'stun-status',
  'blind-status',
  'silence-status',
]);
const NUMERIC_COMPARATORS = new Set([
  'greater-than-equal',
  'less-than-equal',
  'greater-than',
  'less-than',
  'equal',
]);

export function normalizeMemberId(profile: SimulatorProfileDto, memberIndex: number): string {
  if (profile.sourcePlayerId === null || profile.sourcePlayerId === undefined) {
    return `51530000-0000-0000-0000-${String(memberIndex + 1).padStart(12, '0')}`;
  }
  const parsed = tryParseGuid(profile.sourcePlayerId);
  if (!parsed || parsed === EMPTY_GUID) {
    throw new SimulatorValidationError('预演档案源玩家标识无效。');
  }
  return parsed;
}

export function validateTrigger(
  trigger: TriggerConditionJson | unknown,
  knownBuffIds: Set<string> | null = null,
): void {
  if (!trigger || typeof trigger !== 'object') {
    throw new SimulatorValidationError('触发条件对象无效。');
  }
  const t = trigger as TriggerConditionJson;
  const dependency = typeof t.dependency === 'string' ? t.dependency : null;
  if (
    !dependency ||
    (!UNIT_DEPS.has(dependency) && !SET_DEPS.has(dependency) && dependency !== 'battle')
  ) {
    throw new SimulatorValidationError('触发条件对象无效。');
  }
  const condition = typeof t.condition === 'string' ? t.condition : null;
  let conditionValid = false;
  let isBool = false;
  if (dependency === 'battle') {
    conditionValid = condition === 'elapsed-seconds';
    isBool = false;
  } else if (SET_DEPS.has(dependency)) {
    isBool = condition !== null && BOOL_SET_CONDS.has(condition);
    conditionValid = isBool || (condition !== null && NUMERIC_SET_CONDS.has(condition));
  } else {
    isBool = condition !== null && BOOL_UNIT_CONDS.has(condition);
    conditionValid = isBool || (condition !== null && NUMERIC_UNIT_CONDS.has(condition));
  }
  if (!conditionValid) {
    throw new SimulatorValidationError('触发条件类型对所选对象不可用。');
  }
  const comparator = typeof t.comparator === 'string' ? t.comparator : null;
  const comparatorValid = isBool
    ? comparator === 'is-active' || comparator === 'is-inactive'
    : comparator !== null && NUMERIC_COMPARATORS.has(comparator);
  if (!comparatorValid) {
    throw new SimulatorValidationError('触发条件比对方式与类型不匹配。');
  }
  const val = typeof t.value === 'number' ? t.value : 0;
  if (!isBool && (!Number.isFinite(val) || val < 0)) {
    throw new SimulatorValidationError('触发条件阈值无效。');
  }
  if (condition !== 'buff-stacks') return;
  const buffId = typeof t.buffId === 'string' ? t.buffId : null;
  if (!buffId || buffId.trim().length === 0) {
    throw new SimulatorValidationError('状态层数条件必须指定具体状态。');
  }
  if (knownBuffIds !== null && !knownBuffIds.has(buffId)) {
    throw new SimulatorValidationError(`触发条件引用了不存在的状态：${buffId}。`);
  }
}

export function validateTriggerList(
  triggers: unknown,
  knownBuffIds: Set<string> | null = null,
): void {
  if (!Array.isArray(triggers)) {
    throw new SimulatorValidationError('触发条件必须是数组。');
  }
  if (triggers.length > MAX_TRIGGERS_PER_LIST) {
    throw new SimulatorValidationError(`触发条件最多 ${MAX_TRIGGERS_PER_LIST} 条。`);
  }
  for (const trigger of triggers) {
    validateTrigger(trigger, knownBuffIds);
  }
}

function validateAbilityBuff(buffElement: Record<string, unknown>): void {
  const duration = typeof buffElement.durationSeconds === 'number' ? buffElement.durationSeconds : 0;
  for (const [k, v] of Object.entries(buffElement)) {
    if (!BUFF_CHANNEL_BY_KEY.has(k)) continue;
    if (v !== undefined && (typeof v !== 'number' || !Number.isFinite(v))) {
      throw new SimulatorValidationError('技能 Buff 数值无效。');
    }
  }
  for (const k of [
    'missingHitpointsPerStack',
    'resistancePerStack',
    'resistanceStackRatio',
    'spellAttackStackPercent',
  ]) {
    const val = typeof buffElement[k] === 'number' ? (buffElement[k] as number) : 0;
    if (!Number.isFinite(val) || val < 0) {
      throw new SimulatorValidationError(`技能 Buff 的 ${k} 不能为负数。`);
    }
  }
  const stackPolicy = buffElement.stackPolicy;
  if (
    stackPolicy !== undefined &&
    stackPolicy !== null &&
    stackPolicy !== 'capped' &&
    stackPolicy !== 'perCaster' &&
    stackPolicy !== 'maxKeep'
  ) {
    throw new SimulatorValidationError('技能 Buff 的 stackPolicy 无效。');
  }
  if (buffElement.resetOnWave !== undefined && typeof buffElement.resetOnWave !== 'boolean') {
    throw new SimulatorValidationError('技能 Buff 的 resetOnWave 必须为布尔值。');
  }
  if (!Number.isFinite(duration) || duration <= 0) {
    throw new SimulatorValidationError('技能 Buff 必须配置正持续时长。');
  }
}

export function validateAbility(ability: AbilityJson): void {
  const discipline = typeof ability.discipline === 'string' ? ability.discipline : 'spell';
  if (discipline !== 'internal' && discipline !== 'spell') {
    throw new SimulatorValidationError('功法类别（discipline）必须为 internal 或 spell。');
  }
  if (discipline === 'internal') {
    if (
      ability.rootExpEfficiencyPerLevel !== undefined &&
      (typeof ability.rootExpEfficiencyPerLevel !== 'number' ||
        ability.rootExpEfficiencyPerLevel < 0)
    ) {
      throw new SimulatorValidationError('内功参悟效率必须为非负数。');
    }
    if (ability.cultivationExpBonusPoints !== undefined && !Number.isFinite(ability.cultivationExpBonusPoints)) {
      throw new SimulatorValidationError('内功修为增减点必须为有限数值。');
    }
    return;
  }
  const healing = ability.effectType === 'heal';
  const ratio = healing
    ? typeof ability.healingRatio === 'number'
      ? ability.healingRatio
      : 0
    : typeof ability.damageRatio === 'number'
      ? ability.damageRatio
      : 0;
  const flat = healing
    ? typeof ability.flatHealing === 'number'
      ? ability.flatHealing
      : 0
    : typeof ability.flatDamage === 'number'
      ? ability.flatDamage
      : 0;
  const cast = typeof ability.castMilliseconds === 'number' ? ability.castMilliseconds : 0;
  const cooldown =
    typeof ability.cooldownMilliseconds === 'number' ? ability.cooldownMilliseconds : 0;
  const target = typeof ability.targetType === 'string' ? ability.targetType : null;
  const supportive =
    target === 'self' || target === 'lowestHitpointsAlly' || target === 'allAllies';
  const buffElements: Record<string, unknown>[] = [];
  if (Array.isArray(ability.buffs)) {
    for (const entry of ability.buffs) {
      if (!entry || typeof entry !== 'object') {
        throw new SimulatorValidationError('技能 buffs 列表元素必须为对象。');
      }
      buffElements.push(entry);
    }
  } else if (ability.buff && typeof ability.buff === 'object') {
    buffElements.push(ability.buff);
  }
  const threatGain = typeof ability.threatGain === 'number' ? ability.threatGain : 0;
  const threatPoints = typeof ability.threatPoints === 'number' ? ability.threatPoints : 0;
  const hasEffect =
    ratio + flat > 0 ||
    healing ||
    (supportive && (threatGain > 0 || threatPoints > 0 || buffElements.length > 0));
  const manaCost = typeof ability.manaCost === 'number' ? ability.manaCost : 0;
  if (ratio < 0 || flat < 0 || !hasEffect || cast < 0 || cooldown < 0 || cast + cooldown <= 0 || manaCost < 0) {
    throw new SimulatorValidationError('技能未配置可执行效果或有效施放周期。');
  }
  if (ability.category !== 'physical' && ability.category !== 'spell') {
    throw new SimulatorValidationError('技能伤害类别或五行属性无效。');
  }
  // element parse check
  const el = parseCombatElement(typeof ability.element === 'string' ? ability.element : 'none');
  if (el === CombatElement.None && ability.element && ability.element.toLowerCase() !== 'none') {
    throw new SimulatorValidationError('技能伤害类别或五行属性无效。');
  }
  for (const spec of buffElements) validateAbilityBuff(spec);
  if (Array.isArray(ability.defaultTriggers)) {
    for (const trigger of ability.defaultTriggers) validateTrigger(trigger);
  }
}

export function validateRespawnTargets(
  members: readonly SimulatorProfileDto[],
  memberIds: string[],
): void {
  for (let i = 0; i < members.length; i++) {
    const profile = members[i];
    if (profile.respawnPolicy < 0 || profile.respawnPolicy > 2) {
      throw new SimulatorValidationError('预演档案复活策略无效。');
    }
    let target: string | null = null;
    if (profile.respawnTargetPlayerId !== null && profile.respawnTargetPlayerId !== undefined) {
      const parsed = tryParseGuid(profile.respawnTargetPlayerId);
      if (!parsed || parsed === EMPTY_GUID) {
        throw new SimulatorValidationError('预演档案复活目标无效。');
      }
      target = parsed;
    }
    if (profile.respawnPolicy !== 2) continue;
    if (target === null) {
      throw new SimulatorValidationError('指定队友存活复活策略必须指定复活目标。');
    }
    const targetIndex = memberIds.indexOf(target);
    if (targetIndex < 0) {
      throw new SimulatorValidationError('复活目标不在当前推演阵容中。');
    }
    if (targetIndex === i) {
      throw new SimulatorValidationError('复活目标必须是阵容中的另一名成员。');
    }
  }
}

export function validateEnvironment(
  environment: SimulatorEnvironmentDto,
  gameData: GameDataCatalog,
): void {
  if (!Array.isArray(environment.nodes)) {
    throw new SimulatorValidationError('环境快照节点列表缺失。');
  }
  if (!Number.isFinite(environment.capturedAtUnixSeconds)) {
    throw new SimulatorValidationError('环境快照捕获时间无效。');
  }
  const seen = new Set<string>();
  for (const node of environment.nodes) {
    if (!node || !node.nodeId || node.nodeId.trim().length === 0 || seen.has(node.nodeId)) {
      throw new SimulatorValidationError('环境快照节点标识为空或重复。');
    }
    seen.add(node.nodeId);
    const isStatic = gameData.worldNodes.has(node.nodeId);
    const isResidence = isZongmenResidenceNodeId(node.nodeId);
    if (!isStatic && !isResidence) {
      throw new SimulatorValidationError(`环境快照节点不存在：${node.nodeId}。`);
    }
    if (!Array.isArray(node.buildings) || !node.effects || !Array.isArray(node.usages)) {
      throw new SimulatorValidationError(`环境快照节点 ${node.nodeId} 条目缺字段。`);
    }
    for (const b of node.buildings) {
      if (!b || !b.buildingId || !gameData.buildings.has(b.buildingId)) {
        throw new SimulatorValidationError(`环境快照节点 ${node.nodeId} 引用了不存在的建筑。`);
      }
      if (b.level < 0 || b.targetLevel < 0 || !Number.isFinite(b.startedAtUnixSeconds)) {
        throw new SimulatorValidationError(`环境快照节点 ${node.nodeId} 建筑状态无效。`);
      }
    }
    for (const u of node.usages) {
      if (
        !u ||
        !u.buildingId ||
        !gameData.buildings.has(u.buildingId) ||
        !Number.isFinite(u.activeUsers) ||
        u.activeUsers < 0 ||
        u.capacity < 0 ||
        !Number.isFinite(u.efficiency) ||
        u.efficiency < 0
      ) {
        throw new SimulatorValidationError(`环境快照节点 ${node.nodeId} 建筑拥挤条目无效。`);
      }
    }
    for (const val of Object.values(node.effects)) {
      if (val !== undefined && !Number.isFinite(val)) {
        throw new SimulatorValidationError(`环境快照节点 ${node.nodeId} 建筑效果存在非法数值。`);
      }
    }
  }
}

export function validateMember(
  profile: SimulatorProfileDto,
  memberIndex: number,
  gameData: GameDataCatalog,
): void {
  if (
    !profile ||
    !Number.isFinite(profile.capturedAtUnixSeconds) ||
    !Number.isFinite(profile.hitpoints) ||
    profile.hitpoints < 0 ||
    !Number.isFinite(profile.maxHitpoints) ||
    profile.maxHitpoints <= 0 ||
    !Number.isFinite(profile.manapoints) ||
    profile.manapoints < 0 ||
    !Number.isFinite(profile.maxManapoints) ||
    profile.maxManapoints < 0 ||
    !Number.isFinite(profile.regenCarrySeconds) ||
    profile.regenCarrySeconds < 0 ||
    !Number.isFinite(profile.experience) ||
    profile.experience < 0 ||
    !Number.isFinite(profile.foundation) ||
    profile.foundation < 0 ||
    !Number.isFinite(profile.combatExperience) ||
    profile.combatExperience < 0 ||
    !Number.isFinite(profile.lifetimeExperience) ||
    profile.lifetimeExperience < 0 ||
    !Number.isFinite(profile.ageSeconds) ||
    profile.ageSeconds < 0 ||
    !Number.isFinite(profile.completedActions) ||
    profile.completedActions < 0
  ) {
    throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员基础数值无效。`);
  }
  if (profile.realmLevel < 1) {
    throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员境界无效。`);
  }
  if (
    !profile.coreAttributes ||
    !Array.isArray(profile.abilitySlots) ||
    !Array.isArray(profile.equipment) ||
    !profile.combatSkills ||
    !Array.isArray(profile.combatBuffCarry) ||
    !Array.isArray(profile.buffs) ||
    !Array.isArray(profile.combatAutoUseSlots) ||
    !Array.isArray(profile.assets)
  ) {
    throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员缺必需段。`);
  }

  // Equipment
  const eqSlots = new Set<string>();
  for (const slot of profile.equipment) {
    if (!slot || !slot.itemId || slot.itemId.trim().length === 0) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员装备条目无效。`);
    }
    if (
      !Number.isFinite(slot.enhancementLevel) ||
      slot.enhancementLevel < 0 ||
      slot.enhancementLevel > MAX_ENHANCEMENT_LEVEL ||
      slot.enhancementLevel !== Math.floor(slot.enhancementLevel)
    ) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员装备强化等级无效。`);
    }
    const def = gameData.rawEquipments.get(slot.itemId);
    if (!def) {
      throw new SimulatorValidationError(
        `预演档案第 ${memberIndex + 1} 名成员装备了不存在的装备：${slot.itemId}。`,
      );
    }
    const slotId = String(def.slot ?? '');
    if (
      eqSlots.has(slotId) ||
      ((slotId === 'two-hand' && (eqSlots.has('weapon') || eqSlots.has('off-hand'))) ||
        ((slotId === 'weapon' || slotId === 'off-hand') && eqSlots.has('two-hand')))
    ) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员装备槽位冲突。`);
    }
    eqSlots.add(slotId);
  }

  // Abilities
  if (profile.abilitySlots.length > 16) {
    throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员功法槽位过多。`);
  }
  const slotted = new Set<string>();
  for (let slotIndex = 0; slotIndex < profile.abilitySlots.length; slotIndex++) {
    const slot = profile.abilitySlots[slotIndex];
    if (!slot) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员功法槽位无效。`);
    }
    if (!Number.isFinite(slot.level) || slot.level < 1 || slot.level !== Math.floor(slot.level)) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员功法等级无效。`);
    }
    if (slot.abilityId === null || slot.abilityId === undefined) continue;
    const ability = gameData.abilities.get(slot.abilityId);
    if (!ability || slotted.has(slot.abilityId)) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员功法不存在或重复装配。`);
    }
    slotted.add(slot.abilityId);
    validateAbility(ability);
    const discipline = typeof ability.discipline === 'string' ? ability.discipline : 'spell';
    if ((slotIndex === 0) !== (discipline === 'internal')) {
      throw new SimulatorValidationError(
        slotIndex === 0
          ? `预演档案第 ${memberIndex + 1} 名成员内功位只能装配心法内功。`
          : `预演档案第 ${memberIndex + 1} 名成员术法位只能装配神通术法。`,
      );
    }
  }
  if (profile.abilityExperiences) {
    for (const [k, v] of Object.entries(profile.abilityExperiences)) {
      if (!k || k.trim().length === 0 || !gameData.abilities.has(k) || !Number.isFinite(v) || v < 0) {
        throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员功法经验条目无效。`);
      }
    }
  }
  if (profile.techniqueExperiences) {
    for (const [k, v] of Object.entries(profile.techniqueExperiences)) {
      if (!k || k.trim().length === 0 || !Number.isFinite(v) || v < 0) {
        throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员技艺经验条目无效。`);
      }
    }
  }

  // Traits
  const seenTraits = new Set<string>();
  for (const id of profile.traitIds ?? []) {
    if (!id || id.trim().length === 0 || !ALL_TRAITS.has(id) || seenTraits.has(id)) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员特质不存在或重复。`);
    }
    seenTraits.add(id);
  }
  if (profile.unlockedTraits) {
    for (const tr of profile.unlockedTraits) {
      if (!tr || !tr.id || !ALL_TRAITS.has(tr.id)) {
        throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员觉醒特质不存在。`);
      }
      if (tr.level < 1 || tr.level > 20 || !Number.isFinite(tr.exp) || tr.exp < 0) {
        throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员特质等级无效。`);
      }
    }
  }

  // Triggers
  if (profile.abilityTriggers) {
    for (const [k, v] of Object.entries(profile.abilityTriggers)) {
      if (!k || k.trim().length === 0 || !gameData.abilities.has(k)) {
        throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员功法触发条件键无效。`);
      }
      validateTriggerList(v, gameData.triggerBuffIds);
    }
  }
  if (profile.consumableTriggers) {
    for (const [k, v] of Object.entries(profile.consumableTriggers)) {
      const item = gameData.items.get(k);
      if (!item || item.category !== ItemCategories.Consumable) {
        throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员消耗品触发条件键无效。`);
      }
      validateTriggerList(v, gameData.triggerBuffIds);
    }
  }

  // Assets
  const seenAssets = new Set<string>();
  for (const asset of profile.assets) {
    if (!asset || !asset.itemId || asset.itemId.trim().length === 0) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员资产条目无效。`);
    }
    if (!Number.isFinite(asset.quantity) || asset.quantity < 0) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员资产数量无效。`);
    }
    const key = `${asset.itemId}\0${asset.variant ?? ''}`;
    if (seenAssets.has(key)) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员资产存在重复条目。`);
    }
    seenAssets.add(key);
  }

  // AutoUseSlots
  const seenSlots = new Set<string>();
  const itemCounts = new Map<string, number>();
  const stackable = new Map<string, number>();
  for (const slot of profile.combatAutoUseSlots) {
    if (
      !slot ||
      slot.actionType !== AutoUseBar.Combat ||
      (slot.group !== SlotGroupGeneral && slot.group !== SlotGroupPotion) ||
      slot.slotIndex < 0 ||
      slot.slotIndex >= 3
    ) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员战斗消耗品槽位无效。`);
    }
    const slotKey = `${slot.group}\0${slot.slotIndex}`;
    if (seenSlots.has(slotKey)) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员战斗消耗品槽位无效。`);
    }
    seenSlots.add(slotKey);
    if (
      !Number.isFinite(slot.quantity) ||
      slot.quantity <= 0 ||
      !Number.isFinite(slot.remainingDurationSeconds) ||
      slot.remainingDurationSeconds < 0 ||
      !Number.isFinite(slot.cooldownUntilUnixSeconds) ||
      slot.cooldownUntilUnixSeconds < 0
    ) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员战斗消耗品槽数量或时钟无效。`);
    }
    if (slot.itemId === null || slot.itemId === undefined) {
      if (slot.enabled) {
        throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员空消耗品槽不能启用。`);
      }
      continue;
    }
    const item = gameData.items.get(slot.itemId);
    if (
      !item ||
      item.category !== ItemCategories.Consumable ||
      !item.consumable ||
      !item.consumable.usableInBars.has(AutoUseBar.Combat)
    ) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员消耗品槽物品不适用于战斗栏。`);
    }
    if (
      slot.group === SlotGroupPotion &&
      (item.displaySubCategory !== ItemCategories.Pill || item.consumable.isContinuous)
    ) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员药品栏只能装配药品类即时消耗品。`);
    }
    itemCounts.set(slot.itemId, (itemCounts.get(slot.itemId) ?? 0) + 1);
    stackable.set(slot.itemId, item.consumable.stackableSlotCount);
  }
  for (const [itemId, count] of itemCounts.entries()) {
    if (count > Math.max(1, stackable.get(itemId) ?? 0)) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员消耗品槽同物品槽数超限。`);
    }
  }

  // Buffs & Skills
  for (const buff of profile.buffs) {
    if (
      !buff ||
      !buff.buffId ||
      buff.buffId.trim().length === 0 ||
      !Number.isFinite(buff.expiresAtUnixSeconds) ||
      !Number.isFinite(buff.stacks) ||
      buff.stacks <= 0
    ) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员战外增益条目无效。`);
    }
  }
  for (const carry of profile.combatBuffCarry) {
    if (
      !carry ||
      !carry.buffId ||
      carry.buffId.trim().length === 0 ||
      !Number.isFinite(carry.remainingSeconds) ||
      carry.remainingSeconds < 0 ||
      !Number.isFinite(carry.stacks) ||
      carry.stacks <= 0
    ) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员战内增益条目无效。`);
    }
  }
  if (!Array.isArray(profile.combatSkills.cooldowns)) {
    throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员技能冷却列表缺失。`);
  }
  for (const cd of profile.combatSkills.cooldowns) {
    if (!cd || !cd.abilityId || cd.abilityId.trim().length === 0 || !Number.isFinite(cd.remainingSeconds) || cd.remainingSeconds < 0) {
      throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员技能冷却条目无效。`);
    }
  }
  if (
    !Number.isFinite(profile.combatSkills.castRemainingSeconds) ||
    profile.combatSkills.castRemainingSeconds < 0 ||
    !Number.isFinite(profile.combatSkills.castCount) ||
    profile.combatSkills.castCount < 0
  ) {
    throw new SimulatorValidationError(`预演档案第 ${memberIndex + 1} 名成员吟唱状态无效。`);
  }
}
