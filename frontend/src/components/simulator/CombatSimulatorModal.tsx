import { memo, useCallback, useEffect, useState } from 'react'
import { Modal } from '../Modal'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import {
  sanitizeAttributes,
  type MultiNodeSimulatorReport,
  type SimulatorCombatantConfig,
  type SimulatorConfig,
  type SimulatorNodeComparisonItem,
  type SimulatorReport,
} from '../../game/simulator/simulatorTypes'
import { ExpeditionSimulator } from '../../game/simulator/expeditionSimulator'
import { fetchNodeBuildingIndex } from '../../network/apiClient'
import { aggregateNodeBuildingEffects } from '../../game/buildingCalculations'
import type { MapNodeBuilding, NodeBuildingEffectSet } from '../../game/types'
import { StatGlyph } from '../StatGlyph'
import { SimulatorPartyEditor } from './SimulatorPartyEditor'
import { SimulatorConfigPanel } from './SimulatorConfigPanel'
import { SimulatorResultsView } from './SimulatorResultsView'

interface CombatSimulatorModalProps {
  open: boolean
  onClose: () => void
}

export const CombatSimulatorModal = memo(function CombatSimulatorModal({
  open,
  onClose,
}: CombatSimulatorModalProps): React.JSX.Element | null {
  const t = useT()

  // 依赖的全局游戏状态
  const myName = useGameStore(state => state.name)
  const myAttributes = useGameStore(state => state.combatAttributes)
  const myCombat = useGameStore(state => state.combat)
  const myConsumables = useGameStore(state => state.queue?.autoUseSlots?.combat)
  const currentNodeId = useGameStore(state => state.world?.nodeId)
  // 当前节点的权威建筑效果投影（服务端 EffectiveNodeBuildingEffects，含拥挤衰减）
  const currentBuildingEffects = useGameStore(state => state.world?.buildingEffects)
  const buildingDefinitions = useGameStore(state => state.buildings)
  const maps = useGameStore(state => state.maps)
  const encounters = useGameStore(state => state.encounters)
  const abilities = useGameStore(state => state.abilities)
  const items = useGameStore(state => state.items)
  const equipment = useGameStore(state => state.equipment)
  const marketStats = useGameStore(state => state.marketStats)

  // 模拟配置状态
  const [allies, setAllies] = useState<SimulatorCombatantConfig[]>([])
  const [selectedMapId, setSelectedMapId] = useState<string>('all')
  const [selectedNodeIds, setSelectedNodeIds] = useState<string[]>([])
  const [durationSeconds, setDurationSeconds] = useState<number>(3600) // 默认 1 小时
  const [difficulties, setDifficulties] = useState<number[]>([0])

  // 运行与结果状态
  const [isSimulating, setIsSimulating] = useState<boolean>(false)
  const [multiReport, setMultiReport] = useState<MultiNodeSimulatorReport | null>(null)
  const [activeTab, setActiveTab] = useState<'config' | 'results'>('config')
  const [activeResultGroup, setActiveResultGroup] = useState<'comparison' | 'details'>('comparison')
  const [activeNodeId, setActiveNodeId] = useState<string>('')
  const [activeItemKey, setActiveItemKey] = useState<string>('')

  // 全图节点建筑索引（推演非当前地点时必须带上该地点的建筑加成，否则多地点对比失真）。
  // 当前节点优先用服务端权威投影 world.buildingEffects（含拥挤衰减），其余节点按索引 + 建筑目录聚合。
  const [nodeBuildingIndex, setNodeBuildingIndex] = useState<Record<string, MapNodeBuilding[]>>({})
  useEffect(() => {
    if (!open) return
    let cancelled = false
    void fetchNodeBuildingIndex()
      .then(index => { if (!cancelled) setNodeBuildingIndex(index) })
      .catch(() => undefined)
    return () => { cancelled = true }
  }, [open])

  // 首次打开或初始化时自动导入玩家自身与当前地点
  useEffect(() => {
    if (!open) return

    // 默认参战者（玩家本人：属性 + 技能 + 装备 + 消耗品）
    if (allies.length === 0) {
      const initialSelf: SimulatorCombatantConfig = {
        id: 'self',
        name: myName || t('combat.simulator.selfName'),
        isSelf: true,
        formationIndex: 1,
        attributes: myAttributes ? sanitizeAttributes(myAttributes) : {
          maxHitpoints: 300,
          maxManapoints: 100,
          physicalAttack: 30,
          spellAttack: 20,
          armor: 15,
          magicResistance: 10,
          accuracy: 20,
          evasion: 10,
          critical: 10,
          criticalResistance: 10,
          criticalDamagePoints: 50,
          breakPower: 10,
          parryResolve: 10,
          hpRegenPerFiveSeconds: 5,
          mpRegenPerFiveSeconds: 3,
          attackIntervalMilliseconds: 1000,
          physicalDamageBonusPoints: 0,
          spellDamageBonusPoints: 0,
          metalDamageBonusPoints: 0,
          woodDamageBonusPoints: 0,
          waterDamageBonusPoints: 0,
          fireDamageBonusPoints: 0,
          earthDamageBonusPoints: 0,
          totalDamageBonusPoints: 0,
          basicAttackBonusPoints: 0,
          skillBonusPoints: 0,
          physicalDamageReductionPoints: 0,
          spellDamageReductionPoints: 0,
          metalDamageReductionPoints: 0,
          woodDamageReductionPoints: 0,
          waterDamageReductionPoints: 0,
          fireDamageReductionPoints: 0,
          earthDamageReductionPoints: 0,
          damageAbsorptionPoints: 0,
          fixedDamageBonus: 0,
          fixedDamageReduction: 0,
          fixedPhysicalDamageBonus: 0,
          fixedPhysicalDamageReduction: 0,
          fixedSpellDamageBonus: 0,
          fixedSpellDamageReduction: 0,
          attackSpeedPoints: 0,
          actionSpeedPoints: 0,
          cooldownReductionPoints: 0,
          threatBonusPoints: 0,
          hpDrainPoints: 0,
          mpDrainPoints: 0,
          damageReflectPoints: 0,
          healingBonusPoints: 0,
        },
        abilitySlots: myCombat?.abilitySlots ? myCombat.abilitySlots.map(s => ({ ...s })) : [],
        abilityTriggers: myCombat?.abilityTriggers ? { ...myCombat.abilityTriggers } : {},
        consumableTriggers: myCombat?.consumableTriggers ? { ...myCombat.consumableTriggers } : {},
        equipment: myCombat?.equipment ? myCombat.equipment.map(e => ({ ...e })) : [],
        consumables: myConsumables ? myConsumables.map(c => ({ ...c })) : [],
      }
      setAllies([initialSelf])
    }

    // 默认勾选当前地点（或第一个可用地点）
    if (selectedNodeIds.length === 0) {
      const defaultId = currentNodeId || maps[0]?.nodes?.[0]?.nodeId
      if (defaultId) {
        setSelectedNodeIds([defaultId])
      }
    }
  }, [allies.length, currentNodeId, maps, myAttributes, myCombat, myConsumables, myName, open, selectedNodeIds.length, t])

  // 地点选择交互函数
  const handleToggleNodeId = useCallback((nodeId: string) => {
    setSelectedNodeIds(prev => {
      if (prev.includes(nodeId)) {
        return prev.filter(id => id !== nodeId)
      }
      return [...prev, nodeId]
    })
  }, [])

  const handleSelectAllNodesInMap = useCallback((nodeIds: string[]) => {
    setSelectedNodeIds(prev => {
      const set = new Set(prev)
      for (const id of nodeIds) set.add(id)
      return Array.from(set)
    })
  }, [])

  const handleClearSelectedNodes = useCallback(() => {
    setSelectedNodeIds([])
  }, [])
  // 难度勾选管理（至少保持选中 1 个有效难度）
  const handleToggleDifficulty = useCallback((tier: number) => {
    setDifficulties(prev => {
      if (prev.includes(tier)) {
        if (prev.length <= 1) return prev
        return prev.filter(t => t !== tier)
      }
      return [...prev, tier].sort((a, b) => a - b)
    })
  }, [])

  const handleSelectAllDifficulties = useCallback(() => {
    setDifficulties([0, 1, 2, 3, 4, 5])
  }, [])

  const handleResetDifficulties = useCallback(() => {
    setDifficulties([0])
  }, [])


  // 执行多地点模拟推演
  const handleRunSimulation = useCallback(() => {
    if (selectedNodeIds.length === 0) return
    setIsSimulating(true)

    // 异步执行以让 UI 刷新加载动画
    setTimeout(() => {
      try {
        const reports: Record<string, SimulatorReport> = {}
        const comparison: SimulatorNodeComparisonItem[] = []

        const effectiveDifficulties = difficulties.length > 0 ? difficulties : [0]
        const sortedDifficulties = [...effectiveDifficulties].sort((a, b) => a - b)

        // 为多节点与多选难度分别推演（节点 × 难度 笛卡尔积）
        for (const nodeId of selectedNodeIds) {
          let nodeName = nodeId
          let mapName = ''
          for (const m of maps) {
            const found = m.nodes?.find(n => n.nodeId === nodeId)
            if (found) {
              nodeName = found.name
              mapName = m.name
              break
            }
          }

          for (const diff of sortedDifficulties) {
            const seed = BigInt(Math.floor(Math.random() * 1_000_000_000) + 1)
            // 目标地点的建筑战斗类效果：当前节点用服务端权威投影（含拥挤衰减），
            // 其余节点按全图建筑索引 + 建筑目录按等级聚合（无拥挤上限口径）。
            const nodeEffects: NodeBuildingEffectSet = nodeId === currentNodeId && currentBuildingEffects
              ? currentBuildingEffects
              : aggregateNodeBuildingEffects(nodeBuildingIndex[nodeId], buildingDefinitions)
            const simConfig: SimulatorConfig = {
              nodeId,
              nodeName,
              mapName,
              durationSeconds,
              difficulty: diff,
              seed,
              allies,
              buildingEffects: nodeEffects,
            }

            const rep = ExpeditionSimulator.runSimulation(simConfig, {
              encounters,
              abilities,
              items,
              maps,
              equipment,
              marketStats,
            })

            const itemKey = `${nodeId}#${diff}`
            reports[itemKey] = rep
            if (!reports[nodeId]) {
              reports[nodeId] = rep
            }

            comparison.push({
              itemKey,
              nodeId,
              nodeName,
              mapName,
              difficulty: rep.difficulty,
              encountersPerHour: rep.encountersPerHour,
              winRate: rep.winRate,
              dailyRevenue: rep.dailyDropsRevenue,
              dailyCultivationExp: rep.dailyCultivationExp,
              dailySixDimensionExp: rep.dailySixDimensionExp,
              killsPerHour: Number((rep.totalKills / (rep.totalSimulatedSeconds / 3600)).toFixed(1)),
              deathsPerHour: rep.playerDeathsPerHour,
              maxWave: rep.maxWave,
              buildingEffects: nodeEffects,
              report: rep,
              avgDurationSeconds: rep.durationBreakdown.overallAverageSeconds,
            })
          }
        }

        // 默认按每日灵石收益降序排列
        comparison.sort((a, b) => b.dailyRevenue - a.dailyRevenue || b.dailyCultivationExp - a.dailyCultivationExp)

        const topItem = comparison[0]
        const multi: MultiNodeSimulatorReport = {
          comparison,
          reports,
          selectedNodeId: topItem?.nodeId || selectedNodeIds[0],
          selectedItemKey: topItem?.itemKey || `${selectedNodeIds[0]}#${sortedDifficulties[0]}`,
        }

        setMultiReport(multi)
        setActiveNodeId(multi.selectedNodeId)
        setActiveItemKey(multi.selectedItemKey || '')
        setActiveTab('results')
      } finally {
        setIsSimulating(false)
      }
    }, 50)
  }, [abilities, allies, buildingDefinitions, currentBuildingEffects, currentNodeId, difficulties, durationSeconds, encounters, equipment, items, maps, marketStats, nodeBuildingIndex, selectedNodeIds])

  if (!open) return null

  return (
    <Modal
      open={open}
      labelledBy="sim-modal-title"
      onClose={onClose}
      size="large"
      className="sim-modal"
    >
      <div className="sim-modal-header">
        <div className="sim-modal-title-wrap">
          <StatGlyph glyph="attack" className="sim-title-icon" size={20} />
          <h2 id="sim-modal-title" className="sim-title-text">
            {t('combat.simulator.modalTitle')}
          </h2>
        </div>

        {/* 顶部标签切换（配置 vs 结果） */}
        <div className="sim-main-tabs">
          <button
            type="button"
            className={`sim-main-tab ${activeTab === 'config' ? 'is-active' : ''}`}
            onClick={() => setActiveTab('config')}
          >
            {t('combat.simulator.tabConfig')}
          </button>
          <button
            type="button"
            className={`sim-main-tab ${activeTab === 'results' ? 'is-active' : ''}`}
            onClick={() => setActiveTab('results')}
          >
            {t('combat.simulator.tabResults')}
            {multiReport && <span className="sim-tab-dot" />}
          </button>
        </div>
      </div>

      <div className="sim-modal-body">
        {activeTab === 'config' ? (
          <div className="sim-config-layout">
            <div className="sim-left-column">
              <SimulatorPartyEditor
                allies={allies}
                onChangeAllies={setAllies}
                abilities={abilities}
              />
            </div>
            <div className="sim-right-column">
              <SimulatorConfigPanel
                selectedMapId={selectedMapId}
                onChangeMapId={setSelectedMapId}
                selectedNodeIds={selectedNodeIds}
                onToggleNodeId={handleToggleNodeId}
                onSelectAllNodesInMap={handleSelectAllNodesInMap}
                onClearSelectedNodes={handleClearSelectedNodes}
                durationSeconds={durationSeconds}
                onChangeDuration={setDurationSeconds}
                difficulties={difficulties}
                onToggleDifficulty={handleToggleDifficulty}
                onSelectAllDifficulties={handleSelectAllDifficulties}
                onResetDifficulties={handleResetDifficulties}
                onRunSimulation={handleRunSimulation}
                isSimulating={isSimulating}
              />
            </div>
          </div>
        ) : (
          <div className="sim-results-layout">
            <SimulatorResultsView
              multiReport={multiReport}
              activeResultGroup={activeResultGroup}
              onChangeResultGroup={setActiveResultGroup}
              activeNodeId={activeNodeId}
              onChangeActiveNodeId={setActiveNodeId}
              activeItemKey={activeItemKey}
              onChangeActiveItemKey={setActiveItemKey}
            />
          </div>
        )}
      </div>
    </Modal>
  )
})
