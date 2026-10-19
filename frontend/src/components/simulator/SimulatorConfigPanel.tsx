import { memo, useMemo } from 'react'
import { useGameStore } from '../../game/gameStore'
import { useT } from '../../i18n'
import type { WorldNodeDefinition } from '../../game/types'

interface SimulatorConfigPanelProps {
  selectedNodeId: string
  onChangeNodeId: (nodeId: string, nodeName: string) => void
  selectedEncounterId: string | null
  onChangeEncounterId: (encounterId: string | null) => void
  durationSeconds: number
  onChangeDuration: (seconds: number) => void
  seed: bigint
  onChangeSeed: (seed: bigint) => void
  onRunSimulation: () => void
  isSimulating: boolean
}

const DURATION_PRESETS = [
  { label: '10分钟', seconds: 600 },
  { label: '30分钟', seconds: 1800 },
  { label: '1小时', seconds: 3600 },
  { label: '4小时', seconds: 14400 },
  { label: '8小时', seconds: 28800 },
  { label: '24小时', seconds: 86400 },
]

export const SimulatorConfigPanel = memo(function SimulatorConfigPanel({
  selectedNodeId,
  onChangeNodeId,
  selectedEncounterId,
  onChangeEncounterId,
  durationSeconds,
  onChangeDuration,
  seed,
  onChangeSeed,
  onRunSimulation,
  isSimulating,
}: SimulatorConfigPanelProps): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)
  const encounters = useGameStore(state => state.encounters)

  // 1. 扁平化提取全地图所有节点
  const allNodes = useMemo(() => {
    const list: Array<{ mapName: string; node: WorldNodeDefinition }> = []
    for (const map of maps) {
      for (const node of map.nodes || []) {
        list.push({ mapName: map.name, node })
      }
    }
    return list
  }, [maps])

  // 当前节点对应的遭遇战列表
  const currentNode = useMemo(() => {
    return allNodes.find(item => item.node.nodeId === selectedNodeId)?.node
  }, [allNodes, selectedNodeId])

  const availableEncounters = useMemo(() => {
    if (!currentNode || !currentNode.encounterIds || currentNode.encounterIds.length === 0) {
      return encounters
    }
    const idSet = new Set(currentNode.encounterIds)
    return encounters.filter(enc => idSet.has(enc.encounterId))
  }, [currentNode, encounters])

  return (
    <div className="sim-config-panel">
      <div className="sim-config-section">
        <div className="sim-config-title">{t('combat.simulator.selectMapTitle')}</div>
        <div className="sim-config-row">
          <label className="sim-config-field">
            <span className="sim-field-label">{t('combat.simulator.nodeLabel')}:</span>
            <select
              className="sim-select"
              value={selectedNodeId}
              onChange={e => {
                const nodeItem = allNodes.find(item => item.node.nodeId === e.target.value)
                if (nodeItem) {
                  onChangeNodeId(nodeItem.node.nodeId, nodeItem.node.name)
                }
              }}
            >
              {allNodes.map(item => (
                <option key={item.node.nodeId} value={item.node.nodeId}>
                  [{item.mapName}] {item.node.name}
                </option>
              ))}
            </select>
          </label>

          <label className="sim-config-field">
            <span className="sim-field-label">{t('combat.simulator.encounterLabel')}:</span>
            <select
              className="sim-select"
              value={selectedEncounterId || ''}
              onChange={e => onChangeEncounterId(e.target.value ? e.target.value : null)}
            >
              <option value="">{t('combat.simulator.autoEcology')}</option>
              {availableEncounters.map(enc => (
                <option key={enc.encounterId} value={enc.encounterId}>
                  {enc.name} (Lv.{enc.enemy?.level || 1})
                </option>
              ))}
            </select>
          </label>
        </div>
      </div>

      <div className="sim-config-section">
        <div className="sim-config-title">{t('combat.simulator.durationTitle')}</div>
        <div className="sim-duration-presets">
          {DURATION_PRESETS.map(preset => (
            <button
              key={preset.seconds}
              type="button"
              className={`sim-preset-btn ${durationSeconds === preset.seconds ? 'is-active' : ''}`}
              onClick={() => onChangeDuration(preset.seconds)}
            >
              {preset.label}
            </button>
          ))}
        </div>
        <div className="sim-config-row sim-custom-duration">
          <span className="sim-field-label">{t('combat.simulator.customDuration')}:</span>
          <input
            type="number"
            min={1}
            max={720}
            className="sim-input sim-input--short"
            value={Number((durationSeconds / 3600).toFixed(2))}
            onChange={e => onChangeDuration(Math.max(60, Math.round(Number(e.target.value) * 3600)))}
          />
          <span className="sim-unit">{t('combat.simulator.hoursUnit')}</span>
          <span className="sim-seconds-hint">({durationSeconds} {t('combat.simulator.secondsUnit')})</span>
        </div>
      </div>

      <div className="sim-config-section">
        <div className="sim-config-title">{t('combat.simulator.seedTitle')}</div>
        <div className="sim-config-row">
          <input
            type="text"
            className="sim-input"
            value={seed.toString()}
            onChange={e => {
              try {
                const next = BigInt(e.target.value.replace(/[^0-9]/g, ''))
                onChangeSeed(next)
              } catch {
                // ignore
              }
            }}
          />
          <button
            type="button"
            className="sim-btn sim-btn--secondary"
            onClick={() => onChangeSeed(BigInt(Math.floor(Math.random() * 1_000_000_000) + 1))}
          >
            {t('combat.simulator.randomSeed')}
          </button>
        </div>
      </div>

      <div className="sim-action-area">
        <button
          type="button"
          className="sim-run-button"
          disabled={isSimulating}
          onClick={onRunSimulation}
        >
          {isSimulating ? t('combat.simulator.running') : t('combat.simulator.startSimulation')}
        </button>
      </div>
    </div>
  )
})
