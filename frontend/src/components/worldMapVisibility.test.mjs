import test from 'node:test'
import assert from 'node:assert/strict'
import { resolveVisibleMapIds, resolveAnchorBounds } from './worldMapVisibility.ts'

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
