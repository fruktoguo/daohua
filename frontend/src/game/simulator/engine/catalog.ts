import { roundAwayFromZero } from './math.ts';
import {
  AutoUseBar,
  BuffIds,
  CombatDamageKind,
  CombatMonsterRank,
  createCombatantStats,
  ceilCombatantStats,
  growthSpeedPoints,
  growthBasicAttackBonusPoints,
  growthCooldownReductionPoints,
  growthCriticalDamagePoints,
  growthSkillBonusPoints,
  parseCombatElement,
  PartyWipeRule,
  TraitIds,
  type CombatantStats,
  type CombatEffectProfile,
} from './rules.ts';
import type {
  AbilityJson,
  ConsumableDefinition,
  ConsumableStrike,
  ItemDefinition,
  TriggerConditionJson,
} from './types.ts';

export interface BaseAttributeAnchor {
  level: number;
  hp: number;
  mp: number;
  ad: number;
  ap: number;
  armor: number;
  mr: number;
  acc: number;
  eva: number;
  crit: number;
  cavoid: number;
  breakPt: number;
  parry: number;
  hpRegen: number;
  mpRegen: number;
}

export class BaseAttributeCatalog {
  readonly anchors: BaseAttributeAnchor[];

  constructor(anchors: BaseAttributeAnchor[]) {
    if (anchors.length === 0) {
      throw new Error('基础属性锚点不能为空。');
    }
    this.anchors = anchors;
  }

  static parse(rawAnchors: Record<string, unknown>[]): BaseAttributeCatalog {
    const raw = BaseAttributeCatalog.toAnchors(rawAnchors);
    return new BaseAttributeCatalog(BaseAttributeCatalog.scaleAnchors(raw));
  }

  static parseRaw(rawAnchors: Record<string, unknown>[]): BaseAttributeCatalog {
    return new BaseAttributeCatalog(BaseAttributeCatalog.toAnchors(rawAnchors));
  }

  private static toAnchors(rawAnchors: Record<string, unknown>[]): BaseAttributeAnchor[] {
    return rawAnchors.map((a) => ({
      level: Number(a.level ?? 0),
      hp: Number(a.hp ?? 0),
      mp: Number(a.mp ?? 0),
      ad: Number(a.ad ?? 0),
      ap: Number(a.ap ?? 0),
      armor: Number(a.armor ?? 0),
      mr: Number(a.mr ?? 0),
      acc: Number(a.acc ?? 0),
      eva: Number(a.eva ?? 0),
      crit: Number(a.crit ?? 0),
      cavoid: Number(a.cavoid ?? 0),
      breakPt: Number(a.breakPt ?? 0),
      parry: Number(a.parry ?? 0),
      hpRegen: Number(a.hpRegen ?? 0),
      mpRegen: Number(a.mpRegen ?? 0),
    }));
  }

  private static scaleAnchors(raw: BaseAttributeAnchor[]): BaseAttributeAnchor[] {
    if (raw.length < 2 || raw[0].level !== 1) return raw;
    let idx18 = -1;
    for (let i = 0; i < raw.length; i++) {
      if (raw[i].level === 18) {
        idx18 = i;
        break;
      }
    }
    if (idx18 < 0) return raw;

    const a1 = raw[0];
    const a18 = raw[idx18];
    const result: BaseAttributeAnchor[] = new Array(raw.length);
    result[0] = a1;

    const scalePre18 = (v1: number, vi: number) => v1 + 3.0 * (vi - v1);
    const scalePost18 = (vNew18: number, vOld18: number, vi: number) => vNew18 + 2.0 * (vi - vOld18);

    const scaled18: BaseAttributeAnchor = {
      level: 18,
      hp: scalePre18(a1.hp, a18.hp),
      mp: scalePre18(a1.mp, a18.mp),
      ad: scalePre18(a1.ad, a18.ad),
      ap: scalePre18(a1.ap, a18.ap),
      armor: scalePre18(a1.armor, a18.armor),
      mr: scalePre18(a1.mr, a18.mr),
      acc: scalePre18(a1.acc, a18.acc),
      eva: scalePre18(a1.eva, a18.eva),
      crit: scalePre18(a1.crit, a18.crit),
      cavoid: scalePre18(a1.cavoid, a18.cavoid),
      breakPt: scalePre18(a1.breakPt, a18.breakPt),
      parry: scalePre18(a1.parry, a18.parry),
      hpRegen: scalePre18(a1.hpRegen, a18.hpRegen),
      mpRegen: scalePre18(a1.mpRegen, a18.mpRegen),
    };

    for (let i = 1; i < raw.length; i++) {
      const cur = raw[i];
      if (cur.level === 18) {
        result[i] = scaled18;
      } else if (cur.level < 18) {
        result[i] = {
          level: cur.level,
          hp: scalePre18(a1.hp, cur.hp),
          mp: scalePre18(a1.mp, cur.mp),
          ad: scalePre18(a1.ad, cur.ad),
          ap: scalePre18(a1.ap, cur.ap),
          armor: scalePre18(a1.armor, cur.armor),
          mr: scalePre18(a1.mr, cur.mr),
          acc: scalePre18(a1.acc, cur.acc),
          eva: scalePre18(a1.eva, cur.eva),
          crit: scalePre18(a1.crit, cur.crit),
          cavoid: scalePre18(a1.cavoid, cur.cavoid),
          breakPt: scalePre18(a1.breakPt, cur.breakPt),
          parry: scalePre18(a1.parry, cur.parry),
          hpRegen: scalePre18(a1.hpRegen, cur.hpRegen),
          mpRegen: scalePre18(a1.mpRegen, cur.mpRegen),
        };
      } else {
        result[i] = {
          level: cur.level,
          hp: scalePost18(scaled18.hp, a18.hp, cur.hp),
          mp: scalePost18(scaled18.mp, a18.mp, cur.mp),
          ad: scalePost18(scaled18.ad, a18.ad, cur.ad),
          ap: scalePost18(scaled18.ap, a18.ap, cur.ap),
          armor: scalePost18(scaled18.armor, a18.armor, cur.armor),
          mr: scalePost18(scaled18.mr, a18.mr, cur.mr),
          acc: scalePost18(scaled18.acc, a18.acc, cur.acc),
          eva: scalePost18(scaled18.eva, a18.eva, cur.eva),
          crit: scalePost18(scaled18.crit, a18.crit, cur.crit),
          cavoid: scalePost18(scaled18.cavoid, a18.cavoid, cur.cavoid),
          breakPt: scalePost18(scaled18.breakPt, a18.breakPt, cur.breakPt),
          parry: scalePost18(scaled18.parry, a18.parry, cur.parry),
          hpRegen: scalePost18(scaled18.hpRegen, a18.hpRegen, cur.hpRegen),
          mpRegen: scalePost18(scaled18.mpRegen, a18.mpRegen, cur.mpRegen),
        };
      }
    }
    return result;
  }

  private static compoundMultiplier(level: number, isAp = false): number {
    const pts = 3 * level;
    const rootMult = 1.0 + pts * (isAp ? 0.02 : 0.01);
    const equipMult =
      level < 18 ? 1.1158 : level < 30 ? 1.2042 : level < 42 ? 1.2814 : level < 54 ? 1.4654 : level < 66 ? 1.6516 : 1.8979;
    const buffMult =
      level < 18 ? 1.0042 : level < 30 ? 1.0133 : level < 42 ? 1.0333 : level < 54 ? 1.075 : level < 66 ? 1.1333 : 1.2292;
    return rootMult * equipMult * buffMult;
  }

  static parseExpected(
    tiers: Record<string, unknown>[],
    scaledBase: BaseAttributeCatalog | null,
    rawBase: BaseAttributeCatalog | null,
  ): BaseAttributeCatalog {
    const anchors: BaseAttributeAnchor[] = [];
    for (const tier of tiers) {
      if (!tier || typeof tier !== 'object') continue;
      const expected = tier.finalExpected as Record<string, unknown> | undefined;
      if (!expected || typeof expected !== 'object') continue;
      const level = Number(tier.level ?? 0);
      const field = (key: string) => Number(expected[key] ?? 0);
      const diff = (getter: (a: BaseAttributeAnchor) => number, isAp = false): number =>
        scaledBase !== null && rawBase !== null
          ? Math.max(0, getter(scaledBase.at(level)) - getter(rawBase.at(level))) *
            BaseAttributeCatalog.compoundMultiplier(level, isAp)
          : 0;
      anchors.push({
        level,
        hp: field('maxHitpoints') + diff((a) => a.hp),
        mp: field('maxManapoints') + diff((a) => a.mp),
        ad: field('physicalAttack') + diff((a) => a.ad),
        ap: field('magicAttack') + diff((a) => a.ap, true),
        armor: field('armor') + diff((a) => a.armor),
        mr: field('magicResistance') + diff((a) => a.mr),
        acc: field('accuracyRating') + diff((a) => a.acc),
        eva: field('evasionRating') + diff((a) => a.eva),
        crit: field('criticalRating') + diff((a) => a.crit),
        cavoid: field('criticalAvoidance') + diff((a) => a.cavoid),
        breakPt: field('breakRating') + diff((a) => a.breakPt),
        parry: field('parryRating') + diff((a) => a.parry),
        hpRegen: 0,
        mpRegen: 0,
      });
    }
    return new BaseAttributeCatalog(anchors);
  }

  at(level: number): BaseAttributeAnchor {
    let low = this.anchors[0];
    let high = low;
    for (let i = 0; i < this.anchors.length; i++) {
      const anchor = this.anchors[i];
      high = anchor;
      if (anchor.level >= level) break;
      low = anchor;
    }
    let ratio = high.level === low.level ? 0 : (level - low.level) / (high.level - low.level);
    if (ratio < 0) ratio = 0;
    else if (ratio > 1) ratio = 1;
    const mix = (a: number, b: number) => a + (b - a) * ratio;
    return {
      level,
      hp: mix(low.hp, high.hp),
      mp: mix(low.mp, high.mp),
      ad: mix(low.ad, high.ad),
      ap: mix(low.ap, high.ap),
      armor: mix(low.armor, high.armor),
      mr: mix(low.mr, high.mr),
      acc: mix(low.acc, high.acc),
      eva: mix(low.eva, high.eva),
      crit: mix(low.crit, high.crit),
      cavoid: mix(low.cavoid, high.cavoid),
      breakPt: mix(low.breakPt, high.breakPt),
      parry: mix(low.parry, high.parry),
      hpRegen: mix(low.hpRegen, high.hpRegen),
      mpRegen: mix(low.mpRegen, high.mpRegen),
    };
  }

  tierAt(level: number): number {
    let tier = 0;
    for (let i = 0; i < this.anchors.length; i++) {
      if (this.anchors[i].level > level) break;
      tier++;
    }
    return Math.max(1, tier);
  }
}

export class RealmProgressionCatalog {
  readonly expToNext: number[];
  readonly lifespanYearsByTier: number[];
  readonly discountedExpToNext: number[];
  private readonly rootPointCost: number[];
  private readonly rootPointCumulative: number[];

  constructor(expToNext: number[], lifespanYearsByTier: number[]) {
    this.expToNext = expToNext;
    this.lifespanYearsByTier = lifespanYearsByTier;
    this.discountedExpToNext = expToNext.map((baseExp, i) => {
      if (baseExp <= 0) return baseExp;
      const ratio = RealmProgressionCatalog.earlyUpgradeDiscountRatio(i + 1);
      return ratio <= 0 ? baseExp : roundAwayFromZero(baseExp * (1 - ratio));
    });
    const cap = expToNext.length * 3;
    const cost = new Array<number>(cap + 1).fill(0);
    const cumulative = new Array<number>(cap + 1).fill(0);
    const segments = this.discountedExpToNext;
    for (let p = 1; p <= cap; p++) {
      const t = (p - 1) / 3;
      const i0 = Math.floor(t);
      const i1 = Math.min(i0 + 1, segments.length - 1);
      const b0 = segments[i0 <= 0 ? 0 : i0 - 1];
      const b1 = segments[i1 - 1];
      cost[p] = b0 + (b1 - b0) * (t - i0);
      cumulative[p] = cumulative[p - 1] + cost[p];
    }
    this.rootPointCost = cost;
    this.rootPointCumulative = cumulative;
  }

  get maxLevel(): number {
    return this.expToNext.length;
  }

  get rootPointCap(): number {
    return this.expToNext.length * 3;
  }

  expCapFor(realmLevel: number): number {
    return realmLevel >= 1 && realmLevel <= this.expToNext.length ? this.expToNext[realmLevel - 1] : 0;
  }

  static earlyUpgradeDiscountRatio(targetLevel: number): number {
    if (!(targetLevel >= 1) || targetLevel > 30) return 0;
    return (0.95 * (30 - targetLevel)) / 29;
  }

  upgradeExpCapFor(realmLevel: number): number {
    return realmLevel >= 1 && realmLevel <= this.discountedExpToNext.length
      ? this.discountedExpToNext[realmLevel - 1]
      : 0;
  }

  cumulativeExpFor(level: number): number {
    const count = Math.min(Math.max(level - 1, 0), this.expToNext.length);
    let total = 0;
    for (let i = 0; i < count; i++) total += this.expToNext[i];
    return total;
  }

  rootLevelFor(exp: number): number {
    if (!Number.isFinite(exp) || exp <= 0) return 0;
    const arr = this.rootPointCumulative;
    let lo = 0;
    let hi = arr.length - 1;
    while (lo <= hi) {
      const mid = (lo + hi) >>> 1;
      const v = arr[mid];
      if (v === exp) return Math.min(Math.max(mid, 0), this.rootPointCap);
      if (v < exp) lo = mid + 1;
      else hi = mid - 1;
    }
    return Math.min(Math.max(lo - 1, 0), this.rootPointCap);
  }

  rootExpToNextFor(level: number): number {
    const idx = Math.floor(Math.max(0, level));
    if (idx < 0 || idx >= this.rootPointCap) return 0;
    return this.rootPointCost[idx + 1];
  }

  baseExperienceForLevel(level: number): number {
    const capLevel = Math.min(Math.max(level, 1), Math.max(1, this.expToNext.length - 1));
    return (this.expCapFor(capLevel) / 1000) * Math.pow(0.9, Math.max(1, level) - 1);
  }

  monsterKillExperience(level: number, rank: CombatMonsterRank): number {
    const multiplier = rank === CombatMonsterRank.Elite ? 10 : rank === CombatMonsterRank.Boss ? 40 : 1;
    return this.baseExperienceForLevel(level) * multiplier;
  }

  lifespanYearsFor(tier: number): number {
    if (this.lifespanYearsByTier.length === 0) return 0;
    const idx = Math.min(Math.max(tier, 1), this.lifespanYearsByTier.length) - 1;
    return this.lifespanYearsByTier[idx];
  }
}

export interface CombatDropDefinition {
  itemId: string;
  dropChance: number;
  minQuantity: number;
  maxQuantity: number;
}

export interface SkillSlotDefinition {
  ability: AbilityJson;
  abilityId: string;
  level: number;
  triggers: TriggerConditionJson[];
}

export interface CombatMonsterDefinition {
  id: string;
  name: string;
  budgetCost: number;
  level: number;
  rank: CombatMonsterRank;
  stats: CombatantStats;
  dropTable: CombatDropDefinition[];
  skills: SkillSlotDefinition[];
  experience: number;
}

export interface CombatSpawnDefinition {
  enemyId: string;
  weight: number;
  strength: number;
}

export interface CombatEncounterDefinition {
  id: string;
  name: string;
  minBudget: number;
  maxBudget: number;
  maxTotalStrength: number;
  searchSeconds: number;
  respawnSeconds: number;
  playerRespawnSeconds: number;
  randomSpawns: CombatSpawnDefinition[];
  eliteEnemyIds: string[];
  bossEnemyIds: string[];
  battlesPerBoss: number;
  battlesPerElite: number;
  onPartyWipe: PartyWipeRule;
}

export interface CombatActionDefinition {
  id: string;
  name: string;
  durationSeconds: number;
  combatEncounterId: string | null;
  allowedNodeIds: Set<string> | null;
  excludedNodeIds: Set<string> | null;
}

export interface WorldNodeDefinition {
  id: string;
  name: string;
  encounterIds: string[];
}

export interface DuelSettings {
  encounterChancePerBattle: number;
  powerBandRatio: number;
  defeatCooldownSeconds: number;
  stagedSettleSeconds: number;
  stagedBucketSeconds: number;
  dropChanceBonusPercent: number;
  maxDuelSeconds: number;
  maxBattleReports: number;
}

export interface CatalogJsonShape {
  contentVersion: string;
  fingerprint: string;
  duel: DuelSettings;
  expectedConfig: {
    baseAttributeAnchors: Record<string, unknown>[];
    levelExpected: Record<string, unknown>[];
    realmProgression: {
      expToNext: number[];
      lifespanYearsByTier: number[];
    };
  };
  abilities: Record<string, AbilityJson>;
  combatActions: Record<string, unknown>[];
  buildings: Record<string, Record<string, unknown>>;
  encounters: Record<string, unknown>[];
  monsters: Record<string, Record<string, unknown>>;
  maps: Record<string, unknown>[];
  rawItems: Record<string, Record<string, unknown>>;
  rawEquipments: Record<string, Record<string, unknown>>;
  actionYieldBuffs?: Array<{ buffId: string; name?: string }> | Record<string, string>;
}

export interface GameDataCatalog {
  contentVersion: string;
  fingerprint: string;
  duel: DuelSettings;
  baseAttributes: BaseAttributeCatalog;
  expectedAttributes: BaseAttributeCatalog;
  realmProgression: RealmProgressionCatalog;
  abilities: Map<string, AbilityJson>;
  items: Map<string, ItemDefinition>;
  rawItems: Map<string, Record<string, unknown>>;
  rawEquipments: Map<string, Record<string, unknown>>;
  buildings: Map<string, Record<string, unknown>>;
  combatMonsters: Map<string, CombatMonsterDefinition>;
  combatEncounters: Map<string, CombatEncounterDefinition>;
  combatActions: CombatActionDefinition[];
  /** 方便引擎按行动 ID O(1) 查表，与 combatActions 同源 */
  actions: Map<string, CombatActionDefinition>;
  /** 与 combatEncounters 同引用的便民别名 */
  encounters: Map<string, CombatEncounterDefinition>;
  /** 与 combatMonsters 同引用的便民别名 */
  monsters: Map<string, CombatMonsterDefinition>;
  worldNodes: Map<string, WorldNodeDefinition>;
  triggerBuffIds: Set<string>;
}

function num(obj: Record<string, unknown> | undefined | null, key: string, fallback = 0): number {
  if (!obj) return fallback;
  const v = obj[key];
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

function text(obj: Record<string, unknown> | undefined | null, key: string, fallback = ''): string {
  if (!obj) return fallback;
  const v = obj[key];
  return typeof v === 'string' ? v : fallback;
}

function parseItem(entry: Record<string, unknown>, id: string): ItemDefinition {
  const name = typeof entry.name === 'string' ? entry.name : id;
  const description = typeof entry.description === 'string' ? entry.description : '';
  const sellPrice = num(entry, 'sellPrice', 0);
  const isTradable = entry.isTradable === undefined ? true : Boolean(entry.isTradable);
  let consumable: ConsumableDefinition | null = null;
  const rawCons = entry.consumable as Record<string, unknown> | undefined;
  if (rawCons && typeof rawCons === 'object') {
    const usableInBars = new Set<string>();
    const rawBars = Array.isArray(rawCons.usableInActionTypes) ? rawCons.usableInActionTypes : [];
    for (const v of rawBars) {
      if (typeof v === 'string') usableInBars.add(v.trim().toLowerCase());
    }
    const triggers = Array.isArray(rawCons.triggers)
      ? (rawCons.triggers as TriggerConditionJson[])
      : null;
    let strike: ConsumableStrike | null = null;
    const rawStrike = rawCons.strike as Record<string, unknown> | undefined;
    if (rawStrike && typeof rawStrike === 'object') {
      strike = {
        manaCostRatio: num(rawStrike, 'manaCostRatio', 0),
        manaDamageRatio: num(rawStrike, 'manaDamageRatio', 0),
        spellAttackRatio: num(rawStrike, 'spellAttackRatio', 0),
        element: parseCombatElement(typeof rawStrike.element === 'string' ? rawStrike.element : 'none'),
      };
    }
    consumable = {
      usableInBars,
      durationSeconds: num(rawCons, 'durationSeconds', 0),
      isContinuous: Boolean(rawCons.isContinuous),
      cultivationMultiplier: num(rawCons, 'cultivationMultiplier', 1),
      gatheringMultiplier: num(rawCons, 'gatheringMultiplier', 1),
      alchemyMultiplier: num(rawCons, 'alchemyMultiplier', 1),
      triggers,
      durable: Boolean(rawCons.durable),
      strike,
      cooldownSeconds: num(rawCons, 'cooldownSeconds', 0),
      techniqueSpeedRatio: num(rawCons, 'techniqueSpeedRatio', 0),
      enhancementSuccessBonus: num(rawCons, 'enhancementSuccessBonus', 0),
      stackableSlotCount: Math.trunc(num(rawCons, 'stackableSlotCount', 0)),
    };
  }
  const category = typeof entry.category === 'string' ? entry.category : 'general';
  const subCategory = typeof entry.subCategory === 'string' ? entry.subCategory : null;
  return {
    id,
    name,
    description,
    isTradable,
    sellPrice,
    consumable,
    category,
    subCategory,
    displaySubCategory: subCategory ?? category,
  };
}

function parseMonsters(
  rawMonsters: Record<string, Record<string, unknown>>,
  playerBaseline: BaseAttributeCatalog,
  tierBoundaries: BaseAttributeCatalog,
  abilities: Map<string, AbilityJson>,
  realmProgression: RealmProgressionCatalog,
): Map<string, CombatMonsterDefinition> {
  const result = new Map<string, CombatMonsterDefinition>();
  for (const [id, value] of Object.entries(rawMonsters)) {
    const budgetCost = num(value, 'budgetCost', 1);
    const weights = (value.weights as Record<string, unknown> | undefined) ?? {};
    const weight = (k: string) => num(weights, k, 1);
    const level = Math.trunc(num(value, 'level', 1));
    const anchor = playerBaseline.at(level);
    const rankStr = text(value, 'rank', 'minion').toLowerCase();
    const rank =
      rankStr === 'elite'
        ? CombatMonsterRank.Elite
        : rankStr === 'boss'
          ? CombatMonsterRank.Boss
          : CombatMonsterRank.Minion;
    const attrMultiplier =
      rank === CombatMonsterRank.Elite ? 1.2 : rank === CombatMonsterRank.Boss ? 1.5 : 0.8;
    const hpMultiplier =
      rank === CombatMonsterRank.Elite ? 5.0 : rank === CombatMonsterRank.Boss ? 20.0 : 3.0;
    const rawInterval = num(value, 'attackIntervalMilliseconds', 0) / 1000;
    const interval = rawInterval > 0 ? rawInterval : 1;
    const attackMultiplier =
      level <= 1 ? 0.6 : level >= 24 ? 0.8 : 0.6 + (0.2 * (level - 1.0)) / 23.0;
    const ad = anchor.ad * attrMultiplier * weight('ad') * attackMultiplier;
    const ap = anchor.ap * attrMultiplier * weight('ap') * attackMultiplier;
    const tier = tierBoundaries.tierAt(level);

    const dropTable: CombatDropDefinition[] = [];
    if (Array.isArray(value.dropTable)) {
      for (const d of value.dropTable as Record<string, unknown>[]) {
        dropTable.push({
          itemId: text(d, 'itemId', ''),
          dropChance: num(d, 'dropChance', 0),
          minQuantity: num(d, 'minQuantity', 0),
          maxQuantity: num(d, 'maxQuantity', 0),
        });
      }
    }

    const skills: SkillSlotDefinition[] = [];
    if (Array.isArray(value.skills)) {
      for (const skill of value.skills as Record<string, unknown>[]) {
        const abilityId = text(skill, 'abilityId', '');
        const ability = abilities.get(abilityId);
        if (!ability) {
          throw new Error(`怪物 ${id} 引用了不存在的技能 ${abilityId}。`);
        }
        let triggers: TriggerConditionJson[];
        if (Array.isArray(skill.triggers)) {
          triggers = skill.triggers as TriggerConditionJson[];
        } else if (typeof skill.triggerHpRatio === 'number') {
          triggers = [
            {
              dependency: 'self',
              condition: 'current-hp-percentage',
              comparator: 'less-than-equal',
              value: skill.triggerHpRatio * 100,
            },
          ];
        } else if (Array.isArray(ability.defaultTriggers)) {
          triggers = ability.defaultTriggers;
        } else {
          triggers = [];
        }
        skills.push({
          ability,
          abilityId,
          level: num(skill, 'level', 1),
          triggers,
        });
      }
    }

    const modifiers = (value.modifiers as Record<string, unknown> | undefined) ?? {};
    const modifier = (k: string) => num(modifiers, k, 0);
    const effects: CombatEffectProfile = {
      hpDrainPoints: num(value, 'hpDrainPoints', 0),
      mpDrainPoints: num(value, 'mpDrainPoints', 0),
      damageReflectPoints: num(value, 'damageReflectPoints', 0),
      healingBonusPoints: num(value, 'healingBonusPoints', 0),
      threatOnTakenPoints: num(value, 'threatOnTakenPoints', 0),
    };

    const rawStats = createCombatantStats(
      anchor.hp * hpMultiplier * weight('hp'),
      ad,
      anchor.armor * attrMultiplier * weight('armor'),
      interval,
      anchor.acc * attrMultiplier * weight('hit'),
      anchor.eva * attrMultiplier * weight('eva') * 0.5,
      CombatDamageKind.Physical,
      {
        spellAttack: ap,
        maxManapoints: anchor.mp * attrMultiplier * weight('mp'),
        magicResistance: anchor.mr * attrMultiplier * weight('mres'),
        resistanceScale: tier,
        critical: anchor.crit * attrMultiplier * weight('crit'),
        criticalResistance: anchor.cavoid * attrMultiplier * weight('cavoid') * 0.5,
        criticalDamagePoints: growthCriticalDamagePoints(level) + modifier('criticalDamageBonusPoints'),
        breakPower: anchor.breakPt * attrMultiplier * weight('break'),
        parryResolve: anchor.parry * attrMultiplier * weight('parry'),
        element: parseCombatElement(text(value, 'element', 'none')),
        physicalDamageBonusPoints: modifier('physicalDamageBonusPoints'),
        spellDamageBonusPoints: modifier('spellDamageBonusPoints'),
        metalDamageBonusPoints: modifier('metalDamageBonusPoints'),
        woodDamageBonusPoints: modifier('woodDamageBonusPoints'),
        waterDamageBonusPoints: modifier('waterDamageBonusPoints'),
        fireDamageBonusPoints: modifier('fireDamageBonusPoints'),
        earthDamageBonusPoints: modifier('earthDamageBonusPoints'),
        totalDamageBonusPoints: modifier('totalDamageBonusPoints'),
        basicAttackBonusPoints:
          modifier('basicAttackBonusPoints') + growthBasicAttackBonusPoints(level),
        skillBonusPoints: modifier('skillBonusPoints') + growthSkillBonusPoints(tier),
        physicalDamageReductionPoints: modifier('physicalDamageReductionPoints'),
        spellDamageReductionPoints: modifier('spellDamageReductionPoints'),
        metalDamageReductionPoints: modifier('metalDamageReductionPoints'),
        woodDamageReductionPoints: modifier('woodDamageReductionPoints'),
        waterDamageReductionPoints: modifier('waterDamageReductionPoints'),
        fireDamageReductionPoints: modifier('fireDamageReductionPoints'),
        earthDamageReductionPoints: modifier('earthDamageReductionPoints'),
        damageAbsorptionPoints: modifier('damageAbsorptionPoints'),
        fixedDamageBonus: modifier('fixedDamageBonus'),
        fixedDamageReduction: modifier('fixedDamageReduction'),
        fixedPhysicalDamageBonus: modifier('fixedPhysicalDamageBonus'),
        fixedPhysicalDamageReduction: modifier('fixedPhysicalDamageReduction'),
        fixedSpellDamageBonus: modifier('fixedSpellDamageBonus'),
        fixedSpellDamageReduction: modifier('fixedSpellDamageReduction'),
        attackSpeedPoints: modifier('attackSpeedPoints') + growthSpeedPoints(level),
        actionSpeedPoints: modifier('actionSpeedPoints') + growthSpeedPoints(level),
        cooldownReductionPoints:
          modifier('cooldownReductionPoints') + growthCooldownReductionPoints(level),
        threatBonusPoints: modifier('threatBonusPoints'),
        effects,
        combatExperience: realmProgression.cumulativeExpFor(level),
      },
    );
    const stats = ceilCombatantStats(rawStats);
    const experience =
      value.experience !== undefined
        ? num(value, 'experience', 0)
        : realmProgression.monsterKillExperience(level, rank);
    result.set(id, {
      id,
      name: text(value, 'name', id),
      budgetCost,
      level,
      rank,
      stats,
      dropTable,
      skills,
      experience,
    });
  }
  return result;
}

function parseEncounters(rawEncounters: Record<string, unknown>[]): Map<string, CombatEncounterDefinition> {
  const result = new Map<string, CombatEncounterDefinition>();
  for (const value of rawEncounters) {
    const id = text(value, 'id', '');
    const spawns: CombatSpawnDefinition[] = Array.isArray(value.randomSpawns)
      ? (value.randomSpawns as Record<string, unknown>[]).map((s) => ({
          enemyId: text(s, 'enemyId', ''),
          weight: num(s, 'weight', 1),
          strength: num(s, 'strength', 1),
        }))
      : [];
    const maxBudget = num(value, 'maxBudget', 0);
    const wipeStr = text(value, 'onPartyWipe', 'respawn');
    const onPartyWipe =
      wipeStr === 'defeat' ? PartyWipeRule.Abort : PartyWipeRule.Retry;
    const eliteIds = Array.isArray(value.eliteEnemyIds)
      ? (value.eliteEnemyIds as string[])
      : [];
    const bossIds = Array.isArray(value.bossEnemyIds) ? (value.bossEnemyIds as string[]) : [];
    const battlesPerBoss = Math.trunc(num(value, 'battlesPerBoss', 0));
    const eliteInterval = Math.trunc(num(value, 'eliteInterval', 5));
    const battlesPerElite = Math.trunc(num(value, 'battlesPerElite', eliteInterval));
    result.set(id, {
      id,
      name: text(value, 'name', id),
      minBudget: num(value, 'minBudget', 0),
      maxBudget,
      maxTotalStrength: num(value, 'maxTotalStrength', maxBudget),
      searchSeconds: num(value, 'searchMilliseconds', 20_000) / 1000,
      respawnSeconds: num(value, 'respawnMilliseconds', 5_000) / 1000,
      playerRespawnSeconds: num(value, 'playerRespawnMilliseconds', 180_000) / 1000,
      randomSpawns: spawns,
      eliteEnemyIds: eliteIds,
      bossEnemyIds: bossIds,
      battlesPerBoss,
      battlesPerElite,
      onPartyWipe,
    });
  }
  return result;
}

function deriveTriggerBuffIds(
  rawItems: Map<string, Record<string, unknown>>,
  abilities: Map<string, AbilityJson>,
  actionYieldBuffs?: Array<{ buffId: string; name?: string }> | Record<string, string>,
): Set<string> {
  const buffs = new Set<string>();
  const add = (buffId: unknown) => {
    if (typeof buffId === 'string' && buffId.trim().length > 0) buffs.add(buffId);
  };
  for (const raw of rawItems.values()) {
    const cons = raw.consumable as Record<string, unknown> | undefined;
    if (!cons || !Array.isArray(cons.buffs)) continue;
    for (const buff of cons.buffs as Record<string, unknown>[]) add(buff?.buffId);
  }
  for (const [abilityId, ability] of abilities.entries()) {
    if (Array.isArray(ability.buffs)) {
      for (const entry of ability.buffs) {
        if (entry && typeof entry === 'object') {
          add((entry.buffId as string | undefined) ?? `ability-buff:${abilityId}`);
        }
      }
      continue;
    }
    if (ability.buff && typeof ability.buff === 'object') {
      add((ability.buff.buffId as string | undefined) ?? `ability-buff:${abilityId}`);
    }
  }
  if (Array.isArray(actionYieldBuffs)) {
    for (const grant of actionYieldBuffs) add(grant.buffId);
  } else if (actionYieldBuffs && typeof actionYieldBuffs === 'object') {
    for (const buffId of Object.keys(actionYieldBuffs)) add(buffId);
  }
  buffs.add(BuffIds.Unseen);
  buffs.add(BuffIds.FirstSight);
  buffs.add(BuffIds.Haste);
  buffs.add(BuffIds.Outnumbered);
  buffs.add(BuffIds.DuelKing);
  buffs.add(BuffIds.Xuyi);
  buffs.add(BuffIds.Ranhun);
  // 保持与 TraitIds 引用的机制一致
  void TraitIds;
  return buffs;
}

export function buildGameDataCatalog(raw: CatalogJsonShape): GameDataCatalog {
  const baseAttributes = BaseAttributeCatalog.parse(raw.expectedConfig.baseAttributeAnchors);
  const rawBaseAttributes = BaseAttributeCatalog.parseRaw(raw.expectedConfig.baseAttributeAnchors);
  const expectedAttributes = BaseAttributeCatalog.parseExpected(
    raw.expectedConfig.levelExpected,
    baseAttributes,
    rawBaseAttributes,
  );
  const realmProgression = new RealmProgressionCatalog(
    raw.expectedConfig.realmProgression.expToNext,
    raw.expectedConfig.realmProgression.lifespanYearsByTier,
  );

  const abilities = new Map<string, AbilityJson>(Object.entries(raw.abilities));
  const rawItems = new Map<string, Record<string, unknown>>(Object.entries(raw.rawItems));
  const items = new Map<string, ItemDefinition>();
  for (const [id, entry] of rawItems.entries()) {
    items.set(id, parseItem(entry, id));
  }
  const rawEquipments = new Map<string, Record<string, unknown>>(
    Object.entries(raw.rawEquipments),
  );
  const buildings = new Map<string, Record<string, unknown>>(Object.entries(raw.buildings));

  const combatMonsters = parseMonsters(
    raw.monsters,
    expectedAttributes,
    baseAttributes,
    abilities,
    realmProgression,
  );
  const combatEncounters = parseEncounters(raw.encounters);

  const combatActions: CombatActionDefinition[] = raw.combatActions.map((entry) => ({
    id: text(entry, 'id', ''),
    name: text(entry, 'name', ''),
    durationSeconds: num(entry, 'durationSeconds', 0),
    combatEncounterId: typeof entry.combatEncounterId === 'string' ? entry.combatEncounterId : null,
    allowedNodeIds: Array.isArray(entry.availableNodeIds)
      ? new Set((entry.availableNodeIds as string[]).filter((v) => v.length > 0))
      : null,
    excludedNodeIds: Array.isArray(entry.excludedNodeIds)
      ? new Set((entry.excludedNodeIds as string[]).filter((v) => v.length > 0))
      : null,
  }));

  const worldNodes = new Map<string, WorldNodeDefinition>();
  for (const map of raw.maps) {
    if (!map || !Array.isArray(map.nodes)) continue;
    for (const node of map.nodes as Record<string, unknown>[]) {
      const id = text(node, 'id', '');
      const name = text(node, 'name', id);
      if (!id || !name || worldNodes.has(id)) continue;
      const encounterIds = Array.isArray(node.encounterIds)
        ? (node.encounterIds as unknown[])
            .filter((e): e is string => typeof e === 'string' && e.trim().length > 0)
            .map((e) => e.trim())
        : [];
      worldNodes.set(id, { id, name, encounterIds });
    }
  }

  const triggerBuffIds = deriveTriggerBuffIds(rawItems, abilities, raw.actionYieldBuffs);
  const actions = new Map<string, CombatActionDefinition>(combatActions.map((a) => [a.id, a]));
  void AutoUseBar;

  return {
    contentVersion: raw.contentVersion,
    fingerprint: raw.fingerprint,
    duel: raw.duel,
    baseAttributes,
    expectedAttributes,
    realmProgression,
    abilities,
    items,
    rawItems,
    rawEquipments,
    buildings,
    combatMonsters,
    combatEncounters,
    combatActions,
    actions,
    encounters: combatEncounters,
    monsters: combatMonsters,
    worldNodes,
    triggerBuffIds,
  };
}
