import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { resolve } from 'node:path'

test('强化已有行动时仍可配置并追加新目标', async () => {
  const source = await readFile(resolve('frontend/src/components/EnhancementPage.tsx'), 'utf8')

  assert.match(source, /const selected = userSelected/,
    '新强化行动必须使用玩家当前选择，不能强制显示队列中已有目标')
  assert.match(
    source,
    /<ItemUseSlot\s+item=\{selected\}\s+onClick=\{event => setPickerAnchor\(prev => \(prev \? null : event\.currentTarget\)\)\}/,
    '当前强化行动存在时，目标槽仍须打开新行动的物品选择器',
  )
  const activeBranchStart = source.indexOf('{isEnhancing ? (')
  const activeBranchEnd = source.indexOf(') : maxedOut ?', activeBranchStart)
  assert.notEqual(activeBranchStart, -1, '必须保留已有强化行动分支')
  assert.notEqual(activeBranchEnd, -1, '已有强化行动分支必须与普通表单状态分开')
  const activeBranch = source.slice(activeBranchStart, activeBranchEnd)
  assert.match(activeBranch, /app\.enhancement\.enqueue/, '已有强化行动时必须提供新行动入队按钮')
  assert.match(activeBranch, /submit\(false\)/, '新强化行动必须追加到队尾')
  assert.match(activeBranch, /app\.enhancement\.stop/, '已有强化行动仍须提供独立停止操作')
})
