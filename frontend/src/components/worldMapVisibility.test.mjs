import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  buildNodeIconLookup,
  computeAnchorFitViewport,
  lookupNodeIconFromMap,
  reprojectViewportOnResize,
  resolveAnchorBounds,
  resolveVisibleMapIds,
  toWorldPixel,
  WORLD_UNIT_X,
  WORLD_UNIT_Y,
} from './worldMapVisibility.ts'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../..')

const testMaps = [
  {
    mapId: 'map_a',
    name: '玄元门',
    nodes: [
      { nodeId: 'a1', x: 10, y: 10 },
      { nodeId: 'a2', x: 20, y: 20 },
      { nodeId: 'a_gate', x: 30, y: 30 },
    ],
  },
  {
    mapId: 'map_b',
    name: '青州',
    nodes: [
      { nodeId: 'b_gate', x: 50, y: 50 },
      { nodeId: 'b1', x: 60, y: 60 },
      { nodeId: 'b2', x: 70, y: 70 },
    ],
  },
]

const testWorldIndex = {
  nodeOwner: new Map([
    ['a1', 'map_a'],
    ['a2', 'map_a'],
    ['a_gate', 'map_a'],
    ['b_gate', 'map_b'],
    ['b1', 'map_b'],
    ['b2', 'map_b'],
  ]),
  nodes: [
    { nodeId: 'a1', x: 10, y: 10 },
    { nodeId: 'a2', x: 20, y: 20 },
    { nodeId: 'a_gate', x: 30, y: 30 },
    { nodeId: 'b_gate', x: 50, y: 50 },
    { nodeId: 'b1', x: 60, y: 60 },
    { nodeId: 'b2', x: 70, y: 70 },
  ],
  edges: [
    { edgeId: 'e_a1_a2', fromNodeId: 'a1', toNodeId: 'a2' },
    { edgeId: 'e_a2_gate', fromNodeId: 'a2', toNodeId: 'a_gate' },
    { edgeId: 'e_cross', fromNodeId: 'a_gate', toNodeId: 'b_gate' },
    { edgeId: 'e_gate_b1', fromNodeId: 'b_gate', toNodeId: 'b1' },
    { edgeId: 'e_b1_b2', fromNodeId: 'b1', toNodeId: 'b2' },
  ],
}

const testGatewayNodeIds = new Set(['a_gate', 'b_gate'])

test('大地图可见区域：驻留内陆普通节点且未选中关口时仅可见当前区域', () => {
  const visible = resolveVisibleMapIds({
    world: { nodeId: 'a1', mapId: 'map_a' },
    selectedNodeId: 'a1',
    worldIndex: testWorldIndex,
    gatewayNodeIds: testGatewayNodeIds,
  })

  assert.deepEqual([...visible], ['map_a'])
})

test('大地图可见区域：肉身移动驻留在关口节点时双显邻接区域', () => {
  const visible = resolveVisibleMapIds({
    world: { nodeId: 'a_gate', mapId: 'map_a' },
    selectedNodeId: 'a_gate',
    worldIndex: testWorldIndex,
    gatewayNodeIds: testGatewayNodeIds,
  })

  assert.equal(visible.has('map_a'), true)
  assert.equal(visible.has('map_b'), true)
  assert.equal(visible.size, 2)
})

test('大地图可见区域：驻留内陆节点但点击选中交界关口节点时，即时展开对侧区域', () => {
  const visible = resolveVisibleMapIds({
    world: { nodeId: 'a1', mapId: 'map_a' },
    selectedNodeId: 'a_gate',
    worldIndex: testWorldIndex,
    gatewayNodeIds: testGatewayNodeIds,
  })

  assert.equal(visible.has('map_a'), true)
  assert.equal(visible.has('map_b'), true)
  assert.equal(visible.size, 2)
})

test('大地图视口锚框：点击选中关口节点与肉身移动驻留关口节点时的包围盒绝对一致', () => {
  // 1. 肉身移动到关口节点
  const boundsAtGateway = resolveAnchorBounds({
    world: { nodeId: 'a_gate', mapId: 'map_a' },
    selectedNodeId: 'a_gate',
    maps: testMaps,
    worldIndex: testWorldIndex,
    gatewayNodeIds: testGatewayNodeIds,
  })

  // 2. 肉身留在内陆节点 a1，但点击选中关口节点 a_gate
  const boundsAtInlandSelectGateway = resolveAnchorBounds({
    world: { nodeId: 'a1', mapId: 'map_a' },
    selectedNodeId: 'a_gate',
    maps: testMaps,
    worldIndex: testWorldIndex,
    gatewayNodeIds: testGatewayNodeIds,
  })

  assert.ok(boundsAtGateway)
  assert.ok(boundsAtInlandSelectGateway)
  // 两者都应覆盖区域 A 的全部节点 (10~30) ∪ 对侧关口节点 b_gate (50, 50)
  assert.deepEqual(boundsAtInlandSelectGateway, boundsAtGateway)
  assert.equal(boundsAtGateway.minX, 10)
  assert.equal(boundsAtGateway.minY, 10)
  assert.equal(boundsAtGateway.maxX, 50)
  assert.equal(boundsAtGateway.maxY, 50)
})

test('大地图交互保持：在双区域展开后点击对侧普通节点，对侧区域保持可见不闪退', () => {
  const visible = resolveVisibleMapIds({
    world: { nodeId: 'a1', mapId: 'map_a' },
    selectedNodeId: 'b1',
    worldIndex: testWorldIndex,
    gatewayNodeIds: testGatewayNodeIds,
  })

  assert.equal(visible.has('map_a'), true)
  assert.equal(visible.has('map_b'), true)
  assert.equal(visible.size, 2)
})

test('大地图交互收起：点回本区域内陆普通节点时平滑收起对侧区域', () => {
  const visible = resolveVisibleMapIds({
    world: { nodeId: 'a1', mapId: 'map_a' },
    selectedNodeId: 'a2',
    worldIndex: testWorldIndex,
    gatewayNodeIds: testGatewayNodeIds,
  })

  assert.deepEqual([...visible], ['map_a'])
})

test('大地图跨界赶路：行进在跨区域边上时双显两端点所属区域', () => {
  const visible = resolveVisibleMapIds({
    world: { edgeId: 'e_cross', mapId: 'map_a' },
    selectedNodeId: null,
    worldIndex: testWorldIndex,
    gatewayNodeIds: testGatewayNodeIds,
  })

  assert.equal(visible.has('map_a'), true)
  assert.equal(visible.has('map_b'), true)
  assert.equal(visible.size, 2)
})

function loadRealMaps() {
  const mapFiles = ['qingyun_mountain.json', 'qingyun_trail.json', 'xingyun_realm.json']
  return mapFiles.flatMap(file =>
    JSON.parse(fs.readFileSync(path.join(repoRoot, 'config/game-data/maps', file), 'utf-8')),
  )
}

function getBounds(nodes) {
  let minX = Infinity
  let minY = Infinity
  let maxX = -Infinity
  let maxY = -Infinity
  for (const node of nodes) {
    if (node.x < minX) minX = node.x
    if (node.y < minY) minY = node.y
    if (node.x > maxX) maxX = node.x
    if (node.y > maxY) maxY = node.y
  }
  return { minX, minY, maxX, maxY }
}

test('等比世界坐标投影：真实配表在不同系统 DPI/缩放比例下归一化构图严格一致且纵横无畸变', () => {
  const maps = loadRealMaps()
  assert.ok(maps.length >= 3)
  // 同一桌面的 100% / 150% / 200% 显示比例（等价等比视口收缩）+ 不同屏幕宽高比
  const dpiStages = [
    { width: 1120, height: 740, label: '100% DPI (1120x740)' },
    { width: 1120 / 1.5, height: 740 / 1.5, label: '150% DPI (747x493)' },
    { width: 560, height: 370, label: '200% DPI (560x370)' },
  ]
  const aspectStages = [
    { width: 860, height: 620, label: 'compact laptop' },
    { width: 580, height: 360, label: 'container stacked' },
    { width: 1680, height: 760, label: 'ultrawide' },
  ]

  for (const region of maps) {
    const regionId = region.id ?? region.mapId
    const bounds = getBounds(region.nodes)
    const refFit = computeAnchorFitViewport(bounds, dpiStages[0].width, dpiStages[0].height)

    // 1. 等比 DPI 缩放（100% vs 150% vs 200%）：归一化屏幕坐标完全一致
    for (const stage of dpiStages.slice(1)) {
      const fit = computeAnchorFitViewport(bounds, stage.width, stage.height)
      for (const node of region.nodes) {
        const nodeId = node.id ?? node.nodeId
        const px = toWorldPixel(node.x, node.y)
        const refNormX = (refFit.viewport.panX + px.x * refFit.viewport.scale) / dpiStages[0].width
        const refNormY = (refFit.viewport.panY + px.y * refFit.viewport.scale) / dpiStages[0].height
        const curNormX = (fit.viewport.panX + px.x * fit.viewport.scale) / stage.width
        const curNormY = (fit.viewport.panY + px.y * fit.viewport.scale) / stage.height
        assert.ok(
          Math.abs(refNormX - curNormX) < 1e-9 && Math.abs(refNormY - curNormY) < 1e-9,
          `${regionId}/${nodeId} 在 ${stage.label} 下相对构图发生漂移`,
        )
      }
    }

    // 2. 任意宽高比视口下所有节点相对向量保持严格等比（零 X/Y 拉伸变形）
    for (const stage of aspectStages) {
      const fit = computeAnchorFitViewport(bounds, stage.width, stage.height)
      const first = region.nodes[0]
      const firstBase = toWorldPixel(first.x, first.y)
      const firstScreen = {
        x: fit.viewport.panX + firstBase.x * fit.viewport.scale,
        y: fit.viewport.panY + firstBase.y * fit.viewport.scale,
      }
      for (const other of region.nodes.slice(1)) {
        const otherBase = toWorldPixel(other.x, other.y)
        const otherScreen = {
          x: fit.viewport.panX + otherBase.x * fit.viewport.scale,
          y: fit.viewport.panY + otherBase.y * fit.viewport.scale,
        }
        assert.ok(
          Math.abs((otherScreen.x - firstScreen.x) - (otherBase.x - firstBase.x) * fit.fitScale) < 1e-9,
          `${regionId} X 比例异常`,
        )
        assert.ok(
          Math.abs((otherScreen.y - firstScreen.y) - (otherBase.y - firstBase.y) * fit.fitScale) < 1e-9,
          `${regionId} Y 比例异常`,
        )
      }
    }
  }
})

test('真实全量配表节点在基准像素空间（纯牌匾与全带图双模式）零碰撞重叠', () => {
  const maps = loadRealMaps()
  const allNodes = maps.flatMap(m => m.nodes.map(n => ({ ...n, mapId: m.id ?? m.mapId })))
  assert.ok(allNodes.length >= 18)

  // 模式 A：无图纯紧凑牌匾（5字名宽约 78px，高约 24px）
  // 模式 B：全部节点配置地标图像（58×42 图像框 + 24px 牌匾 + 3px 间距 -> 宽取 max(60, plaqueW)，总高 68px）
  const modes = [
    { name: '纯紧凑牌匾', minImgW: 0, height: 24 },
    { name: '全节点带图+牌匾', minImgW: 60, height: 68 },
  ]

  for (const mode of modes) {
    for (let i = 0; i < allNodes.length; i += 1) {
      for (let j = i + 1; j < allNodes.length; j += 1) {
        const a = allNodes[i]
        const b = allNodes[j]
        const aPx = toWorldPixel(a.x, a.y)
        const bPx = toWorldPixel(b.x, b.y)
        const wA = Math.max(mode.minImgW, a.name.length * 12 + 18)
        const wB = Math.max(mode.minImgW, b.name.length * 12 + 18)
        const overlapX = Math.abs(aPx.x - bPx.x) < (wA + wB) / 2
        const overlapY = Math.abs(aPx.y - bPx.y) < mode.height
        assert.equal(
          overlapX && overlapY,
          false,
          `${mode.name} 模式下节点重叠: ${a.name}(${a.x},${a.y}) 与 ${b.name}(${b.x},${b.y}) [dxPx=${Math.abs(aPx.x - bPx.x).toFixed(1)}, dyPx=${Math.abs(aPx.y - bPx.y).toFixed(1)}]`,
        )
      }
    }
  }

  assert.equal(WORLD_UNIT_X, 9.6)
  assert.equal(WORLD_UNIT_Y, 6.4)
})

test('reprojectViewportOnResize：缩放/切窗重投影严格保持视口中心世界点与相对缩放比', () => {
  const bounds = { minX: 10, minY: 12, maxX: 95, maxY: 88 }
  const fit1 = computeAnchorFitViewport(bounds, 1120, 740)
  const relZoom = 1.65
  const zoomedScale = fit1.fitScale * relZoom
  const shiftedPanX = 1120 / 2 - fit1.focusWorld.x * zoomedScale - 140
  const shiftedPanY = 740 / 2 - fit1.focusWorld.y * zoomedScale + 85
  const prevCenterWorldX = (1120 / 2 - shiftedPanX) / zoomedScale
  const prevCenterWorldY = (740 / 2 - shiftedPanY) / zoomedScale

  for (const nextStage of [{ w: 560, h: 370 }, { w: 1680, h: 900 }]) {
    const fit2 = computeAnchorFitViewport(bounds, nextStage.w, nextStage.h)
    const next = reprojectViewportOnResize({
      prevViewport: { panX: shiftedPanX, panY: shiftedPanY, scale: zoomedScale },
      prevStageWidth: 1120,
      prevStageHeight: 740,
      prevFitScale: fit1.fitScale,
      nextStageWidth: nextStage.w,
      nextStageHeight: nextStage.h,
      nextFitScale: fit2.fitScale,
    })
    assert.ok(Math.abs(next.relativeZoom - relZoom) < 1e-9)
    const nextCenterWorldX = (nextStage.w / 2 - next.viewport.panX) / next.viewport.scale
    const nextCenterWorldY = (nextStage.h / 2 - next.viewport.panY) / next.viewport.scale
    assert.ok(Math.abs(nextCenterWorldX - prevCenterWorldX) < 1e-9)
    assert.ok(Math.abs(nextCenterWorldY - prevCenterWorldY) < 1e-9)
  }
})

test('buildNodeIconLookup & lookupNodeIconFromMap：格式优先级与宗门回退规则', () => {
  const iconMap = buildNodeIconLookup([
    { '../assets/nodes/node.cloud-gate.svg': '/assets/gate.svg', '../assets/nodes/.gitkeep': '' },
    { '../assets/nodes/node.cloud-gate.png': '/assets/gate.png' },
    {
      '../assets/nodes/node.cloud-gate.webp': '/assets/gate.webp',
      '../assets/nodes/node.zongmen.webp': '/assets/sect.webp',
      '../assets/nodes/node.zongmen.special.png': '/assets/sect-special.png',
    },
  ])

  assert.equal(lookupNodeIconFromMap(iconMap, 'node.cloud-gate'), '/assets/gate.webp')
  assert.equal(lookupNodeIconFromMap(iconMap, 'node.zongmen.9988'), '/assets/sect.webp')
  assert.equal(lookupNodeIconFromMap(iconMap, 'node.zongmen.special'), '/assets/sect-special.png')
  assert.equal(lookupNodeIconFromMap(iconMap, 'node.missing'), null)
})

test('静态代码契约：WorldMapPanel 与 expedition.css 强制保持等比画布、强 nodeId 透传与容器查询规则', () => {
  const panelSource = fs.readFileSync(path.join(repoRoot, 'frontend/src/components/WorldMapPanel.tsx'), 'utf-8')
  const cssSource = fs.readFileSync(path.join(repoRoot, 'frontend/src/styles/expedition.css'), 'utf-8')

  // 严禁回退至非等比百分比拉伸 SVG 或固定死像素节点覆盖
  assert.equal(panelSource.includes('preserveAspectRatio="none"'), false)
  assert.equal(panelSource.includes('vectorEffect="non-scaling-stroke"'), false)
  // 强 ID 显式透传契约：MapNodeButton 显式接收 nodeId 并直传 resolveNodeIconUrl(nodeId) 与 data-node-id={nodeId}
  assert.match(panelSource, /const MapNodeButton = memo\(function MapNodeButton\(\{[\s\S]*?\bnodeId\b/)
  assert.match(panelSource, /resolveNodeIconUrl\(nodeId\)/)
  assert.match(panelSource, /data-node-id=\{nodeId\}/)
  // canvas JSX style 不拥有 transform 属性，防高刷路径被 React 重渲染拉回
  assert.doesNotMatch(panelSource, /className="world-map-canvas"[\s\S]{0,160}transform:/)
  // 容器查询窄区自动退栈
  assert.match(cssSource, /container:\s*map-shell\s*\/\s*inline-size/)
  assert.match(cssSource, /@container map-shell \(max-width:\s*660px\)/)
})
