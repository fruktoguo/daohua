import { memo, useCallback, useEffect, useState } from 'react'
import { Modal } from '../Modal'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { sanitizeAttributes, type SimulatorCombatantConfig, type SimulatorConfig, type SimulatorReport } from '../../game/simulator/simulatorTypes'
import { ExpeditionSimulator } from '../../game/simulator/expeditionSimulator'
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
  const currentNodeId = useGameStore(state => state.world?.nodeId)
  const maps = useGameStore(state => state.maps)
  const encounters = useGameStore(state => state.encounters)
  const abilities = useGameStore(state => state.abilities)
  const items = useGameStore(state => state.items)

  // 模拟配置状态
  const [allies, setAllies] = useState<SimulatorCombatantConfig[]>([])
  const [selectedNodeId, setSelectedNodeId] = useState<string>('')
  const [selectedNodeName, setSelectedNodeName] = useState<string>('')
  const [selectedEncounterId, setSelectedEncounterId] = useState<string | null>(null)
  const [durationSeconds, setDurationSeconds] = useState<number>(3600) // 默认 1 小时
  const [seed, setSeed] = useState<bigint>(123456789n)

  // 运行与结果状态
  const [isSimulating, setIsSimulating] = useState<boolean>(false)
  const [report, setReport] = useState<SimulatorReport | null>(null)
  const [activeTab, setActiveTab] = useState<'config' | 'results'>('config')

  // 首次打开或初始化时自动导入玩家自身与当前地点
  useEffect(() => {
    if (!open) return

    // 默认参战者（玩家本人）
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
        abilitySlots: myCombat?.abilitySlots ? [...myCombat.abilitySlots] : [],
        abilityTriggers: myCombat?.abilityTriggers ? { ...myCombat.abilityTriggers } : {},
      }
      setAllies([initialSelf])
    }

    // 默认地点
    if (!selectedNodeId) {
      let foundName = ''
      for (const map of maps) {
        const found = map.nodes?.find(n => n.nodeId === currentNodeId)
        if (found) {
          foundName = found.name
          break
        }
      }
      setSelectedNodeId(currentNodeId || maps[0]?.nodes?.[0]?.nodeId || '')
      setSelectedNodeName(foundName || maps[0]?.nodes?.[0]?.name || '')
    }

    // 随机一个种子
    setSeed(BigInt(Math.floor(Math.random() * 1_000_000_000) + 1))
  }, [allies.length, currentNodeId, maps, myAttributes, myCombat, myName, open, selectedNodeId, t])

  // 执行模拟
  const handleRunSimulation = useCallback(() => {
    setIsSimulating(true)

    // 异步执行以让 UI 刷新加载动画
    setTimeout(() => {
      try {
        const simConfig: SimulatorConfig = {
          nodeId: selectedNodeId,
          nodeName: selectedNodeName,
          encounterId: selectedEncounterId,
          durationSeconds,
          seed,
          allies,
        }

        const simReport = ExpeditionSimulator.runSimulation(simConfig, {
          encounters,
          abilities,
          items,
        })

        setReport(simReport)
        setActiveTab('results')
      } finally {
        setIsSimulating(false)
      }
    }, 50)
  }, [abilities, allies, durationSeconds, encounters, items, seed, selectedEncounterId, selectedNodeId, selectedNodeName])

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
          <span className="sim-title-icon">⚔️</span>
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
            {report && <span className="sim-tab-dot" />}
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
                selectedNodeId={selectedNodeId}
                onChangeNodeId={(id, name) => {
                  setSelectedNodeId(id)
                  setSelectedNodeName(name)
                }}
                selectedEncounterId={selectedEncounterId}
                onChangeEncounterId={setSelectedEncounterId}
                durationSeconds={durationSeconds}
                onChangeDuration={setDurationSeconds}
                seed={seed}
                onChangeSeed={setSeed}
                onRunSimulation={handleRunSimulation}
                isSimulating={isSimulating}
              />
            </div>
          </div>
        ) : (
          <div className="sim-results-layout">
            <SimulatorResultsView report={report} />
          </div>
        )}
      </div>
    </Modal>
  )
})
