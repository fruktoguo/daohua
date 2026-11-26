import '../../test-loader.mjs'
import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'

const {
  applyEditorInsertion,
  countUnicodeRunes,
  GUIDE_KAOMOJI_CATEGORIES,
  GUIDE_MAX_BODY_RUNES,
  parseGuideBlocks,
  parseGuideInlines,
  sanitizeSafeHttpImageUrl,
} = await import('./guideMarkup.ts')

test('guideMarkup：Unicode Rune 字数计数对含 Emoji 与颜文字的攻略正文精确对齐', () => {
  assert.equal(GUIDE_MAX_BODY_RUNES, 10_000)
  assert.equal(countUnicodeRunes('剑修攻略😀⚔️'), 7)
  assert.ok(GUIDE_KAOMOJI_CATEGORIES.length >= 4)
  for (const cat of GUIDE_KAOMOJI_CATEGORIES) {
    assert.ok(cat.items.length >= 5)
  }
})

test('guideMarkup：自动解析内置表情、颜文字、裸图片链接、Markdown 图片与物品胶囊，并拦截危险协议', () => {
  const inlines = parseGuideInlines(
    '起手[微笑]接 **破军** (๑•̀ㅂ•́)و✧ 看图 https://img.example.com/build.webp?v=1紧跟中文 ![面板](https://img.example.com/stat.png) 配 {{item:equipment.xuantie-sword:5}}',
  )

  assert.ok(inlines.some(n => n.type === 'qqEmoji' && n.name === '微笑'))
  assert.ok(inlines.some(n => n.type === 'bold'))
  assert.ok(inlines.some(n => n.type === 'text' && n.text.includes('(๑•̀ㅂ•́)و✧')))
  assert.ok(inlines.some(n => n.type === 'image' && n.url === 'https://img.example.com/build.webp?v=1'))
  assert.ok(inlines.some(n => n.type === 'text' && n.text.startsWith('紧跟中文')))
  assert.ok(inlines.some(n => n.type === 'image' && n.url === 'https://img.example.com/stat.png' && n.alt === '面板'))
  assert.ok(inlines.some(n => n.type === 'item' && n.itemId === 'equipment.xuantie-sword' && n.enhancementLevel === 5))

  assert.equal(sanitizeSafeHttpImageUrl('javascript:alert(1)'), null)
  assert.equal(sanitizeSafeHttpImageUrl('data:image/png;base64,AAAA'), null)
})

test('guideMarkup：块级 AST 正确切分标题、引用、列表、代码块与分割线，且编辑器插入保留选区语义', () => {
  const doc = [
    '# 剑修筑基指南',
    '> 核心思路：叠暴击',
    '- 青锋剑',
    '- 玄铁甲',
    '---',
    '```',
    'crit >= 45%',
    '```',
  ].join('\n')

  const blocks = parseGuideBlocks(doc)
  assert.deepEqual(
    blocks.map(b => b.type),
    ['heading', 'blockquote', 'unorderedList', 'hr', 'codeBlock'],
  )

  const wrapped = applyEditorInsertion('剑修核心词条', 2, 4, { prefix: '**', suffix: '**' })
  assert.equal(wrapped.value, '剑修**核心**词条')
})

test('静态代码契约：攻略渲染器与名片付费查阅所有物品/灵石视觉组件必须显式携带 itemId', async () => {
  const richContent = await readFile(new URL('../components/GuideRichContent.tsx', import.meta.url), 'utf8')
  assert.match(
    richContent,
    /<ItemReference[\s\S]*?itemId=\{node\.itemId\}[\s\S]*?\/>/,
    'GuideRichContent 中的 ItemReference 必须显式透传 itemId',
  )

  for (const relPath of [
    '../components/GuidePage.tsx',
    '../components/GuideEditorModal.tsx',
    '../components/PlayerCardModal.tsx',
    '../components/SettingsPage.tsx',
  ]) {
    const src = await readFile(new URL(relPath, import.meta.url), 'utf8')
    const icons = src.match(/<ItemIcon\b[^>]*>/g) ?? []
    assert.ok(icons.length > 0, `${relPath} 应当使用 ItemIcon 展示灵石或物品图标`)
    for (const tag of icons) {
      assert.match(tag, /\bitemId=/, `${relPath} 中的 ${tag} 必须显式传入 itemId`)
    }
  }
})
