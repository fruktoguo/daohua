import assert from 'node:assert/strict'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { build } from 'esbuild'

// 执行真实组件的来源切换，仅替代 React 提交与图片租约传输。
const { outputFiles: [bundle] } = await build({
  entryPoints: [fileURLToPath(new URL('./QqEmoji.tsx', import.meta.url))],
  bundle: true,
  format: 'esm',
  jsx: 'automatic',
  write: false,
  plugins: [{
    name: 'qq-emoji-source-test',
    setup(build) {
      build.onResolve({ filter: /^(react(?:\/jsx-runtime)?|\.\/useChatImageLease)$/ }, ({ path }) => ({ path, namespace: 'emoji-fixture' }))
      build.onLoad({ filter: /.*/, namespace: 'emoji-fixture' }, ({ path }) => ({
        contents: path === 'react'
          ? 'export const memo = value => value; export const useState = initial => globalThis.qqEmojiFixture.useState(initial)'
          : path === 'react/jsx-runtime'
            ? 'export const jsx = (type, props) => ({ type, props }); export const jsxs = jsx'
            : 'export const useChatImageGeneration = () => 0; export const useChatImageSource = url => globalThis.qqEmojiFixture.acquire(url)',
      }))
    },
  }],
})

test('内置表情按需取同源图，仅缺图时尝试外源，双源失败后停止请求', async t => {
  let source
  const requests = []
  globalThis.qqEmojiFixture = {
    useState(initial) {
      source ??= initial
      return [source, next => { source = next }]
    },
    acquire(url) { if (url) requests.push(url); return url },
  }
  t.after(() => { delete globalThis.qqEmojiFixture })
  const { QqEmoji } = await import(`data:text/javascript;base64,${Buffer.from(bundle.text).toString('base64')}`)
  assert.deepEqual(requests, [], '加载组件模块不得预取表情')
  const entry = QqEmoji({ name: '微笑' })
  const render = () => entry.type(entry.props)
  const local = render().props.children
  assert.equal(local.props.src, '/emotes/s14.gif')
  assert.deepEqual(requests, ['/emotes/s14.gif'])
  local.props.onError()
  const external = render().props.children
  assert.equal(external.props.src, 'https://img.yuohira.com/emotes/s14.gif')
  external.props.onError()
  assert.equal(render().props.children.join(''), '[微笑]')
  assert.equal(requests.length, 2, '双源失败后不得再次请求')
})
