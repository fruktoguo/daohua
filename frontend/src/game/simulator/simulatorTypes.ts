export interface SimulatorTraitProgressDto { id: string; level: number; exp: number }
export interface SimulatorLinggenDto { metal: number; wood: number; water: number; fire: number; earth: number }
export interface SimulatorCoreAttributesDto { staminaExp: number; meridiansExp: number; attackExp: number; boneExp: number; agilityExp: number; consciousnessExp: number }
export interface SimulatorAbilitySlotDto { abilityId: string | null; level: number }
export interface SimulatorEquipmentSlotDto { itemId: string; enhancementLevel: number }
export interface SimulatorBuffEffectDto { channel: number; value: number }
export interface SimulatorCombatSkillCooldownDto { abilityId: string; remainingSeconds: number }
export interface SimulatorCombatSkillsDto { cooldowns: SimulatorCombatSkillCooldownDto[]; castingAbilityId: string | null; castRemainingSeconds: number; castCount: number }
export interface SimulatorCombatBuffCarryDto { buffId: string; remainingSeconds: number; stacks: number; name: string; missingHitpointsPerStack: number; effects?: SimulatorBuffEffectDto[]; statRatios?: Record<string, number> }
export interface SimulatorActiveBuffDto { buffId: string; expiresAtUnixSeconds: number; stacks: number; name: string; sourceItemId: string | null; effects?: SimulatorBuffEffectDto[]; statRatios?: Record<string, number> }
export interface SimulatorAutoUseSlotDto { actionType: string; slotIndex: number; itemId: string | null; quantity: number; enabled: boolean; remainingDurationSeconds: number; cooldownUntilUnixSeconds: number; group: string }
export interface SimulatorAssetDto { itemId: string; variant: string; quantity: number }
export interface SimulatorProfileDto {
  formatVersion: number; gameDataFingerprint: string
  sourcePlayerId: string | null; displayName: string; avatar: string
  capturedAtUnixSeconds: number
  realmLevel: number; experience: number; foundation: number; combatExperience: number
  lifetimeExperience: number; ageSeconds: number; completedActions: number
  traitIds?: string[]; unlockedTraits?: SimulatorTraitProgressDto[]
  confirmedLinggen: SimulatorLinggenDto | null; heavenGateEntered: boolean
  coreAttributes: SimulatorCoreAttributesDto
  abilitySlots: SimulatorAbilitySlotDto[]
  abilityExperiences?: Record<string, number>
  equipment: SimulatorEquipmentSlotDto[]
  techniqueExperiences?: Record<string, number>
  abilityTriggers?: Record<string, unknown[]>
  consumableTriggers?: Record<string, unknown[]>
  hitpoints: number; maxHitpoints: number; manapoints: number; maxManapoints: number; regenCarrySeconds: number
  combatSkills: SimulatorCombatSkillsDto
  combatBuffCarry: SimulatorCombatBuffCarryDto[]
  buffs: SimulatorActiveBuffDto[]
  combatAutoUseSlots: SimulatorAutoUseSlotDto[]
  assets: SimulatorAssetDto[]
  respawnPolicy: number; respawnTargetPlayerId: string | null
}
export interface SimulatorNodeBuildingDto { buildingId: string; level: number; targetLevel: number; startedAtUnixSeconds: number; contributedMaterials?: Record<string, number> }
export interface SimulatorNodeBuildingUsageDto { buildingId: string; activeUsers: number; capacity: number; efficiency: number }
export interface SimulatorBuildingEffectsDto {
  explorationSpeedRatio?: number; explorationOutputRatio?: number
  alchemySpeedRatio?: number; alchemyOutputRatio?: number; cultivationOutputRatio?: number
  combatHitpointRegen?: number; combatDamageReductionRatio?: number; monsterWeakenRatio?: number
  gatheringSpeedRatio?: number; miningSpeedRatio?: number; forgingSpeedRatio?: number
  talismanSpeedRatio?: number; formationSpeedRatio?: number; enhancementSpeedRatio?: number
  combatManapointRegen?: number
}
export interface SimulatorEnvironmentNodeDto { nodeId: string; buildings: SimulatorNodeBuildingDto[]; effects: SimulatorBuildingEffectsDto; usages: SimulatorNodeBuildingUsageDto[] }
export interface SimulatorEnvironmentDto { formatVersion: number; gameDataFingerprint: string; capturedAtUnixSeconds: number; nodes: SimulatorEnvironmentNodeDto[] }
export interface SimulatorRunRequestDto { formatVersion: number; nodeId: string; difficulty: number; durationSeconds: number; seed: string; members: SimulatorProfileDto[]; environment: SimulatorEnvironmentDto }
export interface SimulatorRootExpDto { stamina: number; meridians: number; attack: number; bone: number; agility: number; consciousness: number }
export interface SimulatorReportItemDto { itemId: string; variant: string; quantity: number }
export interface SimulatorCombatUnitStatDto { unitKey: string; kind: number; displayName: string; damageDealtPhysical: number; damageDealtSpell: number; damageTakenPhysical: number; damageTakenSpell: number; damageMitigatedPhysical: number; damageMitigatedSpell: number; healingRegen: number; healingOther: number; damageDealtTrue: number; damageTakenTrue: number }
export interface SimulatorExpeditionEnemyDto { enemyId: string; hitpoints: number; manapoints: number }
export interface SimulatorEventDto { atUnixSeconds: number; code: string; message: string; actionId?: string; itemDeltas?: Record<string, number> }
export interface SimulatorMemberResultDto {
  memberIndex: number; playerId: string; displayName: string
  hitpoints: number; maxHitpoints: number; manapoints: number; maxManapoints: number; regenCarrySeconds: number
  combatSkills: SimulatorCombatSkillsDto; combatBuffCarry: SimulatorCombatBuffCarryDto[]
  completedRepeats: number; waveNumber: number; phase: string
  experience: number; foundation: number; combatExperience: number; lifetimeExperience: number
  rootExp: SimulatorRootExpDto
  cycles: number; encounters: number; normalKills: number; eliteKills: number; bossKills: number; deaths: number
  items: SimulatorReportItemDto[]; itemsLost: SimulatorReportItemDto[]
  assetDeltas: SimulatorAssetDto[]; remainingConsumableBalances: SimulatorAssetDto[]
  combatAutoUseSlots: SimulatorAutoUseSlotDto[]
  combatStats: SimulatorCombatUnitStatDto[]
  enemies: SimulatorExpeditionEnemyDto[]
  events: SimulatorEventDto[]; droppedEventCount: number
}
export interface SimulatorRunResultDto { formatVersion: number; nodeId: string; difficulty: number; seed: string; requestedDurationSeconds: number; simulatedSeconds: number; members: SimulatorMemberResultDto[] }
export interface SimulatorResolvedProfileDto { memberIndex: number; playerId: string; displayName: string; attributes: Record<string, unknown>; combatantStats: Record<string, unknown> }
export interface SimulatorResolveRequestDto { formatVersion: number; nodeId: string; members: SimulatorProfileDto[]; environment: SimulatorEnvironmentDto }
export interface SimulatorResolveResultDto { formatVersion: number; nodeId: string; members: SimulatorResolvedProfileDto[] }
export const SIMULATOR_FORMAT_VERSION = 1
