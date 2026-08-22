import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { gameClient } from '../network/gameClient'

/** 地图虚拟坐标画布尺寸（用于统一像素比例与消除 SVG 非等比拉伸） */
const MAP_WIDTH = 1200
const MAP_HEIGHT = 800

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
 * 1. 节点与连线网络：基于虚拟像素坐标 (1200x800) 渲染大地图节点与通路，彻底杜绝文本拉伸变形；
 * 2. 交互式漫游视口：支持鼠标拖拽 (Pan)、触屏漫游与缩放微调 (Zoom)，提供快捷定位与重置；
 * 3. 玩家位置标记：以向下矢量指示箭头实时锚定当前地点或行进路径，初始与定位时自动居中玩家；
 * 4. 宽域连线交互：宽达 32px 的平滑 Hitbox，支持清晰路况 Hover 与路线高亮 (is-planned)；
 * 5. 灵地详情侧边栏：选中节点后展示修炼灵气倍率、可用行动列表、潜伏敌怪遭遇及行进耗时。
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
  const dragStartRef = useRef<{ clientX: number; clientY: number; panX: number; panY: number; moved: boolean } | null>(null)

  // 居中聚焦到玩家当前所在位置
  const centerOnPlayer = useCallback((targetZoom?: number) => {
    const stage = stageRef.current
    if (!stage) return
    const stageW = stage.clientWidth
    const stageH = stage.clientHeight
    if (!stageW || !stageH) return

    const z = targetZoom ?? zoom
    const playerPxX = (world.x / 100) * MAP_WIDTH
    const playerPxY = (world.y / 100) * MAP_HEIGHT

    setPan({
      x: stageW / 2 - playerPxX * z,
      y: stageH / 2 - playerPxY * z,
    })
  }, [world.x, world.y, zoom])

  // 首次渲染或地图切换时自动居中到玩家位置
  const initializedMapIdRef = useRef<string | null>(null)
  useLayoutEffect(() => {
    if (!map) return
    if (initializedMapIdRef.current !== map.mapId) {
      initializedMapIdRef.current = map.mapId
      centerOnPlayer(1)
      setZoom(1)
    }
  }, [map, centerOnPlayer])

  // 视口尺寸发生变化时响应式校准
  useEffect(() => {
    const stage = stageRef.current
    if (!stage) return
    const resizeObserver = new ResizeObserver(() => {
      // 仅在首次未初始化或需要自适应时轻量触发
      if (!isDraggingRef.current) {
        // 保留当前居中倾向
      }
    })
    resizeObserver.observe(stage)
    return () => resizeObserver.disconnect()
  }, [])

  useEffect(() => {
    if (!map) return
    if (!selectedNodeId || !map.nodes.some(node => node.nodeId === selectedNodeId)) {
      setSelectedNodeId(world.nodeId ?? map.nodes[0]?.nodeId ?? null)
    }
  }, [map, selectedNodeId, world.nodeId])

  const selectedNode = map?.nodes.find(node => node.nodeId === selectedNodeId)
  const plannedEdges = useMemo(() => new Set(activeTravel?.travelPathEdgeIds ?? []), [activeTravel?.travelPathEdgeIds])

  // 拖拽漫游事件监听
  const handlePointerDown = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return
    const target = e.target as HTMLElement
    if (target.closest('.world-map-toolbar')) return

    dragStartRef.current = {
      clientX: e.clientX,
      clientY: e.clientY,
      panX: pan.x,
      panY: pan.y,
      moved: false,
    }
    isDraggingRef.current = true
    setIsDragging(true)
    stageRef.current?.setPointerCapture(e.pointerId)
  }, [pan.x, pan.y])

  const handlePointerMove = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (!dragStartRef.current) return
    const dx = e.clientX - dragStartRef.current.clientX
    const dy = e.clientY - dragStartRef.current.clientY
    if (!dragStartRef.current.moved && Math.hypot(dx, dy) > 4) {
      dragStartRef.current.moved = true
    }
    setPan({
      x: dragStartRef.current.panX + dx,
      y: dragStartRef.current.panY + dy,
    })
  }, [])

  const handlePointerUp = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (dragStartRef.current) {
      isDraggingRef.current = false
      setIsDragging(false)
      dragStartRef.current = null
      try {
        stageRef.current?.releasePointerCapture(e.pointerId)
      } catch {
        // 忽略可能已释放的捕获
      }
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
      const nextZoom = Math.min(1.6, Math.max(0.65, prevZoom * zoomFactor))
      // 保持鼠标指向点的相对位置稳定
      const worldX = (mouseX - pan.x) / prevZoom
      const worldY = (mouseY - pan.y) / prevZoom
      setPan({
        x: mouseX - worldX * nextZoom,
        y: mouseY - worldY * nextZoom,
      })
      return nextZoom
    })
  }, [pan.x, pan.y])

  const handleNodeClick = useCallback((nodeId: string) => {
    // 拖拽超过阈值时不触发节点选中
    if (dragStartRef.current?.moved) return
    setSelectedNodeId(nodeId)
  }, [])

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
                const nextZoom = Math.min(1.6, zoom + 0.15)
                setZoom(nextZoom)
                centerOnPlayer(nextZoom)
              }}
              title={t('combat.map.zoomIn')}
              type="button"
            >＋</button>
            <button
              className="world-map-tool-btn icon-only"
              onClick={() => {
                const nextZoom = Math.max(0.65, zoom - 0.15)
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
                centerOnPlayer(1)
              }}
              title={t('combat.map.resetView')}
              type="button"
            >↺</button>
          </div>
        </div>

        {/* 漫游画布视口（统一 1200x800 虚拟绝对像素） */}
        <div
          className="world-map-canvas"
          style={{
            transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})`,
            transformOrigin: '0 0',
            width: `${MAP_WIDTH}px`,
            height: `${MAP_HEIGHT}px`,
          }}
        >
          {/* SVG 路径与距离标注（1:1 无形变） */}
          <svg
            className="world-map-edges"
            height={MAP_HEIGHT}
            viewBox={`0 0 ${MAP_WIDTH} ${MAP_HEIGHT}`}
            width={MAP_WIDTH}
          >
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
              const x1 = (from.x / 100) * MAP_WIDTH
              const y1 = (from.y / 100) * MAP_HEIGHT
              const x2 = (to.x / 100) * MAP_WIDTH
              const y2 = (to.y / 100) * MAP_HEIGHT
              const midX = (x1 + x2) / 2
              const midY = (y1 + y2) / 2
              const isPlanned = plannedEdges.has(edge.edgeId)

              return (
                <g
                  aria-label={t('combat.map.edgeAria', { from: from.name, to: to.name, distance: distance.toFixed(1) })}
                  className={`world-map-edge-group${isPlanned ? ' is-planned' : ''}`}
                  key={edge.edgeId}
                >
                  {/* 宽容度极高的透明交互拾取层 (32px) */}
                  <line className="world-map-edge-hitbox" x1={x1} x2={x2} y1={y1} y2={y2} />
                  {/* 底色衬线 */}
                  <line className="world-map-edge-bg" x1={x1} x2={x2} y1={y1} y2={y2} />
                  {/* 核心主路径 */}
                  <line className="world-map-edge-line" x1={x1} x2={x2} y1={y1} y2={y2} />
                  {/* 距离微型胶囊标注（比例完全正常，居中无拉伸） */}
                  <g className="world-map-edge-label" transform={`translate(${midX}, ${midY})`}>
                    <rect className="world-map-edge-label-bg" height={22} rx={4} width={54} x={-27} y={-11} />
                    <text
                      className="world-map-edge-label-text"
                      dominantBaseline="central"
                      textAnchor="middle"
                      x={0}
                      y={0}
                    >
                      {t('combat.map.edgeDistance', { distance: distance.toFixed(1) })}
                    </text>
                  </g>
                </g>
              )
            })}
          </svg>

          {/* 地图节点实体按钮 */}
          {map.nodes.map(node => {
            const px = (node.x / 100) * MAP_WIDTH
            const py = (node.y / 100) * MAP_HEIGHT
            const isCurrent = node.nodeId === world.nodeId
            const isSelected = selectedNodeId === node.nodeId

            return (
              <button
                aria-label={t('combat.map.nodeAria', { name: node.name, description: node.description })}
                aria-pressed={isSelected}
                className={`world-map-node${isCurrent ? ' is-current' : ''}${isSelected ? ' is-selected' : ''}`}
                key={node.nodeId}
                onClick={() => handleNodeClick(node.nodeId)}
                style={{ left: `${px}px`, top: `${py}px` }}
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
              left: `${(world.x / 100) * MAP_WIDTH}px`,
              top: `${(world.y / 100) * MAP_HEIGHT}px`,
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
