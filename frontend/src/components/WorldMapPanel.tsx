import { memo, useEffect, useMemo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { gameClient } from '../network/gameClient'

function getEdgeDistance(
  from: { x: number; y: number },
  to: { x: number; y: number },
): number {
  return Math.hypot(to.x - from.x, to.y - from.y)
}

export const WorldMapPanel = memo(function WorldMapPanel(): React.JSX.Element {
  const maps = useGameStore(state => state.maps)
  const world = useGameStore(state => state.world)
  const actions = useGameStore(state => state.actions)
  const encounters = useGameStore(state => state.encounters)
  const queue = useGameStore(state => state.queue.entries)
  const map = maps.find(item => item.mapId === world.mapId)
  const activeTravel = queue.find(entry => entry.executionKind === 'travel')
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(world.nodeId)

  useEffect(() => {
    if (!map) return
    if (!selectedNodeId || !map.nodes.some(node => node.nodeId === selectedNodeId)) {
      setSelectedNodeId(world.nodeId ?? map.nodes[0]?.nodeId ?? null)
    }
  }, [map, selectedNodeId, world.nodeId])

  const selectedNode = map?.nodes.find(node => node.nodeId === selectedNodeId)
  const plannedEdges = useMemo(() => new Set(activeTravel?.travelPathEdgeIds ?? []), [activeTravel?.travelPathEdgeIds])
  if (!map) {
    return <div className="world-map-loading">正在读取世界地图...</div>
  }

  const selectedActions = selectedNode?.availableActionIds
    .map(actionId => actions.find(action => action.id === actionId)?.name ?? actionId) ?? []
  const selectedEncounters = selectedNode?.encounterIds
    .map(encounterId => encounters.find(encounter => encounter.encounterId === encounterId)?.name ?? encounterId) ?? []
  const atSelectedNode = selectedNode?.nodeId === world.nodeId
  const travellingToSelected = selectedNode?.nodeId === activeTravel?.travelDestinationNodeId

  return (
    <div className="world-map-layout">
      <section className="world-map-stage" aria-label={`${map.name}地图`}>
        <svg className="world-map-edges" preserveAspectRatio="none" viewBox="0 0 100 100">
          {map.edges.map(edge => {
            const from = map.nodes.find(node => node.nodeId === edge.fromNodeId)
            const to = map.nodes.find(node => node.nodeId === edge.toNodeId)
            if (!from || !to) return null
            const distance = getEdgeDistance(from, to)
            return (
              <g
                aria-label={`${from.name}至${to.name}，${distance.toFixed(1)}里`}
                className={plannedEdges.has(edge.edgeId) ? 'is-planned' : ''}
                key={edge.edgeId}
              >
                <line className="world-map-edge-hitbox" x1={from.x} x2={to.x} y1={from.y} y2={to.y} />
                <line className="world-map-edge-line" x1={from.x} x2={to.x} y1={from.y} y2={to.y} />
                <text x={(from.x + to.x) / 2} y={(from.y + to.y) / 2}>{distance.toFixed(1)}里</text>
              </g>
            )
          })}
        </svg>
        {map.nodes.map(node => (
          <button
            aria-label={`${node.name}，${node.description}`}
            aria-pressed={selectedNodeId === node.nodeId}
            className={`world-map-node${node.nodeId === world.nodeId ? ' is-current' : ''}${selectedNodeId === node.nodeId ? ' is-selected' : ''}`}
            key={node.nodeId}
            onClick={() => setSelectedNodeId(node.nodeId)}
            style={{ left: `${node.x}%`, top: `${node.y}%` }}
            type="button"
          >
            <strong>{node.name}</strong>
          </button>
        ))}
        <span
          aria-label={`玩家当前位置：${world.locationName}`}
          className={`world-player-marker${world.nodeId ? ' is-at-node' : ' is-travelling'}`}
          style={{ left: `${world.x}%`, top: `${world.y}%` }}
          title={world.locationName}
        ><b>修</b></span>
      </section>

      <aside className="world-location-panel">
        <div className="world-location-current">
          <span>当前位置</span>
          <strong>{world.locationName}</strong>
          {!world.nodeId && <small>移动中 · 停止后会保留在线段当前位置</small>}
        </div>
        {selectedNode ? (
          <>
            <header>
              <span>地图节点</span>
              <h3>{selectedNode.name}</h3>
              <p>{selectedNode.description}</p>
            </header>
            <dl className="world-location-stats">
              <div><dt>修炼效率</dt><dd>{Math.round(selectedNode.cultivationMultiplier * 100)}%</dd></div>
              <div><dt>可用行动</dt><dd>{selectedActions.length}</dd></div>
              <div><dt>妖兽集合</dt><dd>{selectedEncounters.length}</dd></div>
            </dl>
            <div className="world-location-tags">
              {selectedActions.map(name => <span key={name}>{name}</span>)}
              {selectedEncounters.map(name => <span className="is-danger" key={name}>{name}</span>)}
              {selectedActions.length === 0 && selectedEncounters.length === 0 && <span>暂无已开放行动</span>}
            </div>
            <div className="world-location-actions">
              <button
                disabled={atSelectedNode || travellingToSelected}
                onClick={() => gameClient.sendCommand('travel_to', { destinationNodeId: selectedNode.nodeId })}
                type="button"
              >{atSelectedNode ? '已在此地' : travellingToSelected ? '正在前往' : '规划路径并前往'}</button>
              {activeTravel && (
                <button
                  className="danger"
                  onClick={() => gameClient.sendCommand('remove_action', { entryId: activeTravel.entryId })}
                  type="button"
                >停止移动</button>
              )}
            </div>
          </>
        ) : <p>选择一个地图节点查看详情。</p>}
      </aside>
    </div>
  )
})
