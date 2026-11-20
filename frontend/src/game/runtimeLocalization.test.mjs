import assert from 'node:assert/strict'
import test from 'node:test'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

const root = process.cwd()
const output = path.join(tmpdir(), `daohua-runtime-localization-${process.pid}.mjs`)
await build({
  stdin: {
    contents: `export { resolveQueueActionName, resolveWorldLocationName, localizeActionDisplayText } from './frontend/src/game/ids.ts';
export { resolveLocalizedEntityName } from './frontend/src/game/entityNames.ts';
export { getT, LOCALES, useI18nStore } from './frontend/src/i18n/index.ts';`,
    resolveDir: root,
    loader: 'ts',
  },
  bundle: true,
  format: 'esm',
  outfile: output,
  logLevel: 'error',
})
const { resolveQueueActionName, resolveWorldLocationName, localizeActionDisplayText, resolveLocalizedEntityName, getT, LOCALES, useI18nStore } = await import(pathToFileURL(output).href)
test.after(() => fs.rmSync(output, { force: true }))

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
