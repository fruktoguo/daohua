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

  // 3. 必须集成通用数量与次数选择器 QuantitySelector
  assert.match(
    source,
    /<QuantitySelector[\s\S]*?value=\{effectiveQuantity\}[\s\S]*?\/>/,
    '必须提供 QuantitySelector 供玩家选择执行次数/数量',
  )

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
