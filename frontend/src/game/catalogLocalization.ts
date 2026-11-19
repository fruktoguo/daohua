import { hasTranslation, type TFunction } from '../i18n/index.ts'
import type {
  ActionDefinition,
  BuildingDefinition,
  CombatAbilityDefinition,
  CombatEncounterDefinition,
  CombatEquipmentDefinition,
  CombatantDefinition,
  ItemDefinition,
  NearbyEntity,
  WorldMapDefinition,
  WorldNodeDefinition,
} from './types'

/** 若词条存在且非空则使用当前语言译文，否则保留服务端原始文案 */
function tr(t: TFunction, key: string, fallback: string): string {
  if (!hasTranslation(key)) return fallback
  const translated = t(key)
  return translated && translated !== key ? translated : fallback
}

function trOptional(t: TFunction, key: string, fallback?: string): string | undefined {
  if (!hasTranslation(key)) return fallback
  const translated = t(key)
  return translated && translated !== key ? translated : fallback
}

/** 本地化行动目录 */
export function localizeActions(actions: readonly ActionDefinition[], t: TFunction): ActionDefinition[] {
  return actions.map(action => ({
    ...action,
    name: tr(t, `game.action.${action.id}.name`, action.name),
    description: tr(t, `game.action.${action.id}.description`, action.description),
  }))
}

/** 本地化物品目录 */
export function localizeItems(items: readonly ItemDefinition[], t: TFunction): ItemDefinition[] {
  return items.map(item => ({
    ...item,
    name: tr(t, `game.item.${item.itemId}.label`, item.name),
    description: tr(t, `game.item.${item.itemId}.description`, item.description),
  }))
}

/** 本地化装备目录 */
export function localizeEquipment(equipment: readonly CombatEquipmentDefinition[], t: TFunction): CombatEquipmentDefinition[] {
  return equipment.map(eq => ({
    ...eq,
    name: tr(t, `game.item.${eq.equipmentId}.label`, eq.name),
    description: trOptional(t, `game.item.${eq.equipmentId}.description`, eq.description),
  }))
}

/** 本地化功法目录 */
export function localizeAbilities(abilities: readonly CombatAbilityDefinition[], t: TFunction): CombatAbilityDefinition[] {
  return abilities.map(ab => ({
    ...ab,
    name: tr(t, `game.ability.${ab.abilityId}.name`, ab.name),
    description: trOptional(t, `game.ability.${ab.abilityId}.description`, ab.description),
  }))
}

/** 本地化建筑目录 */
export function localizeBuildings(buildings: readonly BuildingDefinition[], t: TFunction): BuildingDefinition[] {
  return buildings.map(building => ({
    ...building,
    name: tr(t, `game.building.${building.buildingId}.name`, building.name),
    description: tr(t, `game.building.${building.buildingId}.description`, building.description),
  }))
}

/** 本地化单个妖兽/战斗单位定义 */
export function localizeCombatant(combatant: CombatantDefinition, t: TFunction): CombatantDefinition {
  const name = tr(t, `game.monster.${combatant.enemyId}.name`, combatant.name)
  return name === combatant.name ? combatant : { ...combatant, name }
}

/** 本地化战斗遭遇目录（含遭遇名与下挂所有妖兽） */
export function localizeEncounters(encounters: readonly CombatEncounterDefinition[], t: TFunction): CombatEncounterDefinition[] {
  return encounters.map(enc => ({
    ...enc,
    name: tr(t, `game.encounter.${enc.encounterId}.name`, enc.name),
    enemy: localizeCombatant(enc.enemy, t),
    randomSpawns: enc.randomSpawns.map(spawn => ({
      ...spawn,
      enemy: localizeCombatant(spawn.enemy, t),
    })),
    eliteEnemies: enc.eliteEnemies?.map(enemy => localizeCombatant(enemy, t)),
    bossEnemies: enc.bossEnemies.map(enemy => localizeCombatant(enemy, t)),
  }))
}

function localizeNearbyEntity(entity: NearbyEntity, prefix: 'npc' | 'building', t: TFunction): NearbyEntity {
  return {
    ...entity,
    name: tr(t, `game.${prefix}.${entity.entityId}.name`, entity.name),
    description: tr(t, `game.${prefix}.${entity.entityId}.description`, entity.description),
    effect: tr(t, `game.${prefix}.${entity.entityId}.effect`, entity.effect),
  }
}

function localizeWorldNode(node: WorldNodeDefinition, t: TFunction): WorldNodeDefinition {
  return {
    ...node,
    name: tr(t, `game.node.${node.nodeId}.name`, node.name),
    description: tr(t, `game.node.${node.nodeId}.description`, node.description),
    npcs: node.npcs.map(npc => localizeNearbyEntity(npc, 'npc', t)),
    buildings: node.buildings.map(b => localizeNearbyEntity(b, 'building', t)),
    monsters: node.monsters?.map(enemy => localizeCombatant(enemy, t)),
    eliteEnemies: node.eliteEnemies?.map(enemy => localizeCombatant(enemy, t)),
    bossEnemies: node.bossEnemies?.map(enemy => localizeCombatant(enemy, t)),
  }
}

/** 本地化世界地图目录（含地图、节点、NPC、建筑与生态妖兽） */
export function localizeMaps(maps: readonly WorldMapDefinition[], t: TFunction): WorldMapDefinition[] {
  return maps.map(map => ({
    ...map,
    name: tr(t, `game.map.${map.mapId}.name`, map.name),
    description: tr(t, `game.map.${map.mapId}.description`, map.description),
    nodes: map.nodes.map(node => localizeWorldNode(node, t)),
  }))
}
