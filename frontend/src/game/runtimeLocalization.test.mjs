import '../../test-loader.mjs'
import assert from 'node:assert/strict'
import test from 'node:test'
import fs from 'node:fs'
import path from 'node:path'

const root = process.cwd()
const [
  { resolveQueueActionName, resolveWorldLocationName, localizeActionDisplayText },
  { resolveLocalizedEntityName },
  { getT, LOCALES, useI18nStore },
] = await Promise.all([
  import('./ids.ts'),
  import('./entityNames.ts'),
  import('../i18n/index.ts'),
])

test('运行态中文快照在英文态复用现有实体译名，语言切换后仍保留权威原值', async () => {
  const csv = fs.readFileSync(path.join(root, 'frontend/public/locales/en-US.csv'), 'utf8')
  const originalFetch = globalThis.fetch
  globalThis.fetch = async () => new Response(csv)
  // Node 取证没有 Vite 的构建期 CSV 注册表；显式注册仓库现有英文实包。
  LOCALES.push('en-US')
  const entry = Object.freeze({ actionId: 'explore.gather-ningxue-grass', actionName: '采摘 凝气草' })
  const world = Object.freeze({ mapId: 'map.qingyun-mountains', nodeId: 'node.yunxi-village', locationName: '云溪村' })
  try {
    assert.equal(resolveQueueActionName(entry), '采摘 凝气草')
    assert.equal(resolveWorldLocationName(world), '云溪村')
    await useI18nStore.getState().setLocale('en-US')
    assert.equal(resolveQueueActionName(entry), 'Gather: Qi-Condensing Grass')
    assert.equal(resolveWorldLocationName(world), 'Yunxi Village')
    assert.equal(resolveLocalizedEntityName(getT(), 'enemy', 'enemy.wood-spirit', '木灵'), 'Wood Spirit')
    assert.equal(resolveLocalizedEntityName(getT(), 'enemy', 'player.test', '木灵'), '木灵')
    assert.equal(resolveLocalizedEntityName(getT(), 'buff', 'formation.juling', '聚灵'), 'Spirit-Gathering')
    assert.equal(resolveLocalizedEntityName(getT(), 'buff', 'buff.ranhun', '燃烧'), 'Burning')
    assert.equal(
      localizeActionDisplayText('Crusade: Qingyun Immortal Mountain', world, [{ mapId: world.mapId, name: 'Qingyun Immortal Mountain' }]),
      'Crusade: Yunxi Village',
    )
    assert.equal(resolveWorldLocationName({ nodeId: null, locationName: '途中状态' }), '途中状态')
    assert.equal(resolveQueueActionName({ actionId: 'action.retired', actionName: 'action.retired' }), 'Unknown Action')
    assert.equal(entry.actionName, '采摘 凝气草')
    assert.equal(world.locationName, '云溪村')
    await useI18nStore.getState().setLocale('zh-CN')
    assert.equal(resolveQueueActionName(entry), '采摘 凝气草')
    assert.equal(resolveWorldLocationName(world), '云溪村')
    assert.equal(resolveLocalizedEntityName(getT(), 'enemy', 'enemy.wood-spirit', '木灵'), '木灵')
  } finally {
    globalThis.fetch = originalFetch
    await useI18nStore.getState().setLocale('zh-CN')
  }
})
