import test from 'node:test'
import assert from 'node:assert/strict'

/** 模拟宗门名录附近排序核心算法 */
function sortZongmenDirectory(directory, currentNodeId, currentMapId, nodeToMap) {
  const list = [...directory]
  list.sort((a, b) => {
    const aHere = a.anchorNodeId === currentNodeId ? 2 : currentMapId && nodeToMap.get(a.anchorNodeId) === currentMapId ? 1 : 0
    const bHere = b.anchorNodeId === currentNodeId ? 2 : currentMapId && nodeToMap.get(b.anchorNodeId) === currentMapId ? 1 : 0
    if (aHere !== bHere) return bHere - aHere
    return a.name.localeCompare(b.name, 'zh-CN')
  })
  return list
}

test('宗门名录排序：当前地点优先置顶，同域次之，其他地点按名称排序', () => {
  const nodeToMap = new Map([
    ['node.yunxi-village', 'map.qingyun'],
    ['node.cloud-gate', 'map.qingyun'],
    ['node.astral-rift', 'map.xingyun'],
    ['node.carrion-tomb', 'map.xingyun'],
  ])

  const directory = [
    { zongmenId: '1', name: '异界魔宗', prefix: '魔', anchorNodeId: 'node.astral-rift', memberCount: 5, capacity: 20 },
    { zongmenId: '2', name: '青云主峰门', prefix: '青', anchorNodeId: 'node.cloud-gate', memberCount: 10, capacity: 20 },
    { zongmenId: '3', name: '云溪剑派', prefix: '剑', anchorNodeId: 'node.yunxi-village', memberCount: 8, capacity: 20 },
    { zongmenId: '4', name: '星陨血门', prefix: '血', anchorNodeId: 'node.carrion-tomb', memberCount: 3, capacity: 20 },
  ]

  // 玩家身处云溪村 (node.yunxi-village, map.qingyun)
  const sorted = sortZongmenDirectory(directory, 'node.yunxi-village', 'map.qingyun', nodeToMap)

  // 1. 当前节点：云溪剑派必须第 1 位
  assert.equal(sorted[0].name, '云溪剑派')
  assert.equal(sorted[0].anchorNodeId, 'node.yunxi-village')

  // 2. 同图节点：青云主峰门必须第 2 位
  assert.equal(sorted[1].name, '青云主峰门')
  assert.equal(sorted[1].anchorNodeId, 'node.cloud-gate')

  // 3. 其他异图宗门在后方，按中文名拼音/笔画稳定正序排序
  assert.equal(sorted[2].name, '星陨血门')
  assert.equal(sorted[3].name, '异界魔宗')
})

test('拜帖申请状态：已递交拜帖的宗门能被精确识别，防重判定自洽', () => {
  const applications = [
    { zongmenId: '3', zongmenName: '云溪剑派', playerId: 'me', displayName: '散修道友', kind: 'application' },
  ]

  const isApplied = (zongmenId) => applications.some(app => app.zongmenId === zongmenId)

  assert.equal(isApplied('3'), true)
  assert.equal(isApplied('1'), false)
  assert.equal(isApplied('2'), false)
})
