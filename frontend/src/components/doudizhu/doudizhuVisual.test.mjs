import '../../../test-loader.mjs'
import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'

const { CardFace, VisualSymbols } = await import('./doudizhuSvg.tsx')

const jokers = { small: '小王', big: '大王' }
const face = (card, labels = jokers) => renderToStaticMarkup(createElement(CardFace, { card, jokers: labels }))

test('牌桌 SVG：四种花色映射、双头牌面、A 与大小王图案保留', () => {
  const defs = renderToStaticMarkup(createElement(VisualSymbols))
  for (const [card, suit] of [2, 3, 1, 0].entries()) {
    const markup = face(card)
    assert.match(markup, /viewBox="0 0 76 108" preserveAspectRatio="xMidYMid meet"/)
    assert.deepEqual([...markup.matchAll(/<use href="#ddz-suit-(\d)"/g)].map(m => Number(m[1])), Array(5).fill(suit))
    assert.match(markup, /translate\(76 108\) rotate\(180\)/)
    assert.match(defs, new RegExp(`<symbol id="ddz-suit-${suit}" viewBox="0 0 32 36"><path`))
  }
  assert.match(face(40), /translate\(90 144\) rotate\(180\)/, 'K 保留镜像人物图案')
  assert.match(face(44), />DAOHUA<\/text>/, 'A 保留中央花色与道化印记')
  assert.match(face(52), />小<\/tspan><tspan[^>]*>王<\/tspan>/)
  assert.match(face(53), />大<\/tspan><tspan[^>]*>王<\/tspan>/)
  for (const prop of ['prop-tomato', 'prop-flower', 'prop-tea', 'prop-applause', 'impact-tomato']) {
    assert.ok(defs.includes(`<symbol id="ddz-${prop}"`), `动效仍有 ${prop} 图案`)
  }
})

test('牌桌 SVG：翻译始终是文本，静态牌面不经 HTML 注入', () => {
  const markup = face(52, { small: '<王>', big: '大王' })
  assert.match(markup, />&lt;<\/tspan><tspan[^>]*>王<\/tspan><tspan[^>]*>&gt;<\/tspan>/)
})
