import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { resolve } from 'node:path'

test('秘法操作窗口契约：采用目标槽位+浮动选择器+次数选择器，淘汰平铺网格', async () => {
  const source = await readFile(resolve('frontend/src/components/SecretArtOperations.tsx'), 'utf8')

  // 1. 目标槽位必须使用通用的修仙 ItemUseSlot
  assert.match(
    source,
    /<ItemUseSlot[\s\S]*?item=\{selected\}[\s\S]*?\/>/,
    '必须使用 ItemUseSlot 承载目标物品槽位',
  )

  // 2. 待选列表必须使用通用的浮动待选器 ItemGridPicker
  assert.match(
    source,
    /<ItemGridPicker[\s\S]*?items=\{candidates\}[\s\S]*?\/>/,
    '必须使用 ItemGridPicker 弹出浮动物品选择器',
  )

  // 3. 必须集成通用数量与次数选择器 QuantitySelector（重复次数 repeats）
  assert.match(
    source,
    /<QuantitySelector[\s\S]*?value=\{repeats\}[\s\S]*?\/>/,
    '必须提供 QuantitySelector 供玩家配置重复次数',
  )

  // 4. 必须通过 action.enqueue 投递队列行动
  assert.match(
    source,
    /sendCommand\('action\.enqueue'/,
    '必须通过 action.enqueue 投递挂机队列行动',
  )

  // 5. 必须格式化为「消耗 / 产出 / 感悟 / 单次耗时」四大固定区块
  assert.match(source, /previewSectionCost/, '必须包含消耗区块')
  assert.match(source, /previewSectionYield/, '必须包含产出区块')
  assert.match(source, /insights/, '必须包含感悟区块')
  assert.match(source, /previewSectionDuration/, '必须包含单次耗时区块')

  // 4. 产出条目必须使用 ItemReference 且显式透传 itemId
  assert.match(
    source,
    /<ItemReference[\s\S]*?itemId=/i,
    '产出预览必须使用 ItemReference 且显式透传 itemId 避免纯文本退化',
  )

  // 5. 必须彻底淘汰原先整屏平铺全部背包可用物品的 .secret-art-selection 网格
  assert.doesNotMatch(
    source,
    /className=["'][^"']*secret-art-selection[^"']*["']/,
    '严禁在页面直接平铺所有候选物品，必须收拢进浮动选择器',
  )
})

test('秘法感悟预览按目标物品显式等级结算（与服务端 ResolveItemLevel 同口径）', async () => {
  const source = await readFile(resolve('frontend/src/components/SecretArtOperations.tsx'), 'utf8')

  assert.match(
    source,
    /resolveCatalogItemLevel\(selected\?\.equipmentDefinition, selected\?\.definition/,
    '秘法感悟必须按目标物品显式 level 解析代表等级，严禁只按等阶换算',
  )
})

test('秘法操作已有同类行动时仍可配置并追加新目标', async () => {
  const source = await readFile(resolve('frontend/src/components/SecretArtOperations.tsx'), 'utf8')

  assert.match(
    source,
    /<ItemUseSlot\s+item=\{selected\}\s+onClick=\{event => setPickerAnchor\(prev => \(prev \? null : event\.currentTarget\)\)\}/,
    '当前行动存在时，目标槽仍须打开新行动的物品选择器',
  )
  assert.doesNotMatch(source, /activeEntry\?\.targetItemId[\s\S]*?return found/,
    '新行动目标不得被当前队列目标覆盖')
  const activeBranchStart = source.indexOf('{isActive ? (')
  const activeBranchEnd = source.indexOf(') : queueLength > 0 ?', activeBranchStart)
  assert.notEqual(activeBranchStart, -1, '必须保留已有行动分支')
  assert.notEqual(activeBranchEnd, -1, '已有行动分支必须与普通队列状态分开')
  const activeBranch = source.slice(activeBranchStart, activeBranchEnd)
  assert.match(activeBranch, /enqueue\$\{opCap\}/, '已有行动时必须提供新行动入队按钮')
  assert.match(activeBranch, /submitAction\(false\)/, '新行动必须追加到队尾')
  assert.match(activeBranch, /stop\$\{opCap\}/, '已有行动仍须提供独立停止操作')
})
