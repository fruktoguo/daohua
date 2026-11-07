import test from 'node:test'
import assert from 'node:assert/strict'
import { withZongmenResidence } from './zongmenMap.ts'

test('宗门地图只保留本宗节点，迁宗重建连线，离宗清除私密投影', () => {
  const maps = [{ mapId: 'map', nodes: [{ nodeId: 'a', x: 20, y: 60 }, { nodeId: 'b', x: 80, y: 40 }], edges: [] }]
  const own = { nodeId: 'node.zongmen.one', anchorNodeId: 'a', name: '青云宗' }
  const first = withZongmenResidence(maps, own)
  assert.equal(first[0].nodes.length, 3)
  assert.deepEqual([first[0].nodes[2].x, first[0].nodes[2].y], [26, 66])
  assert.equal(maps[0].nodes.length, 2)
  const moved = withZongmenResidence(first, { ...own, anchorNodeId: 'b' })
  assert.equal(moved[0].nodes.length, 3)
  assert.equal(moved[0].edges.length, 1)
  assert.equal(moved[0].edges[0].fromNodeId, 'b')
  const left = withZongmenResidence(moved, null)
  assert.equal(left[0].nodes.length, 2)
  assert.equal(left[0].edges.length, 0)
})
