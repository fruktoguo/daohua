/**
 * 大地图可见区域集合与视口适配包围盒计算逻辑
 */

export interface WorldIndexLike {
  nodeOwner: Map<string, string>
  nodes: readonly { nodeId: string; x: number; y: number }[]
  edges: readonly { edgeId: string; fromNodeId: string; toNodeId: string }[]
}

export interface RegionDefinitionLike {
  mapId: string
  nodes: readonly { nodeId: string; x: number; y: number }[]
}

export interface ResolveVisibleMapIdsParams {
  world: {
    nodeId?: string | null
    edgeId?: string | null
    mapId?: string | null
  }
  selectedNodeId?: string | null
  worldIndex: {
    nodeOwner: Map<string, string>
    edges: readonly { edgeId: string; fromNodeId: string; toNodeId: string }[]
  }
  gatewayNodeIds: ReadonlySet<string>
}

/**
 * 计算大地图当前应展开渲染的可见区域 ID 集合：
 * 1. 玩家当前实体位置（权威物理驻留/行进）：所在区域 + 跨域边直达的邻接区域（关口双显）；
 * 2. 玩家当前选中的节点（交互探索）：点击选中交界节点时即时展开对侧区域，效果与移动过去一致；
 *    若选中的是对侧展开区域内的节点，保持该区域可见，方便玩家查看详情与规划路线；
 * 3. 兜底玩家当前地图 ID。
 */
export function resolveVisibleMapIds(params: ResolveVisibleMapIdsParams): Set<string> {
  const { world, selectedNodeId, worldIndex, gatewayNodeIds } = params
  const ids = new Set<string>()

  const addNodeRegion = (nodeId: string | null | undefined) => {
    if (!nodeId) return
    const owner = worldIndex.nodeOwner.get(nodeId)
    if (owner) ids.add(owner)
  }

  const addNodeNeighbors = (nodeId: string | null | undefined) => {
    if (!nodeId) return
    for (const edge of worldIndex.edges) {
      if (edge.fromNodeId === nodeId) addNodeRegion(edge.toNodeId)
      else if (edge.toNodeId === nodeId) addNodeRegion(edge.fromNodeId)
    }
  }

  // 1. 玩家物理位置
  if (world.nodeId) {
    addNodeRegion(world.nodeId)
    addNodeNeighbors(world.nodeId)
  } else if (world.edgeId) {
    const edge = worldIndex.edges.find(e => e.edgeId === world.edgeId)
    addNodeRegion(edge?.fromNodeId)
    addNodeRegion(edge?.toNodeId)
  }

  // 2. 交互选中节点：点击选中交界关口节点时展开对侧邻接区域；选中对侧展开区域内节点时保持可见
  if (selectedNodeId) {
    addNodeRegion(selectedNodeId)
    if (gatewayNodeIds.has(selectedNodeId)) {
      addNodeNeighbors(selectedNodeId)
    }
  }

  // 3. 兜底
  if (ids.size === 0 && world.mapId) {
    ids.add(world.mapId)
  }

  return ids
}

export interface ResolveAnchorBoundsParams {
  world: {
    nodeId?: string | null
    edgeId?: string | null
    mapId?: string | null
  }
  selectedNodeId?: string | null
  maps: readonly RegionDefinitionLike[]
  worldIndex: WorldIndexLike
  gatewayNodeIds: ReadonlySet<string>
  edgeNearFromSide?: boolean
}

export interface WorldAnchorBounds {
  minX: number
  minY: number
  maxX: number
  maxY: number
}

/**
 * 计算大地图视口自适应适配锚框：
 * 1. 驻留节点 → 所在区域全部节点 ∪ 相邻节点（跨域关口含对侧节点）；
 * 2. 行进在边上 → 就近端点所在区域 ∪ 两端点邻居；兜底 world.mapId 区域；
 * 3. 选中交界节点 → 与物理移动到关口节点一致，将该关口节点及其邻居（含对侧关口节点）纳入锚框；
 * 4. 选中对侧展开区域节点 → 将选中的节点纳入锚框保证可见。
 */
export function resolveAnchorBounds(params: ResolveAnchorBoundsParams): WorldAnchorBounds | null {
  const { world, selectedNodeId, maps, worldIndex, gatewayNodeIds, edgeNearFromSide = true } = params
  if (worldIndex.nodes.length === 0) return null

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

  const includeRegion = (mapId: string | null | undefined) => {
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

  // 选中关口节点时与移动到关口节点一致：将该关口节点及其邻居（含对侧跨域节点）一并纳入锚框
  if (selectedNodeId && selectedNodeId !== world.nodeId) {
    if (gatewayNodeIds.has(selectedNodeId)) {
      includeNeighbors(selectedNodeId)
    } else if (worldIndex.nodeOwner.get(selectedNodeId) !== worldIndex.nodeOwner.get(world.nodeId ?? '')) {
      include(selectedNodeId)
    }
  }

  if (!Number.isFinite(minX)) includeRegion(world.mapId)
  if (!Number.isFinite(minX)) return null

  return { minX, minY, maxX, maxY }
}

/** 世界单位到固定基准像素的换算倍率（100×100 世界单位 = 960×640 基准像素，固定 3:2 比例） */
export const WORLD_UNIT_X = 9.6
export const WORLD_UNIT_Y = 6.4

/** 相对适配倍率（relativeZoom）允许的最小/最大用户缩放倍数 */
export const MIN_RELATIVE_ZOOM = 0.45
export const MAX_RELATIVE_ZOOM = 2.6

export interface WorldPixelPoint {
  x: number
  y: number
}

export interface WorldCanvasSize {
  width: number
  height: number
}

export interface MapViewport {
  panX: number
  panY: number
  scale: number
}

export interface FitViewportResult {
  viewport: MapViewport
  fitScale: number
  relativeZoom: number
  focusWorld: WorldPixelPoint
}

/** 将配表世界坐标转换为固定基准像素坐标。 */
export function toWorldPixel(x: number, y: number): WorldPixelPoint {
  return { x: x * WORLD_UNIT_X, y: y * WORLD_UNIT_Y }
}

/** 计算全图画布的固定基准像素尺寸。 */
export function computeWorldCanvasSize(maxWorldX: number, maxWorldY: number): WorldCanvasSize {
  const px = toWorldPixel(Math.max(100, maxWorldX) + 12, Math.max(100, maxWorldY) + 8)
  return { width: px.x, height: px.y }
}

/** 将用户相对缩放倍率钳制在安全交互区间内。 */
export function clampRelativeZoom(relativeZoom: number): number {
  return Math.min(MAX_RELATIVE_ZOOM, Math.max(MIN_RELATIVE_ZOOM, relativeZoom))
}

/**
 * 依据当前锚点包围盒与舞台像素尺寸计算初始/复位等比视口。
 * 同一组锚点在不同显示比例下会产出相同的相对屏幕构图。
 */
export function computeAnchorFitViewport(
  anchorBounds: { minX: number; minY: number; maxX: number; maxY: number },
  stageWidth: number,
  stageHeight: number,
): FitViewportResult {
  const safeW = Math.max(1, stageWidth)
  const safeH = Math.max(1, stageHeight)
  const padX = 11
  const padY = 11
  const minPx = toWorldPixel(anchorBounds.minX - padX, anchorBounds.minY - padY)
  const maxPx = toWorldPixel(anchorBounds.maxX + padX, anchorBounds.maxY + padY)
  const contentW = Math.max(1, maxPx.x - minPx.x)
  const contentH = Math.max(1, maxPx.y - minPx.y)
  const rawFitScale = Math.min((safeW * 0.96) / contentW, (safeH * 0.96) / contentH)
  const fitScale = Math.max(0.05, Math.min(8, rawFitScale))
  const focusWorld = {
    x: (minPx.x + maxPx.x) / 2,
    y: (minPx.y + maxPx.y) / 2,
  }
  return {
    fitScale,
    relativeZoom: 1,
    focusWorld,
    viewport: {
      scale: fitScale,
      panX: safeW / 2 - focusWorld.x * fitScale,
      panY: safeH / 2 - focusWorld.y * fitScale,
    },
  }
}

/**
 * 在舞台尺寸/DPI 缩放变化时保持焦点世界坐标与相对缩放倍率进行等比重投影。
 */
export function reprojectViewportOnResize(params: {
  prevViewport: MapViewport
  prevStageWidth: number
  prevStageHeight: number
  prevFitScale: number
  nextStageWidth: number
  nextStageHeight: number
  nextFitScale: number
}): { viewport: MapViewport; relativeZoom: number; focusWorld: WorldPixelPoint } {
  const {
    prevViewport,
    prevStageWidth,
    prevStageHeight,
    prevFitScale,
    nextStageWidth,
    nextStageHeight,
    nextFitScale,
  } = params
  const safePrevScale = Math.max(0.001, prevViewport.scale)
  const safePrevFit = Math.max(0.001, prevFitScale)
  const relativeZoom = clampRelativeZoom(safePrevScale / safePrevFit)
  const focusWorld = {
    x: (prevStageWidth / 2 - prevViewport.panX) / safePrevScale,
    y: (prevStageHeight / 2 - prevViewport.panY) / safePrevScale,
  }
  const nextScale = nextFitScale * relativeZoom
  return {
    relativeZoom,
    focusWorld,
    viewport: {
      scale: nextScale,
      panX: nextStageWidth / 2 - focusWorld.x * nextScale,
      panY: nextStageHeight / 2 - focusWorld.y * nextScale,
    },
  }
}

/**
 * 将按优先级排列的 glob 资源映射合并为以无扩展名文件名（即 nodeId）为键的查找表。
 * 数组中靠后的映射覆盖靠前的映射（用于 .webp > .png > .svg 优先级）。
 */
export function buildNodeIconLookup(
  modulesByPriority: ReadonlyArray<Record<string, string>>,
): ReadonlyMap<string, string> {
  const map = new Map<string, string>()
  for (const modules of modulesByPriority) {
    for (const [rawPath, url] of Object.entries(modules)) {
      const normalized = rawPath.replace(/\\/g, '/')
      const fileName = normalized.slice(normalized.lastIndexOf('/') + 1)
      if (!fileName || fileName.startsWith('.')) continue
      const dotIdx = fileName.lastIndexOf('.')
      const stem = dotIdx > 0 ? fileName.slice(0, dotIdx) : fileName
      if (stem && url) {
        map.set(stem, url)
      }
    }
  }
  return map
}

/**
 * 从节点图标查找表中检索指定 nodeId 对应的图像 URL；
 * 宗门专属驻地（node.zongmen.*）在未命中精确文件时回退至共享键 node.zongmen。
 */
export function lookupNodeIconFromMap(
  iconMap: ReadonlyMap<string, string>,
  nodeId: string,
): string | null {
  if (!nodeId) return null
  const exact = iconMap.get(nodeId)
  if (exact) return exact
  if (nodeId.startsWith('node.zongmen.')) {
    return iconMap.get('node.zongmen') ?? null
  }
  return null
}
