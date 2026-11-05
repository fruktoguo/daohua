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
