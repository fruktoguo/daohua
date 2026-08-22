import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { gameClient } from '../network/gameClient'

/** 计算地图两节点之间的欧氏几何连线距离 */
function getEdgeDistance(
  from: { x: number; y: number },
  to: { x: number; y: number },
): number {
  return Math.hypot(to.x - from.x, to.y - from.y)
}

// 地图节点/地点名称与描述来自服务端数据，仅面板框架文本与 aria 描述模板走 i18n。

/**
 * 修仙大千世界大地图与游历导航面板 (WorldMapPanel)
 *
 * 核心功能与交互流程：
 * 1. 响应式自适应布局：基于 100% 相对坐标与像素级 SVG 覆盖渲染，杜绝任何字形拉伸变形；
 * 2. 交互式漫游视口：支持鼠标拖拽 (Pan)、触屏漫游与缩放微调 (Zoom)，提供快捷定位与重置；
 * 3. 玩家位置居中：打开地图时默认将玩家当前所在位置（驻留地点或行进路径）对齐至视口中心；
 * 4. 可靠节点交互：区分平移漫游与地点点击，支持一键精准选中地点并查看详情；
 * 5. 宽域连线交互：宽达 28px 的平滑 Hitbox，支持清晰路况 Hover 与路线高亮 (is-planned)。
 */
export const WorldMapPanel = memo(function WorldMapPanel(): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)
  const world = useGameStore(state => state.world)
  const actions = useGameStore(state => state.actions)
  const encounters = useGameStore(state => state.encounters)
  const queue = useGameStore(state => state.queue.entries)
  const map = maps.find(item => item.mapId === world.mapId)
  const activeTravel = queue.find(entry => entry.executionKind === 'travel')
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(world.nodeId)

  // 视口拖拽漫游与缩放状态
  const stageRef = useRef<HTMLDivElement>(null)
  const [pan, setPan] = useState<{ x: number; y: number }>({ x: 0, y: 0 })
  const [zoom, setZoom] = useState<number>(1)
  const [isDragging, setIsDragging] = useState<boolean>(false)
  const isDraggingRef = useRef<boolean>(false)
  const dragStartRef = useRef<{
    clientX: number
    clientY: number
    panX: number
    panY: number
    moved: boolean
    targetNodeId: string | null
  } | null>(null)

  // 居中聚焦到玩家当前所在位置（在节点驻留时居中地点，在移动时居中路径当前坐标）
  const centerOnPlayer = useCallback((targetZoom?: number) => {
    const stage = stageRef.current
    if (!stage) return
    const stageW = stage.clientWidth
    const stageH = stage.clientHeight
    if (!stageW || !stageH) return

    const z = targetZoom ?? zoom
    const playerPxX = (world.x / 100) * stageW
    const playerPxY = (world.y / 100) * stageH

    setPan({
      x: Math.round(stageW / 2 - playerPxX * z),
      y: Math.round(stageH / 2 - playerPxY * z),
    })
  }, [world.x, world.y, zoom])

  // 打开地图或视口初始化时，默认将玩家当前位置居中
  const hasAutoCenteredRef = useRef<boolean>(false)
  useEffect(() => {
    const stage = stageRef.current
    if (!stage) return

    const tryCenter = () => {
      const stageW = stage.clientWidth
      const stageH = stage.clientHeight
      if (stageW > 0 && stageH > 0 && !hasAutoCenteredRef.current) {
        centerOnPlayer(zoom)
        hasAutoCenteredRef.current = true
      }
    }

    tryCenter()
    const rafId = requestAnimationFrame(tryCenter)
    const resizeObserver = new ResizeObserver(entries => {
      for (const entry of entries) {
        if (entry.contentRect.width > 0 && entry.contentRect.height > 0) {
          if (!hasAutoCenteredRef.current) {
            tryCenter()
          }
        }
      }
    })
    resizeObserver.observe(stage)

    return () => {
      cancelAnimationFrame(rafId)
      resizeObserver.disconnect()
    }
  }, [map?.mapId, centerOnPlayer, zoom])

  // 地图切换时重置自动居中标志
  useEffect(() => {
    hasAutoCenteredRef.current = false
  }, [map?.mapId])

  useEffect(() => {
    if (!map) return
    if (!selectedNodeId || !map.nodes.some(node => node.nodeId === selectedNodeId)) {
      setSelectedNodeId(world.nodeId ?? map.nodes[0]?.nodeId ?? null)
    }
  }, [map, selectedNodeId, world.nodeId])

  const selectedNode = map?.nodes.find(node => node.nodeId === selectedNodeId)
  const plannedEdges = useMemo(() => new Set(activeTravel?.travelPathEdgeIds ?? []), [activeTravel?.travelPathEdgeIds])

  // 拖拽漫游事件监听：仅在位移超过 4px 时激活拖拽捕获，防止截断节点点击
  const handlePointerDown = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return
    const target = e.target as HTMLElement
    if (target.closest('.world-map-toolbar')) return

    const nodeEl = target.closest<HTMLElement>('.world-map-node')
    const targetNodeId = nodeEl?.getAttribute('data-node-id') ?? null

    dragStartRef.current = {
      clientX: e.clientX,
      clientY: e.clientY,
      panX: pan.x,
      panY: pan.y,
      moved: false,
      targetNodeId,
    }
    isDraggingRef.current = false
  }, [pan.x, pan.y])

  const handlePointerMove = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (!dragStartRef.current) return
    const dx = e.clientX - dragStartRef.current.clientX
    const dy = e.clientY - dragStartRef.current.clientY
    const dist = Math.hypot(dx, dy)

    if (!dragStartRef.current.moved) {
      if (dist > 4) {
        dragStartRef.current.moved = true
        isDraggingRef.current = true
        setIsDragging(true)
        try {
          stageRef.current?.setPointerCapture(e.pointerId)
        } catch {
          // 忽略可能失败的指针捕获
        }
      } else {
        return
      }
    }

    setPan({
      x: dragStartRef.current.panX + dx,
      y: dragStartRef.current.panY + dy,
    })
  }, [])

  const handlePointerUp = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (!dragStartRef.current) return
    const { moved, targetNodeId } = dragStartRef.current
    dragStartRef.current = null
    isDraggingRef.current = false
    setIsDragging(false)

    try {
      stageRef.current?.releasePointerCapture(e.pointerId)
    } catch {
      // 忽略可能已释放的捕获
    }

    // 若未发生拖拽且点击在节点上，确保选中该节点
    if (!moved && targetNodeId) {
      setSelectedNodeId(targetNodeId)
    }
  }, [])

  // 滚轮缩放支持
  const handleWheel = useCallback((e: React.WheelEvent<HTMLDivElement>) => {
    e.preventDefault()
    const stage = stageRef.current
    if (!stage) return
    const rect = stage.getBoundingClientRect()
    const mouseX = e.clientX - rect.left
    const mouseY = e.clientY - rect.top

    const zoomFactor = e.deltaY < 0 ? 1.12 : 0.89
    setZoom(prevZoom => {
      const nextZoom = Math.min(1.8, Math.max(0.6, prevZoom * zoomFactor))
      const worldX = (mouseX - pan.x) / prevZoom
      const worldY = (mouseY - pan.y) / prevZoom
      setPan({
        x: mouseX - worldX * nextZoom,
        y: mouseY - worldY * nextZoom,
      })
      return nextZoom
    })
  }, [pan.x, pan.y])

  if (!map) {
    return <div className="world-map-loading">{t('combat.map.loading')}</div>
  }

  const selectedActions = selectedNode?.availableActionIds
    .map(actionId => actions.find(action => action.id === actionId)?.name ?? actionId) ?? []
  const selectedEncounters = selectedNode?.encounterIds
    .map(encounterId => encounters.find(encounter => encounter.encounterId === encounterId)?.name ?? encounterId) ?? []
  const atSelectedNode = selectedNode?.nodeId === world.nodeId
  const travellingToSelected = selectedNode?.nodeId === activeTravel?.travelDestinationNodeId

  return (
    <div className="world-map-layout">
      <section
        aria-label={t('combat.map.stageAria', { name: map.name })}
        className={`world-map-stage${isDragging ? ' is-dragging' : ''}`}
        onPointerCancel={handlePointerUp}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        onWheel={handleWheel}
        ref={stageRef}
      >
        {/* 浮动操作快捷栏 */}
        <div className="world-map-toolbar" role="toolbar">
          <button
            className="world-map-tool-btn"
            onClick={() => centerOnPlayer(zoom)}
            title={t('combat.map.centerPlayer')}
            type="button"
          >
            <span className="tool-icon">◎</span>
            <span>{t('combat.map.centerPlayer')}</span>
          </button>
          <div className="world-map-zoom-group">
            <button
              className="world-map-tool-btn icon-only"
              onClick={() => {
                const nextZoom = Math.min(1.8, zoom + 0.15)
                setZoom(nextZoom)
                centerOnPlayer(nextZoom)
              }}
              title={t('combat.map.zoomIn')}
              type="button"
            >＋</button>
            <button
              className="world-map-tool-btn icon-only"
              onClick={() => {
                const nextZoom = Math.max(0.6, zoom - 0.15)
                setZoom(nextZoom)
                centerOnPlayer(nextZoom)
              }}
              title={t('combat.map.zoomOut')}
              type="button"
            >－</button>
            <button
              className="world-map-tool-btn icon-only"
              onClick={() => {
                setZoom(1)
                setPan({ x: 0, y: 0 })
              }}
              title={t('combat.map.resetView')}
              type="button"
            >↺</button>
          </div>
        </div>

        {/* 漫游画布视口 */}
        <div
          className="world-map-canvas"
          style={{
            transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})`,
            transformOrigin: '50% 50%',
          }}
        >
          {/* SVG 路径与距离标注（像素渲染，100% 正常字形比例） */}
          <svg className="world-map-edges">
            <defs>
              <filter id="world-route-glow" x="-20%" y="-20%" width="140%" height="140%">
                <feDropShadow dx="0" dy="0" floodColor="#e8d69a" floodOpacity="0.7" stdDeviation="3" />
              </filter>
            </defs>
            {map.edges.map(edge => {
              const from = map.nodes.find(node => node.nodeId === edge.fromNodeId)
              const to = map.nodes.find(node => node.nodeId === edge.toNodeId)
              if (!from || !to) return null
              const distance = getEdgeDistance(from, to)
              const isPlanned = plannedEdges.has(edge.edgeId)
              const midX = (from.x + to.x) / 2
              const midY = (from.y + to.y) / 2

              return (
                <g
                  aria-label={t('combat.map.edgeAria', { from: from.name, to: to.name, distance: distance.toFixed(1) })}
                  className={`world-map-edge-group${isPlanned ? ' is-planned' : ''}`}
                  key={edge.edgeId}
                >
                  {/* 宽容度高的透明交互拾取层 (28px) */}
                  <line className="world-map-edge-hitbox" x1={`${from.x}%`} x2={`${to.x}%`} y1={`${from.y}%`} y2={`${to.y}%`} />
                  {/* 底色衬线 */}
                  <line className="world-map-edge-bg" x1={`${from.x}%`} x2={`${to.x}%`} y1={`${from.y}%`} y2={`${to.y}%`} />
                  {/* 核心主路径 */}
                  <line className="world-map-edge-line" x1={`${from.x}%`} x2={`${to.x}%`} y1={`${from.y}%`} y2={`${to.y}%`} />
                  {/* 距离文字标注（标准字形比例，带黑色轮廓发光） */}
                  <text
                    className="world-map-edge-label-text"
                    dominantBaseline="central"
                    textAnchor="middle"
                    x={`${midX}%`}
                    y={`${midY}%`}
                  >
                    {t('combat.map.edgeDistance', { distance: distance.toFixed(1) })}
                  </text>
                </g>
              )
            })}
          </svg>

          {/* 地图节点实体按钮 */}
          {map.nodes.map(node => {
            const isCurrent = node.nodeId === world.nodeId
            const isSelected = selectedNodeId === node.nodeId

            return (
              <button
                aria-label={t('combat.map.nodeAria', { name: node.name, description: node.description })}
                aria-pressed={isSelected}
                className={`world-map-node${isCurrent ? ' is-current' : ''}${isSelected ? ' is-selected' : ''}`}
                data-node-id={node.nodeId}
                key={node.nodeId}
                onClick={e => {
                  e.stopPropagation()
                  setSelectedNodeId(node.nodeId)
                }}
                style={{ left: `${node.x}%`, top: `${node.y}%` }}
                type="button"
              >
                <strong>{node.name}</strong>
              </button>
            )
          })}

          {/* 玩家当前位置向下箭头指示器 */}
          <div
            aria-label={t('combat.map.playerMarkerAria', { name: world.locationName })}
            className={`world-player-marker${world.nodeId ? ' is-at-node' : ' is-travelling'}`}
            style={{
              left: `${world.x}%`,
              top: `${world.y}%`,
            }}
            title={world.locationName}
          >
            <div className="world-player-marker-anchor">
              <div className="world-player-marker-arrow">
                <svg
                  className="world-player-arrow-svg"
                  height={32}
                  viewBox="0 0 24 32"
                  width={24}
                >
                  {/* 外轮廓护甲底框 */}
                  <path
                    className="player-arrow-body"
                    d="M12 30 L3 16 C1.5 13.5 2 10 4.5 8 L8 5 V2 C8 1.4 8.5 1 9.1 1 H14.9 C15.5 1 16 1.4 16 2 V5 L19.5 8 C22 10 22.5 13.5 21 16 Z"
                  />
                  {/* 向下指引锋芒 */}
                  <path
                    className="player-arrow-symbol"
                    d="M12 24 L6 14 H9.5 V7 H14.5 V14 H18 Z"
                  />
                  {/* 剑尖明光晶核 */}
                  <polygon
                    className="player-arrow-tip"
                    points="12,24 8,17 16,17"
                  />
                </svg>
              </div>
              <div className="world-player-marker-pulse" />
            </div>
          </div>
        </div>
      </section>

      <aside className="world-location-panel">
        <div className="world-location-current">
          <span>{t('combat.map.currentLocation')}</span>
          <strong>{world.locationName}</strong>
          {!world.nodeId && <small>{t('combat.map.movingHint')}</small>}
        </div>
        {selectedNode ? (
          <>
            <header>
              <span>{t('combat.map.nodeHeading')}</span>
              <h3>{selectedNode.name}</h3>
              <p>{selectedNode.description}</p>
            </header>
            <dl className="world-location-stats">
              <div><dt>{t('combat.map.cultivationEfficiency')}</dt><dd>{Math.round(selectedNode.cultivationMultiplier * 100)}%</dd></div>
              <div><dt>{t('combat.map.availableActions')}</dt><dd>{selectedActions.length}</dd></div>
              <div><dt>{t('combat.map.encounterCount')}</dt><dd>{selectedEncounters.length}</dd></div>
            </dl>
            <div className="world-location-tags">
              {selectedActions.map(name => <span key={name}>{name}</span>)}
              {selectedEncounters.map(name => <span className="is-danger" key={name}>{name}</span>)}
              {selectedActions.length === 0 && selectedEncounters.length === 0 && <span>{t('combat.map.noActions')}</span>}
            </div>
            <div className="world-location-actions">
              <button
                disabled={atSelectedNode || travellingToSelected}
                onClick={() => gameClient.sendCommand('travel_to', { destinationNodeId: selectedNode.nodeId })}
                type="button"
              >{atSelectedNode ? t('combat.map.atNode') : travellingToSelected ? t('combat.map.travelling') : t('combat.map.planTravel')}</button>
              {activeTravel && (
                <button
                  className="danger"
                  onClick={() => gameClient.sendCommand('remove_action', { entryId: activeTravel.entryId })}
                  type="button"
                >{t('combat.map.stopTravel')}</button>
              )}
            </div>
          </>
        ) : <p>{t('combat.map.selectPrompt')}</p>}
      </aside>
    </div>
  )
})
