import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { findItemInCatalog } from '../game/ids'
import type { TravelSegment, WorldMapDefinition, WorldState } from '../game/types'
import { useT } from '../i18n'
import { gameClient } from '../network/gameClient'
import { FloatingTooltip } from './FloatingPopover'
import { Modal } from './Modal'
/** 计算地图两节点之间的欧氏几何连线距离 */
function getEdgeDistance(
  from: { x: number; y: number },
  to: { x: number; y: number },
): number {
  return Math.hypot(to.x - from.x, to.y - from.y)
}

/** 路径规划计算结果 */
interface PathPlanResult {
  /** 最短路径总耗时（秒） */
  totalDurationSeconds: number
  /** 途经的节点名称链 */
  pathNodeNames: string[]
  /** 途经的路径边 ID 列表 */
  pathEdgeIds: string[]
}

/**
 * 使用 Dijkstra 算法精确规划当前玩家位置到目标地点的最短路径与耗时
 */
function planShortestTravel(
  map: WorldMapDefinition,
  world: WorldState,
  targetNodeId: string,
): PathPlanResult | null {
  if (!map || !targetNodeId) return null
  const targetNode = map.nodes.find(n => n.nodeId === targetNodeId)
  if (!targetNode) return null

  // 单源 Dijkstra 核心
  const dijkstra = (startId: string) => {
    const dist = new Map<string, number>()
    const prev = new Map<string, { nodeId: string; edgeId: string } | null>()
    const unvisited = new Set<string>()

    for (const node of map.nodes) {
      dist.set(node.nodeId, Infinity)
      prev.set(node.nodeId, null)
      unvisited.add(node.nodeId)
    }
    dist.set(startId, 0)

    while (unvisited.size > 0) {
      let u: string | null = null
      let minDist = Infinity
      for (const node of unvisited) {
        const d = dist.get(node) ?? Infinity
        if (d < minDist) {
          minDist = d
          u = node
        }
      }
      if (u === null || minDist === Infinity) break
      if (u === targetNodeId) break

      unvisited.delete(u)

      for (const edge of map.edges) {
        let v: string | null = null
        if (edge.fromNodeId === u) {
          v = edge.toNodeId
        } else if (edge.toNodeId === u) {
          v = edge.fromNodeId
        }

        if (v && unvisited.has(v)) {
          const alt = minDist + edge.durationSeconds
          if (alt < (dist.get(v) ?? Infinity)) {
            dist.set(v, alt)
            prev.set(v, { nodeId: u, edgeId: edge.edgeId })
          }
        }
      }
    }

    return { dist, prev }
  }

  const buildResult = (
    _startId: string,
    initialDelaySeconds: number,
    prev: Map<string, { nodeId: string; edgeId: string } | null>,
    totalDist: number,
  ): PathPlanResult => {
    const nodeNames: string[] = []
    const edgeIds: string[] = []
    let curr: string | null = targetNodeId

    while (curr !== null) {
      const nodeObj = map.nodes.find(n => n.nodeId === curr)
      if (nodeObj) nodeNames.unshift(nodeObj.name)
      const p = prev.get(curr)
      if (p) {
        edgeIds.unshift(p.edgeId)
        curr = p.nodeId
      } else {
        break
      }
    }

    return {
      totalDurationSeconds: Math.ceil(totalDist + initialDelaySeconds),
      pathNodeNames: nodeNames,
      pathEdgeIds: edgeIds,
    }
  }

  if (world.nodeId) {
    if (world.nodeId === targetNodeId) {
      return { totalDurationSeconds: 0, pathNodeNames: [targetNode.name], pathEdgeIds: [] }
    }
    const { dist, prev } = dijkstra(world.nodeId)
    const targetDist = dist.get(targetNodeId)
    if (targetDist === undefined || targetDist === Infinity) return null
    return buildResult(world.nodeId, 0, prev, targetDist)
  }

  if (world.edgeId) {
    const currentEdge = map.edges.find(e => e.edgeId === world.edgeId)
    if (currentEdge) {
      const progress = Math.max(0, Math.min(1, world.edgeProgress))
      const reverseDelay = currentEdge.durationSeconds * progress
      const res1 = dijkstra(currentEdge.fromNodeId)
      const dist1 = (res1.dist.get(targetNodeId) ?? Infinity) + reverseDelay

      const forwardDelay = currentEdge.durationSeconds * (1 - progress)
      const res2 = dijkstra(currentEdge.toNodeId)
      const dist2 = (res2.dist.get(targetNodeId) ?? Infinity) + forwardDelay

      if (dist2 <= dist1 && dist2 < Infinity) {
        return buildResult(currentEdge.toNodeId, forwardDelay, res2.prev, res2.dist.get(targetNodeId)!)
      } else if (dist1 < Infinity) {
        return buildResult(currentEdge.fromNodeId, reverseDelay, res1.prev, res1.dist.get(targetNodeId)!)
      }
    }
  }

  return null
}
/**
 * 实时计算并插值玩家在地图上的精准动态坐标（DOM Ref 直操位移，零 React Re-render 负担）
 */
function usePlayerDisplayPosition(
  map: WorldMapDefinition | undefined,
  world: WorldState,
  activeTravel: {
    status: string
    progressSeconds: number
    durationSeconds: number
    travelPathEdgeIds?: string[] | null
    travelSegments?: TravelSegment[] | null
  } | undefined,
  hidden = false,
): {
  playerPosRef: React.RefObject<{ x: number; y: number }>
  markerRef: React.RefObject<HTMLDivElement | null>
} {
  const markerRef = useRef<HTMLDivElement>(null)
  const playerPosRef = useRef<{ x: number; y: number }>({ x: world.x, y: world.y })

  const stateRef = useRef({
    map,
    world,
    activeTravel,
    hidden,
    lastProgressSec: activeTravel?.progressSeconds ?? 0,
    lastUpdateMs: performance.now(),
  })

  useEffect(() => {
    const prev = stateRef.current
    const now = performance.now()
    const progressChanged = prev.activeTravel?.progressSeconds !== activeTravel?.progressSeconds
    const travelChanged = prev.activeTravel !== activeTravel
    stateRef.current = {
      map,
      world,
      activeTravel,
      hidden,
      lastProgressSec: activeTravel?.progressSeconds ?? 0,
      lastUpdateMs: progressChanged || travelChanged ? now : prev.lastUpdateMs,
    }
  }, [map, world, activeTravel, hidden])

  useEffect(() => {
    let animId: number | null = null

    const computeCurrentCoord = (): { x: number; y: number } => {
      const { map: currMap, world: currWorld, activeTravel: currTravel, lastProgressSec, lastUpdateMs } = stateRef.current
      if (!currMap) return { x: currWorld.x, y: currWorld.y }

      // 1. 正在移动中：沿着旅行路径平滑插值
      if (
        currTravel &&
        currTravel.status === 'running' &&
        currTravel.durationSeconds > 0
      ) {
        const now = performance.now()
        const elapsedSinceUpdateSec = (now - lastUpdateMs) / 1000
        const currentElapsedSec = Math.min(currTravel.durationSeconds, lastProgressSec + elapsedSinceUpdateSec)

        let remainingSec = currentElapsedSec

        // 优先使用后端权威下发的 travelSegments（精准支持半路启程、逆向行走与多路段长途移动）
        if (currTravel.travelSegments && currTravel.travelSegments.length > 0) {
          for (let i = 0; i < currTravel.travelSegments.length; i++) {
            const seg = currTravel.travelSegments[i]
            const edge = currMap.edges.find(e => e.edgeId === seg.edgeId)
            if (!edge) continue

            const fromNode = currMap.nodes.find(n => n.nodeId === edge.fromNodeId)
            const toNode = currMap.nodes.find(n => n.nodeId === edge.toNodeId)
            if (!fromNode || !toNode) continue

            const startX = fromNode.x + (toNode.x - fromNode.x) * seg.startProgress
            const startY = fromNode.y + (toNode.y - fromNode.y) * seg.startProgress
            const endX = fromNode.x + (toNode.x - fromNode.x) * seg.endProgress
            const endY = fromNode.y + (toNode.y - fromNode.y) * seg.endProgress

            const segDuration = seg.durationSeconds
            if (remainingSec <= segDuration || i === currTravel.travelSegments.length - 1) {
              const ratio = segDuration > 0 ? Math.min(1, Math.max(0, remainingSec / segDuration)) : 1
              return {
                x: startX + (endX - startX) * ratio,
                y: startY + (endY - startY) * ratio,
              }
            } else {
              remainingSec -= segDuration
            }
          }
        } else if (currTravel.travelPathEdgeIds && currTravel.travelPathEdgeIds.length > 0) {
          // 当服务端未展开 segments 时，按权威边列表计算当前路径位置
          let currentNodeId: string | null = currWorld.nodeId
          for (let i = 0; i < currTravel.travelPathEdgeIds.length; i++) {
            const edgeId = currTravel.travelPathEdgeIds[i]
            const edge = currMap.edges.find(e => e.edgeId === edgeId)
            if (!edge) continue

            let fromId = edge.fromNodeId
            let toId = edge.toNodeId
            if (currentNodeId && currentNodeId === edge.toNodeId) {
              fromId = edge.toNodeId
              toId = edge.fromNodeId
            }
            currentNodeId = toId

            const fromNode = currMap.nodes.find(n => n.nodeId === fromId)
            const toNode = currMap.nodes.find(n => n.nodeId === toId)
            if (!fromNode || !toNode) continue

            const edgeDuration = edge.durationSeconds
            if (remainingSec <= edgeDuration || i === currTravel.travelPathEdgeIds.length - 1) {
              const ratio = edgeDuration > 0 ? Math.min(1, Math.max(0, remainingSec / edgeDuration)) : 1
              return {
                x: fromNode.x + (toNode.x - fromNode.x) * ratio,
                y: fromNode.y + (toNode.y - fromNode.y) * ratio,
              }
            } else {
              remainingSec -= edgeDuration
            }
          }
        }
      }

      // 2. 驻留在地点处
      if (currWorld.nodeId) {
        const node = currMap.nodes.find(n => n.nodeId === currWorld.nodeId)
        if (node) {
          return { x: node.x, y: node.y }
        }
        return { x: currWorld.x, y: currWorld.y }
      } else if (currWorld.edgeId) {
        // 处于单线段中间
        const edge = currMap.edges.find(e => e.edgeId === currWorld.edgeId)
        if (edge) {
          const fromNode = currMap.nodes.find(n => n.nodeId === edge.fromNodeId)
          const toNode = currMap.nodes.find(n => n.nodeId === edge.toNodeId)
          if (fromNode && toNode) {
            const ratio = Math.max(0, Math.min(1, currWorld.edgeProgress))
            return {
              x: fromNode.x + (toNode.x - fromNode.x) * ratio,
              y: fromNode.y + (toNode.y - fromNode.y) * ratio,
            }
          }
        }
      }

      return { x: currWorld.x, y: currWorld.y }
    }

    const syncDom = (coord: { x: number; y: number }) => {
      playerPosRef.current = coord
      if (markerRef.current) {
        markerRef.current.style.left = `${coord.x}%`
        markerRef.current.style.top = `${coord.y}%`
      }
    }

    const tick = () => {
      if (stateRef.current.hidden || document.hidden) {
        animId = null
        return
      }

      const coord = computeCurrentCoord()
      syncDom(coord)

      const isTravelling = stateRef.current.activeTravel?.status === 'running' && (stateRef.current.activeTravel?.durationSeconds ?? 0) > 0
      if (isTravelling) {
        animId = requestAnimationFrame(tick)
      } else {
        animId = null
      }
    }

    const startOrSync = () => {
      if (animId !== null) {
        cancelAnimationFrame(animId)
        animId = null
      }

      if (stateRef.current.hidden || document.hidden) {
        return
      }

      const isTravelling = stateRef.current.activeTravel?.status === 'running' && (stateRef.current.activeTravel?.durationSeconds ?? 0) > 0
      if (isTravelling) {
        animId = requestAnimationFrame(tick)
      } else {
        const coord = computeCurrentCoord()
        syncDom(coord)
      }
    }

    const onVisibilityChange = () => {
      startOrSync()
    }

    document.addEventListener('visibilitychange', onVisibilityChange)
    startOrSync()

    return () => {
      if (animId !== null) {
        cancelAnimationFrame(animId)
      }
      document.removeEventListener('visibilitychange', onVisibilityChange)
    }
  }, [map, world, activeTravel, hidden])

  return { playerPosRef, markerRef }
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
export const WorldMapPanel = memo(function WorldMapPanel({ hidden = false }: { hidden?: boolean } = {}): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)
  const world = useGameStore(state => state.world)
  const actions = useGameStore(state => state.actions)
  const items = useGameStore(state => state.items)
  const queue = useGameStore(state => state.queue.entries)
  const map = maps.find(item => item.mapId === world.mapId)
  const activeTravel = queue.find(entry => entry.executionKind === 'travel')
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(world.nodeId)
  const [travelModalOpen, setTravelModalOpen] = useState<boolean>(false)
  const { playerPosRef, markerRef } = usePlayerDisplayPosition(map, world, activeTravel, hidden)

  // 视口拖拽漫游与缩放状态
  const stageRef = useRef<HTMLDivElement>(null)
  const [pan, setPan] = useState<{ x: number; y: number }>({ x: 0, y: 0 })
  const [zoom, setZoom] = useState<number>(1)
  const [isDragging, setIsDragging] = useState<boolean>(false)
  const isDraggingRef = useRef<boolean>(false)
  // zoom/pan 的 ref 镜像：供非 passive 原生 wheel 监听读取最新视口状态，避免把副作用塞进 setState updater。
  const zoomRef = useRef(zoom)
  const panRef = useRef(pan)
  useEffect(() => {
    zoomRef.current = zoom
  }, [zoom])
  useEffect(() => {
    panRef.current = pan
  }, [pan])
  const dragStartRef = useRef<{
    clientX: number
    clientY: number
    panX: number
    panY: number
    moved: boolean
    targetNodeId: string | null
  } | null>(null)

  // 滚轮缩放：React 合成 onWheel 挂在根节点上是 passive 的，preventDefault 无效；
  // 必须在容器上挂非 passive 的原生 wheel 监听才能真正阻止页面同时滚动。
  useEffect(() => {
    const stage = stageRef.current
    if (!stage) return
    const onWheel = (event: WheelEvent): void => {
      event.preventDefault()
      const rect = stage.getBoundingClientRect()
      const mouseX = event.clientX - rect.left
      const mouseY = event.clientY - rect.top

      const zoomFactor = event.deltaY < 0 ? 1.12 : 0.89
      const prevZoom = zoomRef.current
      const nextZoom = Math.min(1.8, Math.max(0.6, prevZoom * zoomFactor))
      if (nextZoom === prevZoom) return

      // 以鼠标位置为锚点缩放：先求指针下的世界坐标，再反推新的平移量。
      const worldX = (mouseX - panRef.current.x) / prevZoom
      const worldY = (mouseY - panRef.current.y) / prevZoom
      setZoom(nextZoom)
      setPan({
        x: mouseX - worldX * nextZoom,
        y: mouseY - worldY * nextZoom,
      })
    }
    stage.addEventListener('wheel', onWheel, { passive: false })
    return () => stage.removeEventListener('wheel', onWheel)
  }, [])

  // 居中聚焦到玩家当前所在位置（在节点驻留时居中地点，在移动时居中路径当前动态坐标）
  const centerOnPlayer = useCallback((targetZoom?: number) => {
    const stage = stageRef.current
    if (!stage) return
    const stageW = stage.clientWidth
    const stageH = stage.clientHeight
    if (!stageW || !stageH) return

    const z = targetZoom ?? zoom
    const pos = playerPosRef.current
    const playerPxX = (pos.x / 100) * stageW
    const playerPxY = (pos.y / 100) * stageH

    setPan({
      x: Math.round(stageW / 2 - playerPxX * z),
      y: Math.round(stageH / 2 - playerPxY * z),
    })
  }, [zoom])
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
    if (targetNodeId) {
      setSelectedNodeId(targetNodeId)
    }

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

  // 1. 提取该地点的产出资源（来自开放采集/生产行动 outputs）
  const nodeResources = useMemo(() => {
    if (!selectedNode) return []
    const list: { itemId: string; name: string; quantity: number }[] = []
    const seen = new Set<string>()

    for (const actionId of selectedNode.availableActionIds) {
      const action = actions.find(a => a.id === actionId)
      if (!action || !action.outputs) continue
      for (const [itemId, qty] of Object.entries(action.outputs)) {
        if (seen.has(itemId)) continue
        seen.add(itemId)
        const itemDef = findItemInCatalog(items, itemId)
        list.push({
          itemId,
          name: itemDef?.name ?? itemId,
          quantity: qty,
        })
      }
    }
    return list
  }, [selectedNode, actions, items])

  // 2. 提取该地点的栖息生灵（普通/精英/领主）
  const nodeMonsters = useMemo(() => {
    if (!selectedNode) return { normal: [], elite: [], boss: [] }
    return {
      normal: selectedNode.monsters ?? [],
      elite: selectedNode.eliteEnemies ?? [],
      boss: selectedNode.bossEnemies ?? [],
    }
  }, [selectedNode])

  // 3. 提取该地点的地标与建筑设施
  const nodeBuildings = useMemo(() => {
    return selectedNode?.buildings ?? []
  }, [selectedNode])

  // 4. 计算当前位置到选中地点的最短路径规划
  const plannedTravel = useMemo(() => {
    if (!map || !selectedNodeId) return null
    return planShortestTravel(map, world, selectedNodeId)
  }, [map, world, selectedNodeId])

  const atSelectedNode = selectedNode?.nodeId === world.nodeId
  const travellingToSelected = selectedNode?.nodeId === activeTravel?.travelDestinationNodeId

  if (!map) {
    return <div className="world-map-loading">{t('combat.map.loading')}</div>
  }

  return (
    <div className="world-map-layout">
      <section
        aria-label={t('combat.map.stageAria', { name: map.name })}
        className={`world-map-stage${isDragging ? ' is-dragging' : ''}`}
        onPointerCancel={handlePointerUp}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
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
            transformOrigin: '0 0',
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
            ref={markerRef}
            style={{
              left: `${playerPosRef.current.x}%`,
              top: `${playerPosRef.current.y}%`,
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
        {selectedNode ? (
          <>
            <header className="world-location-header">
              <h3>{selectedNode.name}</h3>
              <p>{selectedNode.description}</p>
            </header>

            <div className="world-location-sections">
              {/* 1. 产出资源：仅在有产出时展示 */}
              {nodeResources.length > 0 && (
                <div className="world-eco-block">
                  <div className="world-eco-title">
                    <span className="eco-label-name">资源</span>
                  </div>
                  <div className="world-eco-content">
                    {nodeResources.map(res => (
                      <span className="world-resource-pill" key={res.itemId}>
                        {res.name}
                        {res.quantity > 1 && <small>×{res.quantity}</small>}
                      </span>
                    ))}
                  </div>
                </div>
              )}

              {/* 2. 栖息生灵：仅在有怪物时展示 */}
              {(nodeMonsters.normal.length > 0 || nodeMonsters.elite.length > 0 || nodeMonsters.boss.length > 0) && (
                <div className="world-eco-block">
                  <div className="world-eco-title">
                    <span className="eco-label-name">怪物</span>
                  </div>
                  <div className="world-eco-content">
                    {nodeMonsters.normal.map(monster => (
                      <span className="world-monster-pill" key={monster.monsterId}>
                        {monster.name}
                      </span>
                    ))}
                    {nodeMonsters.elite.map(monster => (
                      <span className="world-monster-pill is-elite" key={monster.monsterId}>
                        <small className="monster-rank-tag">精</small>
                        {monster.name}
                      </span>
                    ))}
                    {nodeMonsters.boss.map(monster => (
                      <span className="world-monster-pill is-boss" key={monster.monsterId}>
                        <small className="monster-rank-tag">魁</small>
                        {monster.name}
                      </span>
                    ))}
                  </div>
                </div>
              )}

              {/* 3. 地点（地标设施）：仅在有建筑设施时展示，仅展示名称，Hover 弹窗浮现描述与效果 */}
              {nodeBuildings.length > 0 && (
                <div className="world-eco-block">
                  <div className="world-eco-title">
                    <span className="eco-label-name">地点</span>
                  </div>
                  <div className="world-eco-content">
                    {nodeBuildings.map(b => (
                      <FloatingTooltip
                        key={b.entityId}
                        placement="top"
                        title={
                          <div className="world-building-popover">
                            <strong className="text-gold">{b.name}</strong>
                            {b.description && <p>{b.description}</p>}
                            {b.effect && <small className="text-jade">{b.effect}</small>}
                          </div>
                        }
                        tone="gold"
                      >
                        <span className="world-building-pill">
                          {b.name}
                        </span>
                      </FloatingTooltip>
                    ))}
                  </div>
                </div>
              )}
            </div>

            {/* 底部大横条按钮：仅在非当前地点显示 */}
            {!atSelectedNode && (
              <div className="world-location-actions">
                <button
                  className="confirm-primary world-travel-btn is-ready"
                  disabled={travellingToSelected}
                  onClick={() => setTravelModalOpen(true)}
                  type="button"
                >
                  {travellingToSelected
                    ? `⏳ 正在前往该地点途中...`
                    : `🚶 前往该地点`}
                </button>
                {activeTravel && (
                  <button
                    className="danger world-stop-btn"
                    onClick={() => gameClient.sendCommand('action.remove', { actionInstanceId: activeTravel.entryId })}
                    type="button"
                  >
                    {t('combat.map.stopTravel')}
                  </button>
                )}
              </div>
            )}
          </>
        ) : (
          <p className="world-map-select-prompt">{t('combat.map.selectPrompt')}</p>
        )}
      </aside>

      {/* 启程前往确认弹窗 */}
      {selectedNode && (
        <Modal
          labelledBy="travel-confirm-modal-title"
          onClose={() => setTravelModalOpen(false)}
          open={travelModalOpen}
          size="small"
        >
          <div className="travel-confirm-dialog">
            <h3 id="travel-confirm-modal-title">
              {t('combat.map.travelConfirmTitle', { name: selectedNode.name })}
            </h3>

            <div className="travel-confirm-body">
              <div className="travel-confirm-row">
                <span>{t('combat.map.travelOrigin')}</span>
                <strong>{world.locationName}</strong>
              </div>
              <div className="travel-confirm-row">
                <span>{t('combat.map.travelDestination')}</span>
                <strong className="travel-highlight-dest">{selectedNode.name}</strong>
              </div>
              <div className="travel-confirm-row">
                <span>{t('combat.map.travelEstimatedTime')}</span>
                <strong className="travel-highlight-time">{plannedTravel?.totalDurationSeconds ?? 0} 秒</strong>
              </div>
              {plannedTravel && plannedTravel.pathNodeNames.length > 1 && (
                <div className="travel-route-section">
                  <span>{t('combat.map.travelRoute')}</span>
                  <div className="travel-route-chain">
                    {plannedTravel.pathNodeNames.map((nodeName, idx) => (
                      <span className={`travel-route-node${idx === plannedTravel.pathNodeNames.length - 1 ? ' is-dest' : ''}`} key={idx}>
                        {nodeName}
                        {idx < plannedTravel.pathNodeNames.length - 1 && <span className="travel-route-arrow">→</span>}
                      </span>
                    ))}
                  </div>
                </div>
              )}
              <p className="travel-confirm-prompt">{t('combat.map.travelConfirmPrompt')}</p>
            </div>

            <div className="travel-confirm-actions">
              <button
                onClick={() => setTravelModalOpen(false)}
                type="button"
              >
                {t('combat.map.travelCancelBtn')}
              </button>
              <button
                className="confirm-primary"
                onClick={() => {
                  gameClient.sendCommand('world.travel', { targetNodeId: selectedNode.nodeId })
                  setTravelModalOpen(false)
                }}
                type="button"
              >
                {t('combat.map.travelConfirmBtn')}
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  )
})
