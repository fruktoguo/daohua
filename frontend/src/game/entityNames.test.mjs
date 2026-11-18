import assert from 'node:assert/strict'
import test from 'node:test'
import { getT } from '../i18n/index.ts'
import { readableEntityName, resolveAssetName, resolveEntityName } from './entityNames.ts'

test('物品或装备名称缺失时，从本地词典恢复而不是显示 ID 或词条键', () => {
  const t = getT()
  const id = 'equipment.juling-pendant'
  assert.equal(resolveAssetName(t, id, '', '  ', id, `${id} +4`, `game.item.${id}.label`), '聚灵佩')
  assert.equal(resolveAssetName(t, id, undefined, ' 自定义佩饰 '), '自定义佩饰')
  assert.equal(resolveAssetName(t, 'equipment.retired'), '未知装备')
  assert.equal(resolveAssetName(t, 'item.retired'), '未知物品')
})

test('富文本实体名称丢失时优先采用目录名，无法恢复时保留类别', () => {
  const t = getT()
  assert.equal(resolveEntityName(t, 'ability', 'ability.tide', 'ability.tide', '潮汐'), '潮汐')
  assert.equal(resolveEntityName(t, 'location', 'node.retired', ' '), '未知地点')
  assert.equal(readableEntityName('ability.foo', 'St. Elmo'), 'St. Elmo')
  assert.equal(readableEntityName('equipment.juling-pendant', '聚灵佩 +4'), '聚灵佩 +4')
})
