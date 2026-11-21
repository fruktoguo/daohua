import '../../test-loader.mjs'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'

const [helpGuide, { zhCN }, { parseLogTemplate }, { TRAIT_DEFINITIONS }, { useUiStore }] = await Promise.all([
  import('./helpGuide.ts'),
  import('../i18n/locales/zh-CN.ts'),
  import('../logging/gameLogService.ts'),
  import('../game/combatConstants.ts'),
  import('../uiStore.ts'),
])
const mod = { ...helpGuide, zhCN, parseLogTemplate, TRAIT_DEFINITIONS, useUiStore }

function loadJsonDir(dir) {
  const fullDir = path.resolve(process.cwd(), dir)
  const records = []
  for (const file of fs.readdirSync(fullDir)) {
    if (!file.endsWith('.json')) continue
    const parsed = JSON.parse(fs.readFileSync(path.join(fullDir, file), 'utf8'))
    if (Array.isArray(parsed)) records.push(...parsed)
    else if (parsed && typeof parsed === 'object') records.push(parsed)
  }
  return records
}

function getByDotPath(obj, dotPath) {
  return dotPath.split('.').reduce((acc, part) => (acc && typeof acc === 'object' ? acc[part] : undefined), obj)
}

function createZhTranslator(zhDict) {
  return (key, params) => {
    const raw = getByDotPath(zhDict, key)
    if (typeof raw !== 'string') return key
    if (!params) return raw
    return raw.replace(/\{(\w+)\}/g, (_, token) => String(params[token] ?? `{${token}}`))
  }
}

test('帮助指南：五章十四节结构、全量 i18n 词条覆盖与富文本实体配表严格对齐', () => {
  assert.equal(mod.HELP_CHAPTERS.length, 5, '帮助指南应固定包含 5 个章节')
  assert.equal(mod.HELP_TOTAL_SECTIONS, 14, '帮助指南应固定包含 14 个小节')

  const items = new Map()
  for (const row of [...loadJsonDir('config/game-data/items'), ...loadJsonDir('config/game-data/equipment')]) {
    if (row.id) items.set(row.id, row)
  }
  const abilities = new Map(loadJsonDir('config/game-data/abilities').map(a => [a.id, a]))
  const actions = new Map(loadJsonDir('config/game-data/actions').map(a => [a.id, a]))
  const nodes = new Map()
  for (const map of loadJsonDir('config/game-data/maps')) {
    for (const node of map.nodes ?? []) {
      const nodeId = node.id ?? node.nodeId
      if (nodeId) nodes.set(nodeId, node)
    }
  }

  const requireZhValue = key => {
    const val = getByDotPath(mod.zhCN, key)
    assert.equal(typeof val, 'string', `缺失帮助词条键: ${key}`)
    assert.ok(val.trim().length > 0, `帮助词条内容不能为空: ${key}`)
    return val
  }

  for (const extraKey of [
    'app.nav.help',
    'app.nav.helpHint',
    'app.help.title',
    'app.help.chapterCountBadge',
    'app.help.hoverHint',
    'app.help.searchPlaceholder',
    'app.help.searchClear',
    'app.help.searchEmptyTitle',
    'app.help.searchEmptyDesc',
    'app.help.searchResultSummary',
    'app.help.chapterNavAria',
    'app.help.tipLabel',
    'app.help.prompt.kicker',
    'app.help.prompt.title',
    'app.help.prompt.body',
    'app.help.prompt.dismiss',
    'app.help.prompt.goHelp',
  ]) {
    requireZhValue(extraKey)
  }

  // 验证前期讨伐补给词条已由修炼丹「引气丹」修正为战斗药散「止血散」
  const earlyRouteBody = requireZhValue('app.help.chapters.intro.sections.earlyRoute.body')
  assert.ok(earlyRouteBody.includes('{{item:item.zhixue-powder|止血散}}'), '前期路线应精准引用 1 级战斗回血药「止血散」')
  assert.equal(earlyRouteBody.includes('item.qi-gathering-pill'), false, '前期战斗补给示例不应再误引探索修炼丹「引气丹」')

  let checkedRefs = 0
  const chapterIds = new Set()
  const sectionIds = new Set()

  for (const chapter of mod.HELP_CHAPTERS) {
    assert.ok(!chapterIds.has(chapter.id), `章节 ID 重复: ${chapter.id}`)
    chapterIds.add(chapter.id)
    requireZhValue(chapter.titleKey)
    requireZhValue(chapter.badgeKey)
    requireZhValue(chapter.summaryKey)
    assert.ok(chapter.sections.length > 0, `章节不能为空: ${chapter.id}`)

    for (const section of chapter.sections) {
      assert.equal(section.chapterId, chapter.id, `小节归属章节不一致: ${section.id}`)
      assert.ok(!sectionIds.has(section.id), `小节 ID 重复: ${section.id}`)
      sectionIds.add(section.id)

      requireZhValue(section.titleKey)
      requireZhValue(section.tagKey)
      requireZhValue(section.jumpLabelKey)
      const bodyText = requireZhValue(section.bodyKey)
      const tipText = requireZhValue(section.tipKey)

      for (const template of [bodyText, tipText]) {
        assert.doesNotMatch(template, /v\d+\.\d+\.\d+/, `正文或贴士不应硬编码版本号文本: ${section.id}`)
        const segments = mod.parseLogTemplate(template)
        for (const seg of segments) {
          if (seg.type !== 'reference') continue
          checkedRefs += 1
          switch (seg.kind) {
            case 'item': {
              const def = items.get(seg.id)
              assert.ok(def, `物品或装备 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `物品显示名与配表不一致: ${seg.id}`)
              break
            }
            case 'ability': {
              const def = abilities.get(seg.id)
              assert.ok(def, `功法 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `功法显示名与配表不一致: ${seg.id}`)
              break
            }
            case 'trait': {
              const def = mod.TRAIT_DEFINITIONS[seg.id]
              assert.ok(def, `特质 ID 不存在于 TRAIT_DEFINITIONS: ${seg.id}`)
              assert.equal(def.name, seg.label, `特质显示名与定义不一致: ${seg.id}`)
              break
            }
            case 'action': {
              const def = actions.get(seg.id)
              assert.ok(def, `行动 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `行动显示名与配表不一致: ${seg.id}`)
              break
            }
            case 'location': {
              const def = nodes.get(seg.id)
              assert.ok(def, `地点节点 ID 不存在于配表: ${seg.id}`)
              assert.equal(def.name, seg.label, `地点显示名与配表不一致: ${seg.id}`)
              break
            }
            default:
              assert.fail(`帮助指南存在未校验的富文本引用类型 ${seg.kind}: ${seg.id}`)
          }
        }
      }
    }
  }

  assert.ok(checkedRefs >= 14, `帮助指南富文本词条核验覆盖不足，当前仅 ${checkedRefs} 条`)
})

test('帮助指南：富文本标记剥离与多关键字搜索过滤逻辑正确', () => {
  const t = createZhTranslator(mod.zhCN)

  assert.equal(
    mod.stripRichMarkup('核心资源为 {{item:currency.spirit-stone|灵石}} 与 {{location:node.yunxi-village|云溪村}}。'),
    '核心资源为 灵石 与 云溪村。',
  )

  // 空查询应原样返回全部 5 章
  assert.equal(mod.filterHelpChapters(mod.HELP_CHAPTERS, '   ', t).length, 5)

  // 英文大小写不敏感匹配 0CD 与 PVP
  const zeroCdMatches = mod.filterHelpChapters(mod.HELP_CHAPTERS, '0cd', t)
  assert.ok(zeroCdMatches.some(c => c.sections.some(s => s.id === 'power-stats-skills')), '搜索 0cd 应命中功法避坑节')

  const pvpMatches = mod.filterHelpChapters(mod.HELP_CHAPTERS, 'pvp', t)
  assert.ok(pvpMatches.some(c => c.sections.some(s => s.id === 'craft-consumables')), '搜索 pvp 应命中战斗消耗品节')

  // 多词 AND 搜索（空格分词）
  const multiWordMatches = mod.filterHelpChapters(mod.HELP_CHAPTERS, '聚灵阵盘 1512', t)
  assert.equal(multiWordMatches.length, 1)
  assert.equal(multiWordMatches[0].sections[0].id, 'craft-liquefaction')

  // 不应匹配到富文本内部的实体 ID 前缀
  const rawIdMatches = mod.filterHelpChapters(mod.HELP_CHAPTERS, 'currency.spirit-stone', t)
  assert.equal(rawIdMatches.length, 0, '内部实体 ID 不应暴露进全文检索命中结果')

  // 彻底不相关的词返回空
  assert.equal(mod.filterHelpChapters(mod.HELP_CHAPTERS, '不存在的奇门术语xyz', t).length, 0)
})

test('界面状态：帮助独立弹窗、跳转收起、深层子页签定位与引导结束状态联动', () => {
  const store = mod.useUiStore

  // 记录原页签与「更多」分组落点；点击帮助仅弹出独立弹窗，不改变底层视图与导航记忆
  store.getState().setActiveTab('codex')
  store.getState().setActiveTab('combat')
  store.getState().setActiveTab('help')
  assert.equal(store.getState().helpModalOpen, true)
  assert.equal(store.getState().activeTab, 'combat')
  assert.equal(store.getState().mobileNavMemory.more, 'codex')

  // 点击其它常规页签会自动收起帮助弹窗
  store.getState().setActiveTab('insight')
  assert.equal(store.getState().helpModalOpen, false)
  assert.equal(store.getState().activeTab, 'insight')

  // 引导结束弹窗点「前往帮助」关闭提示弹窗并打开帮助独立弹窗
  store.getState().setHelpPromptOpen(true)
  assert.equal(store.getState().helpPromptOpen, true)
  store.getState().navigateToHelp()
  assert.equal(store.getState().helpPromptOpen, false)
  assert.equal(store.getState().helpModalOpen, true)
  assert.equal(store.getState().activeTab, 'insight')

  // 嵌套子页签跳转：技艺 -> 秘法 -> 化灵（同时自动关闭帮助弹窗）
  store.getState().executeHelpJump({ tab: 'crafting', craftingMode: 'enhancement', enhancementSubTab: 'liquefy' })
  assert.equal(store.getState().helpModalOpen, false)
  assert.equal(store.getState().activeTab, 'crafting')
  assert.equal(store.getState().craftingMode, 'enhancement')
  assert.equal(store.getState().enhancementSubTab, 'liquefy')
  assert.equal(store.getState().craftingNavExpanded, true)

  // 嵌套子页签跳转：地图 -> 附近建筑
  store.getState().setHelpModalOpen(true)
  store.getState().executeHelpJump({ tab: 'map', mapSubTab: 'buildings' })
  assert.equal(store.getState().helpModalOpen, false)
  assert.equal(store.getState().activeTab, 'map')
  assert.equal(store.getState().mapSubTab, 'buildings')

  // 嵌套子页签跳转：同道 -> 队伍
  store.getState().setHelpModalOpen(true)
  store.getState().executeHelpJump({ tab: 'social', socialSubTab: 'party' })
  assert.equal(store.getState().helpModalOpen, false)
  assert.equal(store.getState().activeTab, 'social')
  assert.equal(store.getState().socialSubTab, 'party')
  assert.equal(store.getState().socialNavExpanded, true)

  // 正文地点链接跳转同样自动关闭帮助弹窗
  store.getState().setHelpModalOpen(true)
  store.getState().navigateToMapNode('node.yunxi-village')
  assert.equal(store.getState().helpModalOpen, false)
  assert.equal(store.getState().activeTab, 'map')

  // 引导层可见态与外部跳过序号同步
  store.getState().setTutorialOverlayActive(true)
  assert.equal(store.getState().tutorialOverlayActive, true)
  const beforeSeq = store.getState().tutorialExternalSkipSeq
  store.getState().notifyTutorialExternalSkip()
  assert.equal(store.getState().tutorialExternalSkipSeq, beforeSeq + 1)
})
