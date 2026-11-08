import { memo, useMemo } from 'react'
import { useGameStore } from '../../game/gameStore'
import { useT } from '../../i18n'
import type { WorldNodeDefinition } from '../../game/types'

interface SimulatorConfigPanelProps {
  selectedMapId: string
  onChangeMapId: (mapId: string) => void
  selectedNodeIds: string[]
  onToggleNodeId: (nodeId: string) => void
  onSelectAllNodesInMap: (nodeIds: string[]) => void
  onClearSelectedNodes: () => void
  durationSeconds: number
  onChangeDuration: (seconds: number) => void
  difficulties: number[]
  onToggleDifficulty: (tier: number) => void
  onSelectAllDifficulties: () => void
  onResetDifficulties: () => void
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
const DIFFICULTY_OPTIONS = [
  { tier: 0, label: '默认' },
  { tier: 1, label: 'T1' },
  { tier: 2, label: 'T2' },
  { tier: 3, label: 'T3' },
  { tier: 4, label: 'T4' },
  { tier: 5, label: 'T5' },
] as const

function getDifficultySummary(tier: number): string {
  if (tier <= 0) {
    return '妖兽属性 1.0x 基准 · 标准修为与掉落'
  }
  const attrMult = Math.pow(1.25, tier).toFixed(2)
  const expBonus = Math.round(tier * 60)
  const dropBonus = Math.round(tier * 25)
  return `妖兽全属性 ${attrMult}x (向上取整) · 击杀经验 +${expBonus}% · 掉落率与保底量 +${dropBonus}%`
}

function getDifficultiesSummary(diffs: number[]): string {
  if (diffs.length === 0) {
    return '未选定难度，将默认推演基准难度'
  }
  if (diffs.length === 1) {
    return getDifficultySummary(diffs[0])
  }
  if (diffs.length === DIFFICULTY_OPTIONS.length) {
    return '已选定全部 6 档历练难度（默认至 T5），推演时将生成全景梯度横向跑分对比'
  }
  const sorted = [...diffs].sort((a, b) => a - b)
  const labels = sorted.map(d => (d === 0 ? '默认' : `T${d}`)).join(' / ')
  return `已选定 ${diffs.length} 个难度（${labels}），将对所有选定地点同时进行全套难度挂机对比`
}

export const SimulatorConfigPanel = memo(function SimulatorConfigPanel({
  selectedMapId,
  onChangeMapId,
  selectedNodeIds,
  onToggleNodeId,
  onSelectAllNodesInMap,
  onClearSelectedNodes,
  durationSeconds,
  onChangeDuration,
  difficulties,
  onToggleDifficulty,
  onSelectAllDifficulties,
  onResetDifficulties,
  onRunSimulation,
  isSimulating,
}: SimulatorConfigPanelProps): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)

  // 1. 获取当前区域下的所有节点
  const currentNodes = useMemo(() => {
    if (!selectedMapId || selectedMapId === 'all') {
      const list: Array<{ mapId: string; mapName: string; node: WorldNodeDefinition }> = []
      for (const map of maps) {
        for (const node of map.nodes || []) {
          list.push({ mapId: map.mapId, mapName: map.name, node })
        }
      }
      return list
    }
    const map = maps.find(m => m.mapId === selectedMapId)
    if (!map) return []
    return (map.nodes || []).map(node => ({
      mapId: map.mapId,
      mapName: map.name,
      node,
    }))
  }, [maps, selectedMapId])

  const selectedNodeSet = useMemo(() => new Set(selectedNodeIds), [selectedNodeIds])
  const difficultySet = useMemo(() => new Set(difficulties), [difficulties])


  return (
    <div className="sim-config-panel">
      {/* 1. 地图区域与节点二级多选 */}
      <div className="sim-config-section">
        <div className="sim-config-title-row">
          <div className="sim-config-title">大地图区域与挂机地点选择</div>
          <span className="sim-selection-count-badge">
            已选 <strong>{selectedNodeIds.length}</strong> 个节点
          </span>
        </div>

        {/* 区域选择下拉 */}
        <div className="sim-config-row">
          <label className="sim-config-field">
            <span className="sim-field-label">所属区域:</span>
            <select
              className="sim-select"
              value={selectedMapId}
              onChange={e => onChangeMapId(e.target.value)}
            >
              <option value="all">全部区域 (全地图总览)</option>
              {maps.map(m => (
                <option key={m.mapId} value={m.mapId}>
                  {m.name} ({m.nodes?.length || 0} 个地点)
                </option>
              ))}
            </select>
          </label>
        </div>

        {/* 快捷批量操作按钮 */}
        <div className="sim-node-batch-actions">
          <button
            type="button"
            className="sim-batch-btn"
            onClick={() => onSelectAllNodesInMap(currentNodes.map(item => item.node.nodeId))}
          >
            全选当前区域 ({currentNodes.length})
          </button>
          <button
            type="button"
            className="sim-batch-btn sim-batch-btn--clear"
            onClick={onClearSelectedNodes}
          >
            清空已选
          </button>
        </div>

        {/* 节点多选列表 */}
        <div className="sim-node-multiselect-container">
          {currentNodes.length === 0 ? (
            <div className="sim-no-nodes">当前区域暂无可探查节点</div>
          ) : (
            <div className="sim-node-cards-grid">
              {currentNodes.map(item => {
                const isSelected = selectedNodeSet.has(item.node.nodeId)
                const monsterCount = item.node.monsters?.length || item.node.encounterIds?.length || 0
                return (
                  <div
                    key={item.node.nodeId}
                    role="button"
                    tabIndex={0}
                    className={`sim-node-card ${isSelected ? 'is-selected' : ''}`}
                    onClick={() => onToggleNodeId(item.node.nodeId)}
                    onKeyDown={e => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault()
                        onToggleNodeId(item.node.nodeId)
                      }
                    }}
                  >
                    <div className="sim-node-card-header">
                      <input
                        type="checkbox"
                        checked={isSelected}
                        onChange={() => {}} // 由父级卡片点击事件统一托管
                        className="sim-node-checkbox"
                        tabIndex={-1}
                      />
                      <span className="sim-node-name">{item.node.name}</span>
                    </div>
                    <div className="sim-node-card-meta">
                      <span className="sim-node-region">[{item.mapName}]</span>
                      <span className="sim-node-monsters">
                        {monsterCount > 0 ? `${monsterCount} 种妖兽` : '历练点'}
                      </span>
                    </div>
                  </div>
                )
              })}
            </div>
          )}
        </div>
      </div>

      {/* 2. 讨伐历练难度复选 */}
      <div className="sim-config-section">
        <div className="sim-config-title-row">
          <div className="sim-config-title">{t('app.action.difficulty')}</div>
          <span className="sim-selection-count-badge">
            已选 <strong>{difficulties.length}</strong> 个难度
          </span>
        </div>

        {/* 难度快捷批量操作按钮 */}
        <div className="sim-node-batch-actions">
          <button
            type="button"
            className="sim-batch-btn"
            onClick={onSelectAllDifficulties}
          >
            全选难度 ({DIFFICULTY_OPTIONS.length})
          </button>
          <button
            type="button"
            className="sim-batch-btn sim-batch-btn--clear"
            onClick={onResetDifficulties}
          >
            仅默认
          </button>
        </div>

        <div className="sim-difficulty-checkbox-grid">
          {DIFFICULTY_OPTIONS.map(opt => {
            const isChecked = difficultySet.has(opt.tier)
            return (
              <label
                key={opt.tier}
                className={`sim-difficulty-checkbox-card ${isChecked ? 'is-checked' : ''}`}
              >
                <input
                  type="checkbox"
                  className="sim-difficulty-checkbox"
                  checked={isChecked}
                  onChange={() => onToggleDifficulty(opt.tier)}
                />
                <span className="sim-difficulty-card-label">{opt.label}</span>
                <span className="sim-difficulty-card-sub">
                  {opt.tier === 0 ? '基准' : `+${opt.tier * 60}%经验`}
                </span>
              </label>
            )
          })}
        </div>
        <div className="sim-difficulty-hint">
          {getDifficultiesSummary(difficulties)}
        </div>
      </div>

      {/* 2. 模拟时长指定 */}
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
            min={0.1}
            max={720}
            step={0.5}
            className="sim-input sim-input--short"
            value={Number((durationSeconds / 3600).toFixed(2))}
            onChange={e => onChangeDuration(Math.max(60, Math.round(Number(e.target.value) * 3600)))}
          />
          <span className="sim-unit">{t('combat.simulator.hoursUnit')}</span>
          <span className="sim-seconds-hint">({durationSeconds} {t('combat.simulator.secondsUnit')})</span>
        </div>
      </div>

      {/* 3. 开始模拟按钮 */}
      <div className="sim-action-area">
        <button
          type="button"
          className="sim-run-button"
          disabled={isSimulating || selectedNodeIds.length === 0}
          onClick={onRunSimulation}
        >
          {isSimulating
            ? '正在高速推演中...'
            : selectedNodeIds.length > 1
              ? `开始多地点横向对比 (${selectedNodeIds.length} 个节点)`
              : selectedNodeIds.length === 1
                ? '开始战斗模拟推演'
                : '请至少勾选一个地图节点'}
        </button>
      </div>
    </div>
  )
})
