import test from 'node:test'
import assert from 'node:assert/strict'
import { withZongmenResidence } from './zongmenMap.ts'

test('宗门地图只保留本宗节点，迁宗重建连线，离宗清除私密投影', () => {
  const maps = [{ mapId: 'map', nodes: [{ nodeId: 'a', x: 20, y: 60 }, { nodeId: 'b', x: 80, y: 40 }], edges: [] }]
  const own = { nodeId: 'node.zongmen.one', anchorNodeId: 'a', name: '青云宗' }
  const first = withZongmenResidence(maps, own)
  assert.equal(first[0].nodes.length, 3)
  assert.deepEqual([first[0].nodes[2].x, first[0].nodes[2].y], [29, 69])
  assert.equal(maps[0].nodes.length, 2)
  const moved = withZongmenResidence(first, { ...own, anchorNodeId: 'b' })
  assert.equal(moved[0].nodes.length, 3)
  assert.equal(moved[0].edges.length, 1)
  assert.equal(moved[0].edges[0].fromNodeId, 'b')
  const left = withZongmenResidence(moved, null)
  assert.equal(left[0].nodes.length, 2)
  assert.equal(left[0].edges.length, 0)
})

test('宗门地图支持高坐标区域不钳位，并在默认方位拥挤时自动避让邻居', () => {
  const maps = [{
    mapId: 'map.high',
    nodes: [
      { nodeId: 'anchor', x: 180, y: 88 },
      // 占住默认 [9, 9] 方向 (189, 97) 邻近位置，触发自动避让
      { nodeId: 'blocker', x: 188, y: 95 },
    ],
    edges: [],
  }]
  const own = { nodeId: 'node.zongmen.high', anchorNodeId: 'anchor', name: '星陨宗' }
  const next = withZongmenResidence(maps, own)
  const sect = next[0].nodes.find(n => n.nodeId === own.nodeId)
  assert.ok(sect)
  // 高坐标不再被旧逻辑钳位到 <= 95
  assert.ok(sect.x > 100)
  // 已自动避开默认右下方向 [9, 9]，切换至 [9, -9]
  assert.deepEqual([sect.x, sect.y], [189, 79])
})
