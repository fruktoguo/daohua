import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import path from 'node:path'

const repoRoot = process.cwd()

test('SettingsPage 礼包兑换奖励展示严格透传 itemId 与强化等级，并注册共享瓦片锚点', async () => {
  const settingsSource = await readFile(
    path.resolve(repoRoot, 'frontend/src/components/SettingsPage.tsx'),
    'utf8',
  )
  const selectionGridCss = await readFile(
    path.resolve(repoRoot, 'frontend/src/styles/selection-grid.css'),
    'utf8',
  )

  // 1. 强标识防退化契约：RedeemRewardCard 必须对 ItemTooltipTarget 与 ItemTile 显式传入 itemId={reward.itemId}
  const tooltipMatch = settingsSource.match(/<ItemTooltipTarget[\s\S]*?>/)
  assert.ok(tooltipMatch, 'SettingsPage 必须使用 ItemTooltipTarget 包裹兑换奖励瓦片')
  assert.match(tooltipMatch[0], /\bitemId=\{reward\.itemId\}/, 'ItemTooltipTarget 必须显式透传 itemId={reward.itemId}')
  assert.match(tooltipMatch[0], /\benhancementLevel=\{enhancementLevel\}/, 'ItemTooltipTarget 必须显式透传 enhancementLevel')

  const tileMatch = settingsSource.match(/<ItemTile[\s\S]*?\/>/)
  assert.ok(tileMatch, 'SettingsPage 必须使用 ItemTile 渲染兑换奖励图标')
  assert.match(tileMatch[0], /\bitemId=\{reward\.itemId\}/, 'ItemTile 必须显式透传 itemId={reward.itemId}')
  assert.match(tileMatch[0], /\bamount=\{reward\.quantity\}/, 'ItemTile 必须显式透传数量 amount={reward.quantity}')
  assert.match(tileMatch[0], /\benhancementLevel=\{enhancementLevel\}/, 'ItemTile 必须显式透传 enhancementLevel')

  // 2. 共享瓦片锚点注册契约：.redeem-reward-anchor 必须登记在 selection-grid.css 共享 :is() 组中
  assert.match(selectionGridCss, /\.redeem-reward-anchor\b/, 'selection-grid.css 必须登记 .redeem-reward-anchor')
})
