import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { findItemInCatalog } from '../game/ids'
import type { CombatantDefinition, MapNodeBuilding, TravelSegment, WorldEdgeDefinition, WorldNodeDefinition, WorldState } from '../game/types'
import { useT } from '../i18n'
import { fetchNodeBuildingIndex } from '../network/apiClient'
import { gameClient } from '../network/gameClient'
import { FloatingTooltip } from './FloatingPopover'
import { ConfirmDialog } from './ConfirmDialog'
import { Modal } from './Modal'
import { MonsterTooltipTarget } from './MonsterDisplay'
import { useUiStore } from '../uiStore'


/** 计算地图两节点之间的欧氏几何连线距离 */
function getEdgeDistance(
  from: { x: number; y: number },
  to: { x: number; y: number },
): number {
  return Math.hypot(to.x - from.x, to.y - from.y)
}

/**
 * 合并后的世界图视图：大世界共用单一坐标系，区域（maps 条目）仅作显示分组；
 * 寻路与玩家位置插值一律对全图求值，跨区域路径天然可用。
 */
interface WorldGraphView {
  nodes: readonly WorldNodeDefinition[]
  edges: readonly WorldEdgeDefinition[]
}

/** 世界坐标包围盒（决定画布相对视口的尺寸：1 世界单位 = 1% 视口宽/高） */
interface WorldBounds {
  width: number
  height: number
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
  map: WorldGraphView,
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
      // 玩家行进在路径边中途：分别以「掉头返回起点」与「继续走到终点」为起点各跑一次
      // Dijkstra，加上对应的剩余边耗时，取两条候选路线中更短的一条。
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
  map: WorldGraphView | undefined,
  world: WorldState,
  activeTravel: {
    status: string
    progressSeconds: number
    durationSeconds: number
    travelSegments?: TravelSegment[] | null
  } | undefined,
  worldBounds: WorldBounds,
  hidden = false,
): {
  playerPosRef: React.RefObject<{ x: number; y: number }>
  markerRef: React.RefObject<HTMLDivElement | null>
} {
  const markerRef = useRef<HTMLDivElement>(null)
  const playerPosRef = useRef<{ x: number; y: number }>({ x: world.x, y: world.y })

  // rAF 循环经 ref 读取最新快照：tick 闭包不随渲染重建，又能始终拿到最新 props；
  // lastUpdateMs 记录 progressSeconds 跳变时刻，用于同步间隔内的本地插值推进。
  const stateRef = useRef({
    map,
    world,
    activeTravel,
    hidden,
    worldBounds,
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
      worldBounds,
      lastProgressSec: activeTravel?.progressSeconds ?? 0,
      lastUpdateMs: progressChanged || travelChanged ? now : prev.lastUpdateMs,
    }
  }, [map, world, activeTravel, hidden, worldBounds])

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

        // 服务端权威下发的 travelSegments（精准支持半路启程、逆向行走与多路段长途移动）
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
        const bounds = stateRef.current.worldBounds
        markerRef.current.style.left = `${(coord.x / bounds.width) * 100}%`
        markerRef.current.style.top = `${(coord.y / bounds.height) * 100}%`
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
  }, [map, world, activeTravel, hidden, worldBounds])

  return { playerPosRef, markerRef }
}
// 地图节点/地点名称与描述来自服务端数据，仅面板框架文本与 aria 描述模板走 i18n。

/**
 * 栖息生灵胶囊 + Hover 悬浮详情卡（普通怪 / 精英 / 领主共用模板）
 *
 * 胶囊本体保持纯名称；悬停浮层展示生命层次徽记、境界与等阶副标、
 * 生命/灵力/双攻/双防六项基础战斗属性（复用斗法敌怪属性统一口径），
 * 以及掉落物图标瓦片行与击杀收益（修为/底蕴/六维）明细。
 */
const MapMonsterPill = memo(function MapMonsterPill({ monster, isElite, isBoss }: {
  monster: CombatantDefinition
  isElite?: boolean
  isBoss?: boolean
}): React.JSX.Element {
  const t = useT()

  return (
    <MonsterTooltipTarget
      displayContents
      isBoss={isBoss}
      isElite={isElite}
      monster={monster}
      placement="top"
    >
      <span className={`world-monster-pill${isElite ? ' is-elite' : ''}${isBoss ? ' is-boss' : ''}`}>
        {(isElite || isBoss) && (
          <small className="monster-rank-tag">
            {isBoss ? t('combat.nearby.markBoss') : t('combat.nearby.markElite')}
          </small>
        )}
        {monster.name}
      </span>
    </MonsterTooltipTarget>
  )
})

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
  const buildingCatalog = useGameStore(state => state.buildings)
  const queue = useGameStore(state => state.queue.entries)
  const consciousness = useGameStore(state => state.combat?.consciousnessLevel ?? 0)
  const activeTravel = queue.find(entry => entry.executionKind === 'travel')
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(world.nodeId)
  const [travelModalOpen, setTravelModalOpen] = useState<boolean>(false)
  // 中断赶路的二次确认目标（行进中条目 entryId）；目标离场（抵达/被移除）时自动失效收起弹窗
  const [pendingStopTravelId, setPendingStopTravelId] = useState<string | null>(null)

  // 全局世界索引：nodeId→所属区域、全节点与全边合并视图。
  // 大世界共用单一坐标系，区域（maps 条目）仅作显示分组；寻路与插值一律对合并图求值。
  const worldIndex = useMemo(() => {
    const nodeOwner = new Map<string, string>()
    const nodes: WorldNodeDefinition[] = []
    const edges: WorldEdgeDefinition[] = []
    for (const m of maps) {
      for (const node of m.nodes) {
        if (!nodeOwner.has(node.nodeId)) {
          nodeOwner.set(node.nodeId, m.mapId)
          nodes.push(node)
        }
      }
      for (const edge of m.edges) edges.push(edge)
    }
    return { nodeOwner, nodes, edges }
  }, [maps])

  // 隐藏节点感知判定：未标 hidden 为常规节点恒可见；标有 hidden 的节点需玩家神识严格大于要求点数（或玩家肉身正处于该节点）才显现
  const isNodeRevealed = useCallback((node: WorldNodeDefinition) => {
    if (node.hidden && node.requirements?.consciousness !== undefined) {
      if (consciousness <= (node.requirements.consciousness as number) && world.nodeId !== node.nodeId) {
        return false
      }
    }
    return true
  }, [consciousness, world.nodeId])

  const revealedNodeIdSet = useMemo(
    () => new Set(worldIndex.nodes.filter(isNodeRevealed).map(n => n.nodeId)),
    [worldIndex.nodes, isNodeRevealed],
  )

  const worldGraphView = useMemo<WorldGraphView>(
    () => ({
      nodes: worldIndex.nodes.filter(isNodeRevealed),
      edges: worldIndex.edges.filter(e => revealedNodeIdSet.has(e.fromNodeId) && revealedNodeIdSet.has(e.toNodeId)),
    }),
    [worldIndex, isNodeRevealed, revealedNodeIdSet],
  )

  // 世界坐标包围盒：画布按世界尺寸铺放（1 世界单位 = 1% 视口宽/高）。
  const worldBounds = useMemo<WorldBounds>(() => {
    let maxX = 100
    let maxY = 100
    for (const node of worldIndex.nodes) {
      if (!isNodeRevealed(node)) continue
      if (node.x > maxX) maxX = node.x
      if (node.y > maxY) maxY = node.y
    }
    return { width: maxX + 12, height: maxY + 8 }
  }, [worldIndex, isNodeRevealed])
  const nx = useCallback((x: number) => `${(x / worldBounds.width) * 100}%`, [worldBounds])
  const ny = useCallback((y: number) => `${(y / worldBounds.height) * 100}%`, [worldBounds])

  // 区域可见集合：驻留节点 → 所在区域 + 跨域边直达的邻接区域（关口双显）；
  // 行进在边上 → 两端点所属区域（跨界路途中双显、图内边单显）；兜底 world.mapId。
  const visibleMapIds = useMemo(() => {
    const ids = new Set<string>()
    const addNodeRegion = (nodeId: string | null | undefined) => {
      if (!nodeId) return
      const owner = worldIndex.nodeOwner.get(nodeId)
      if (owner) ids.add(owner)
    }
    if (world.nodeId) {
      addNodeRegion(world.nodeId)
      for (const edge of worldIndex.edges) {
        if (edge.fromNodeId === world.nodeId) addNodeRegion(edge.toNodeId)
        else if (edge.toNodeId === world.nodeId) addNodeRegion(edge.fromNodeId)
      }
    } else if (world.edgeId) {
      const edge = worldIndex.edges.find(e => e.edgeId === world.edgeId)
      addNodeRegion(edge?.fromNodeId)
      addNodeRegion(edge?.toNodeId)
    }
    if (ids.size === 0 && world.mapId) ids.add(world.mapId)
    return ids
  }, [worldIndex, world.nodeId, world.edgeId, world.mapId])
  const visibleKey = useMemo(
    () => [...visibleMapIds].sort().join('|'),
    [visibleMapIds],
  )

  // 当前可见区域及其节点包围盒（界框/名牌绘制与视口适配共用）
  const visibleRegions = useMemo(() => maps
    .filter(m => visibleMapIds.has(m.mapId))
    .map(m => {
      let minX = Infinity
      let minY = Infinity
      let maxX = -Infinity
      let maxY = -Infinity
      for (const node of m.nodes) {
        if (node.x < minX) minX = node.x
        if (node.x > maxX) maxX = node.x
        if (node.y < minY) minY = node.y
        if (node.y > maxY) maxY = node.y
      }
      return { map: m, bounds: { minX, minY, maxX, maxY } }
    })
    .filter(r => Number.isFinite(r.bounds.minX)),
  [maps, visibleMapIds])

  // 关口节点：持有通往其他区域跨域边的节点，给界门描边提示
  const gatewayNodeIds = useMemo(() => {
    const set = new Set<string>()
    for (const edge of worldIndex.edges) {
      const from = worldIndex.nodeOwner.get(edge.fromNodeId)
      const to = worldIndex.nodeOwner.get(edge.toNodeId)
      if (from && to && from !== to) {
        set.add(edge.fromNodeId)
        set.add(edge.toNodeId)
      }
    }
    return set
  }, [worldIndex])

  const visibleNodes = useMemo(
    () => worldIndex.nodes.filter(n =>
      visibleMapIds.has(worldIndex.nodeOwner.get(n.nodeId)!) && isNodeRevealed(n)
    ),
    [worldIndex, visibleMapIds, isNodeRevealed],
  )
  const visibleNodeIdSet = useMemo(() => new Set(visibleNodes.map(n => n.nodeId)), [visibleNodes])
  const visibleEdges = useMemo(
    () => worldIndex.edges.filter(e =>
      visibleNodeIdSet.has(e.fromNodeId) && visibleNodeIdSet.has(e.toNodeId)
    ),
    [worldIndex, visibleNodeIdSet],
  )

  const { playerPosRef, markerRef } = usePlayerDisplayPosition(worldGraphView, world, activeTravel, worldBounds, hidden)

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
  // 新手引导地图目标地点聚焦运镜动画状态
  const tutorialMapFocusNodeId = useUiStore(state => state.tutorialMapFocusNodeId)
  const panAnimFrameRef = useRef<number | null>(null)
  const animatedFocusNodeIdRef = useRef<string | null>(null)

  // 终止当前正在执行的运镜平移动画
  const cancelPanAnim = useCallback(() => {
    if (panAnimFrameRef.current !== null) {
      cancelAnimationFrame(panAnimFrameRef.current)
      panAnimFrameRef.current = null
    }
  }, [])
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
      cancelPanAnim()
      event.preventDefault()
      const rect = stage.getBoundingClientRect()
      const mouseX = event.clientX - rect.left
      const mouseY = event.clientY - rect.top

      const zoomFactor = event.deltaY < 0 ? 1.12 : 0.89
      const prevZoom = zoomRef.current
      const nextZoom = Math.min(1.8, Math.max(0.3, prevZoom * zoomFactor))
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
    cancelPanAnim()
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
  // 视口自动适配锚框：驻留节点 → 所在区域全部节点 ∪ 相邻节点（跨域关口含对侧节点）；
  // 行进在边上 → 就近端点所在区域 ∪ 两端点邻居；兜底 world.mapId 区域。
  // 邻接区域仍整体渲染、可漫游查看，但不整个纳入适配范围，
  // 避免驻留关口或跨界途中按双区并集缩放把整图压得过小。
  const edgeNearFromSide = world.edgeProgress < 0.5
  const fitToAnchorRegion = useCallback(() => {
    cancelPanAnim()
    const stage = stageRef.current
    if (!stage || worldIndex.nodes.length === 0) return
    const stageW = stage.clientWidth
    const stageH = stage.clientHeight
    if (!stageW || !stageH) return

    let minX = Infinity
    let minY = Infinity
    let maxX = -Infinity
    let maxY = -Infinity
    const include = (nodeId: string | null | undefined) => {
      const node = worldIndex.nodes.find(n => n.nodeId === nodeId)
      if (!node) return
      if (node.x < minX) minX = node.x
      if (node.x > maxX) maxX = node.x
      if (node.y < minY) minY = node.y
      if (node.y > maxY) maxY = node.y
    }
    const includeNeighbors = (nodeId: string | null | undefined) => {
      if (!nodeId) return
      include(nodeId)
      for (const edge of worldIndex.edges) {
        if (edge.fromNodeId === nodeId) include(edge.toNodeId)
        else if (edge.toNodeId === nodeId) include(edge.fromNodeId)
      }
    }
    const includeRegion = (mapId: string | undefined) => {
      const region = maps.find(m => m.mapId === mapId)
      if (!region) return
      for (const node of region.nodes) include(node.nodeId)
    }

    if (world.nodeId) {
      includeRegion(worldIndex.nodeOwner.get(world.nodeId))
      includeNeighbors(world.nodeId)
    } else if (world.edgeId) {
      const edge = worldIndex.edges.find(e => e.edgeId === world.edgeId)
      if (edge) {
        includeRegion(worldIndex.nodeOwner.get(edgeNearFromSide ? edge.fromNodeId : edge.toNodeId))
        includeNeighbors(edge.fromNodeId)
        includeNeighbors(edge.toNodeId)
      }
    }
    if (!Number.isFinite(minX)) includeRegion(world.mapId)
    if (!Number.isFinite(minX)) return

    const pad = 10
    minX -= pad
    minY -= pad
    maxX += pad
    maxY += pad
    const unionW = Math.max(1, maxX - minX)
    const unionH = Math.max(1, maxY - minY)
    // 1 世界单位 = 1% 视口：unionW 单位在 zoom z 下占 unionW/100*stage*z px，
    // 令其约等于视口的 96% → z = 96/unionW，与视口尺寸无关。
    const nextZoom = Math.min(1.8, Math.max(0.3, Math.min(96 / unionW, 96 / unionH)))
    const cx = (minX + maxX) / 2
    const cy = (minY + maxY) / 2
    setZoom(nextZoom)
    setPan({
      x: Math.round(stageW / 2 - (cx / 100) * stageW * nextZoom),
      y: Math.round(stageH / 2 - (cy / 100) * stageH * nextZoom),
    })
  }, [worldIndex, maps, world.nodeId, world.edgeId, world.mapId, edgeNearFromSide])

  // 区域可见集合变化（含首次布局就绪）时自动适配视口；集合不变期间不打断玩家手动漫游
  const fittedKeyRef = useRef<string | null>(null)
  useEffect(() => {
    const stage = stageRef.current
    if (!stage) return

    const tryFit = () => {
      if (stage.clientWidth > 0 && stage.clientHeight > 0 && fittedKeyRef.current !== visibleKey) {
        fitToAnchorRegion()
        fittedKeyRef.current = visibleKey
      }
    }

    tryFit()
    const rafId = requestAnimationFrame(tryFit)
    const resizeObserver = new ResizeObserver(entries => {
      for (const entry of entries) {
        if (entry.contentRect.width > 0 && entry.contentRect.height > 0) {
          tryFit()
        }
      }
    })
    resizeObserver.observe(stage)

    return () => {
      cancelAnimationFrame(rafId)
      resizeObserver.disconnect()
    }
  }, [visibleKey, fitToAnchorRegion])
  // 新手引导点击目标地点时的运镜动画：
  // 打开地图后先让当前地点居中（停留约 360ms 锚定位置），再平滑平移至目标地点居中，
  // 确保在任何屏幕尺寸与显示比例下，目标节点均稳稳落于视口正中心，百分之百可点击。
  useEffect(() => {
    if (hidden || !tutorialMapFocusNodeId) {
      animatedFocusNodeIdRef.current = null
      cancelPanAnim()
      return
    }

    if (animatedFocusNodeIdRef.current === tutorialMapFocusNodeId) return

    const stage = stageRef.current
    if (!stage) return

    const targetNode = worldIndex.nodes.find(n => n.nodeId === tutorialMapFocusNodeId)
    if (!targetNode) return

    let cancelled = false
    let waitRaf: number | null = null

    const startFocusAnimation = (): boolean => {
      if (cancelled) return true
      const stageW = stage.clientWidth
      const stageH = stage.clientHeight
      if (!stageW || !stageH) return false

      animatedFocusNodeIdRef.current = tutorialMapFocusNodeId
      // 标记区域视口已适配，防止后续 ResizeObserver/tryFit 重复覆盖居中视角
      fittedKeyRef.current = visibleKey

      const currentZoom = zoomRef.current
      const fromPos = playerPosRef.current

      // 起点：当前地点居中；终点：目标地点居中
      const startPan = {
        x: Math.round(stageW / 2 - (fromPos.x / 100) * stageW * currentZoom),
        y: Math.round(stageH / 2 - (fromPos.y / 100) * stageH * currentZoom),
      }
      const targetPan = {
        x: Math.round(stageW / 2 - (targetNode.x / 100) * stageW * currentZoom),
        y: Math.round(stageH / 2 - (targetNode.y / 100) * stageH * currentZoom),
      }

      // 阶段 1：先让当前地点居中
      setPan(startPan)

      const dist = Math.hypot(targetPan.x - startPan.x, targetPan.y - startPan.y)
      if (dist < 4) {
        // 当前地点与目标地点一致或极近，无需平移动画，直接定格
        setPan(targetPan)
        return true
      }

      const HOLD_DELAY_MS = 360
      const PAN_DURATION_MS = 880
      const startTime = performance.now()

      cancelPanAnim()

      const tick = (now: number) => {
        if (cancelled) return
        const elapsed = now - startTime
        if (elapsed < HOLD_DELAY_MS) {
          // 短暂停留：保持当前地点居中，让玩家建立空间认知
          panAnimFrameRef.current = requestAnimationFrame(tick)
          return
        }

        const progress = Math.min(1, (elapsed - HOLD_DELAY_MS) / PAN_DURATION_MS)
        const ease = progress < 0.5 ? 4 * progress * progress * progress : 1 - Math.pow(-2 * progress + 2, 3) / 2
        const nextX = Math.round(startPan.x + (targetPan.x - startPan.x) * ease)
        const nextY = Math.round(startPan.y + (targetPan.y - startPan.y) * ease)

        setPan({ x: nextX, y: nextY })

        if (progress < 1) {
          panAnimFrameRef.current = requestAnimationFrame(tick)
        } else {
          panAnimFrameRef.current = null
        }
      }

      panAnimFrameRef.current = requestAnimationFrame(tick)
      return true
    }

    if (!startFocusAnimation()) {
      // 视口尺寸尚未就绪（如页面刚由 hidden 切为可见），等待首帧布局就绪
      const pollLayout = () => {
        if (!startFocusAnimation()) {
          waitRaf = requestAnimationFrame(pollLayout)
        }
      }
      waitRaf = requestAnimationFrame(pollLayout)
    }

    return () => {
      cancelled = true
      if (waitRaf !== null) cancelAnimationFrame(waitRaf)
      cancelPanAnim()
    }
  }, [hidden, tutorialMapFocusNodeId, worldIndex.nodes, visibleKey, cancelPanAnim])

  // 选中节点离开可见区域（区域切换）时回退到当前驻留节点或首个可见节点
  useEffect(() => {
    if (visibleNodes.length === 0) return
    if (!selectedNodeId || !visibleNodes.some(node => node.nodeId === selectedNodeId)) {
      setSelectedNodeId(world.nodeId ?? visibleNodes[0]?.nodeId ?? null)
    }
  }, [visibleNodes, selectedNodeId, world.nodeId])

  const selectedNode = visibleNodes.find(node => node.nodeId === selectedNodeId)
  const plannedEdges = useMemo(
    () => new Set((activeTravel?.travelSegments ?? []).map(segment => segment.edgeId)),
    [activeTravel?.travelSegments],
  )

  // 拖拽漫游事件监听：仅在位移超过 4px 时激活拖拽捕获，防止截断节点点击
  const handlePointerDown = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return
    cancelPanAnim()
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

  // 1. 提取该地点的产出资源（来自开放采集/生产行动 yields 中的物品收益）
  const nodeResources = useMemo(() => {
    if (!selectedNode) return []
    const list: { itemId: string; name: string; quantity: number }[] = []
    const seen = new Set<string>()

    for (const actionId of selectedNode.availableActionIds) {
      const action = actions.find(a => a.id === actionId)
      if (!action?.yields) continue
      for (const yieldEntry of action.yields) {
        if (yieldEntry.kind !== 'item' || !yieldEntry.id) continue
        const itemId = yieldEntry.id
        if (seen.has(itemId)) continue
        seen.add(itemId)
        const itemDef = findItemInCatalog(items, itemId)
        list.push({
          itemId,
          name: itemDef?.name ?? itemId,
          quantity: yieldEntry.amount,
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

  // 3b. 全图玩家公共建筑索引：挂载时拉取一次，此后随本节点建筑投影变化刷新；
  // 失败静默——玩家建筑胶囊是增强展示，索引缺失不影响地图主体功能。
  const [buildingIndex, setBuildingIndex] = useState<Record<string, MapNodeBuilding[]>>({})
  useEffect(() => {
    let cancelled = false
    void fetchNodeBuildingIndex()
      .then(index => { if (!cancelled) setBuildingIndex(index) })
      .catch(() => undefined)
    return () => { cancelled = true }
  }, [world.nodeBuildings])

  // 选中节点的玩家公共建筑：当前节点优先用快照实时投影（含刚发起的建造/升级），
  // 其余节点用全图索引摘要。
  const nodePlayerBuildings = useMemo<MapNodeBuilding[]>(() => {
    if (!selectedNode) return []
    if (selectedNode.nodeId === world.nodeId) {
      return (world.nodeBuildings ?? [])
        .map(building => ({ buildingId: building.entityId, level: building.level, targetLevel: building.targetLevel }))
    }
    return buildingIndex[selectedNode.nodeId] ?? []
  }, [selectedNode, world.nodeId, world.nodeBuildings, buildingIndex])

  // 4. 计算当前位置到选中地点的最短路径规划（合并世界图，跨区域可达）
  const plannedTravel = useMemo(() => {
    if (worldIndex.nodes.length === 0 || !selectedNodeId) return null
    return planShortestTravel(worldGraphView, world, selectedNodeId)
  }, [worldIndex, worldGraphView, world, selectedNodeId])

  const atSelectedNode = selectedNode?.nodeId === world.nodeId
  const travellingToSelected = selectedNode?.nodeId === activeTravel?.travelDestinationNodeId

  // 待中断目标仅在仍在行进时有效；目的地名优先按 wire 字段 travelDestinationNodeId 从地图定义解析
  const stopTravelTarget = activeTravel && activeTravel.entryId === pendingStopTravelId ? activeTravel : null
  const stopTravelDestName = stopTravelTarget?.travelDestinationNodeId
    ? maps.find(m => m.nodes.some(n => n.nodeId === stopTravelTarget.travelDestinationNodeId))
        ?.nodes.find(n => n.nodeId === stopTravelTarget.travelDestinationNodeId)?.name
    : undefined

  if (worldIndex.nodes.length === 0) {
    return <div className="world-map-loading">{t('combat.map.loading')}</div>
  }

  return (
    <div className="world-map-layout">
      <section
        aria-label={t('combat.map.stageAria', { name: visibleRegions.map(r => r.map.name).join(' · ') || world.locationName })}
        className={`world-map-stage${isDragging ? ' is-dragging' : ''}`}
        data-tour="world-map"
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
                const nextZoom = Math.max(0.3, zoom - 0.15)
                setZoom(nextZoom)
                centerOnPlayer(nextZoom)
              }}
              title={t('combat.map.zoomOut')}
              type="button"
            >－</button>
            <button
              className="world-map-tool-btn icon-only"
              onClick={fitToAnchorRegion}
              title={t('combat.map.resetView')}
              type="button"
            >↺</button>
          </div>
        </div>

        {/* 漫游画布视口：尺寸 = 世界坐标包围盒，区域按全局坐标落位 */}
        <div
          className="world-map-canvas"
          style={{
            transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})`,
            transformOrigin: '0 0',
            width: `${worldBounds.width}%`,
            height: `${worldBounds.height}%`,
          }}
        >
          {/* SVG 区域界框 / 路径与距离标注（像素渲染，100% 正常字形比例） */}
          <svg className="world-map-edges">
            <defs>
              <filter id="world-route-glow" x="-20%" y="-20%" width="140%" height="140%">
                <feDropShadow dx="0" dy="0" floodColor="#e8d69a" floodOpacity="0.7" stdDeviation="3" />
              </filter>
            </defs>
            {/* 可见区域虚线界框与区域名牌 */}
            {visibleRegions.map(({ map: region, bounds }) => {
              const framePad = 8
              const fx = bounds.minX - framePad
              const fy = bounds.minY - framePad
              const fw = bounds.maxX - bounds.minX + framePad * 2
              const fh = bounds.maxY - bounds.minY + framePad * 2
              return (
                <g className="world-map-region" key={region.mapId}>
                  <rect
                    className="world-map-region-frame"
                    height={`${(fh / worldBounds.height) * 100}%`}
                    rx={14}
                    width={`${(fw / worldBounds.width) * 100}%`}
                    x={nx(fx)}
                    y={ny(fy)}
                  />
                  <text
                    className="world-map-region-name"
                    dominantBaseline="central"
                    textAnchor="middle"
                    x={nx((bounds.minX + bounds.maxX) / 2)}
                    y={ny(Math.max(3, fy + 3.5))}
                  >
                    {region.name}
                  </text>
                </g>
              )
            })}
            {visibleEdges.map(edge => {
              const from = worldIndex.nodes.find(node => node.nodeId === edge.fromNodeId)
              const to = worldIndex.nodes.find(node => node.nodeId === edge.toNodeId)
              if (!from || !to) return null
              const distance = getEdgeDistance(from, to)
              const isPlanned = plannedEdges.has(edge.edgeId)
              const isCrossing = worldIndex.nodeOwner.get(edge.fromNodeId) !== worldIndex.nodeOwner.get(edge.toNodeId)
              const midX = (from.x + to.x) / 2
              const midY = (from.y + to.y) / 2

              return (
                <g
                  aria-label={t('combat.map.edgeAria', { from: from.name, to: to.name, distance: distance.toFixed(1) })}
                  className={`world-map-edge-group${isPlanned ? ' is-planned' : ''}${isCrossing ? ' is-crossing' : ''}`}
                  key={edge.edgeId}
                >
                  {/* 宽容度高的透明交互拾取层 (28px) */}
                  <line className="world-map-edge-hitbox" x1={nx(from.x)} x2={nx(to.x)} y1={ny(from.y)} y2={ny(to.y)} />
                  {/* 底色衬线 */}
                  <line className="world-map-edge-bg" x1={nx(from.x)} x2={nx(to.x)} y1={ny(from.y)} y2={ny(to.y)} />
                  {/* 核心主路径 */}
                  <line className="world-map-edge-line" x1={nx(from.x)} x2={nx(to.x)} y1={ny(from.y)} y2={ny(to.y)} />
                  {/* 距离文字标注（标准字形比例，带黑色轮廓发光） */}
                  <text
                    className="world-map-edge-label-text"
                    dominantBaseline="central"
                    textAnchor="middle"
                    x={nx(midX)}
                    y={ny(midY)}
                  >
                    {t('combat.map.edgeDistance', { distance: distance.toFixed(1) })}
                  </text>
                </g>
              )
            })}
          </svg>

          {/* 地图节点实体按钮（仅渲染可见区域） */}
          {visibleNodes.map(node => {
            const isCurrent = node.nodeId === world.nodeId
            const isSelected = selectedNodeId === node.nodeId
            const isGateway = gatewayNodeIds.has(node.nodeId)

            return (
              <button
                aria-label={t('combat.map.nodeAria', { name: node.name, description: node.description })}
                aria-pressed={isSelected}
                className={`world-map-node${isCurrent ? ' is-current' : ''}${isSelected ? ' is-selected' : ''}${isGateway ? ' is-gateway' : ''}`}
                data-node-id={node.nodeId}
                key={node.nodeId}
                onClick={e => {
                  e.stopPropagation()
                  setSelectedNodeId(node.nodeId)
                }}
                style={{ left: nx(node.x), top: ny(node.y) }}
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
              left: nx(playerPosRef.current.x),
              top: ny(playerPosRef.current.y),
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
                    <span className="eco-label-name">{t('combat.map.resourcesHeading')}</span>
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
                    <span className="eco-label-name">{t('combat.map.monstersHeading')}</span>
                  </div>
                  <div className="world-eco-content">
                    {nodeMonsters.normal.map(monster => (
                      <MapMonsterPill key={monster.enemyId} monster={monster} />
                    ))}
                    {nodeMonsters.elite.map(monster => (
                      <MapMonsterPill isElite key={`elite-${monster.enemyId}`} monster={monster} />
                    ))}
                    {nodeMonsters.boss.map(monster => (
                      <MapMonsterPill isBoss key={`boss-${monster.enemyId}`} monster={monster} />
                    ))}
                  </div>
                </div>
              )}

              {/* 3. 地点（地标设施）：配表陈设 + 玩家公共建筑合并展示，仅展示名称，Hover 弹窗浮现描述与效果 */}
              {(nodeBuildings.length > 0 || nodePlayerBuildings.length > 0) && (
                <div className="world-eco-block">
                  <div className="world-eco-title">
                    <span className="eco-label-name">{t('combat.map.buildingsHeading')}</span>
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
                    {nodePlayerBuildings.map(pb => {
                      const definition = buildingCatalog.find(entry => entry.buildingId === pb.buildingId)
                      const name = definition?.name ?? pb.buildingId
                      const statusText = pb.level > 0
                        ? (pb.targetLevel > pb.level
                          ? t('combat.nearby.buildingUpgrading', { level: pb.targetLevel })
                          : t('combat.nearby.buildingLevelValue', { level: pb.level }))
                        : t('combat.nearby.buildingConstructing')
                      return (
                        <FloatingTooltip
                          key={`player-${pb.buildingId}`}
                          placement="top"
                          title={
                            <div className="world-building-popover">
                              <strong className="text-gold">{name} · {statusText}</strong>
                              {definition?.description && <p>{definition.description}</p>}
                              {definition?.effect && <small className="text-jade">{definition.effect}</small>}
                            </div>
                          }
                          tone="gold"
                        >
                          <span className="world-building-pill is-player">
                            {name} · {statusText}
                          </span>
                        </FloatingTooltip>
                      )
                    })}
                  </div>
                </div>
              )}
            </div>

            {/* 底部大横条按钮：仅在非当前地点显示 */}
            {!atSelectedNode && (
              <div className="world-location-actions">
                <button
                  className="confirm-primary world-travel-btn is-ready"
                  data-tour="travel-plan"
                  disabled={travellingToSelected}
                  onClick={() => setTravelModalOpen(true)}
                  type="button"
                >
                  {travellingToSelected
                    ? `⏳ ${t('combat.map.travelling')}`
                    : `🚶 ${t('combat.map.planTravel')}`}
                </button>
                {activeTravel && (
                  <button
                    className="danger world-stop-btn"
                    onClick={() => setPendingStopTravelId(activeTravel.entryId)}
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

      {/* 中断赶路二次确认：目标条目离场（抵达/被移除）时自动收起 */}
      <ConfirmDialog
        confirmLabel={t('combat.map.stopTravelConfirmBtn')}
        danger
        description={stopTravelTarget
          ? t('combat.map.stopTravelConfirmDesc', { name: stopTravelDestName ?? stopTravelTarget.actionName })
          : ''}
        onClose={() => setPendingStopTravelId(null)}
        onConfirm={() => {
          if (stopTravelTarget) gameClient.sendCommand('action.remove', { actionInstanceId: stopTravelTarget.entryId })
        }}
        open={stopTravelTarget !== null}
        title={t('combat.map.stopTravelConfirmTitle')}
      />

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
                <strong className="travel-highlight-time">{t('combat.attr.seconds', { n: plannedTravel?.totalDurationSeconds ?? 0 })}</strong>
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
                data-tour="travel-confirm"
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
