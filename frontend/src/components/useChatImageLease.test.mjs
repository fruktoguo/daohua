import assert from 'node:assert/strict'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { build } from 'esbuild'

// 只驱动本 Hook 的 render/commit/cleanup：真实服务与网络不参与时序测试。
const { outputFiles: [bundle] } = await build({
  entryPoints: [fileURLToPath(new URL('./useChatImageLease.ts', import.meta.url))],
  bundle: true,
  format: 'esm',
  write: false,
  plugins: [{
    name: 'chat-image-lease-test',
    setup(build) {
      build.onResolve({ filter: /^(react|\.\.\/game\/chatStickerService)$/ }, ({ path }) => ({ path, namespace: 'lease-mock' }))
      build.onLoad({ filter: /.*/, namespace: 'lease-mock' }, ({ path }) => ({
        contents: path === 'react'
          ? 'export const { useEffect, useMemo, useState } = globalThis.chatImageLeaseHooks; export const useSyncExternalStore = (_, get) => get()'
          : 'export const chatStickerService = globalThis.chatImageLeaseService',
      }))
    },
  }],
})

function deferred() {
  let resolve, reject
  const promise = new Promise((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

async function mountHook(t) {
  let generation = 0
  let state, memo, deps, effect, cleanup
  const pending = []
  const same = (a, b) => a?.length === b.length && b.every((value, i) => Object.is(value, a[i]))
  globalThis.chatImageLeaseHooks = {
    useMemo(factory, next) {
      if (!same(memo?.deps, next)) memo = { value: factory(), deps: next }
      return memo.value
    },
    useState: initial => [state ?? initial, next => { state = next }],
    useEffect(setup, next) {
      if (!same(deps, next)) { deps = next; effect = setup }
    },
  }
  globalThis.chatImageLeaseService = {
    getGeneration: () => generation,
    subscribe: () => () => undefined,
    acquireImage(url) {
      const request = { url, ...deferred() }
      pending.push(request)
      return request.promise
    },
  }
  t.after(() => {
    cleanup?.()
    delete globalThis.chatImageLeaseHooks
    delete globalThis.chatImageLeaseService
  })
  // 每个用例隔离 import 的模块实例，避免沿用上一用例的 mock。
  const { useChatImageSource } = await import(`data:text/javascript;base64,${Buffer.from(bundle.text).toString('base64')}#${encodeURIComponent(t.name)}`)
  return {
    pending,
    switchSession: () => ++generation,
    render(url, scope = generation) {
      const src = useChatImageSource(url, scope)
      if (effect) {
        cleanup?.()
        cleanup = effect()
        effect = undefined
      }
      return src
    },
    unmount() { cleanup?.(); cleanup = undefined },
  }
}

function resolveLease(t, request, src) {
  const release = t.mock.fn()
  request.resolve({ src, release })
  return release.mock
}

test('换图和卸载释放租约，切回相同链接时不渲染已释放的地址', async t => {
  const view = await mountHook(t)
  view.render('/a.png')
  const first = resolveLease(t, view.pending[0], 'blob:a-old')
  await Promise.resolve()
  assert.equal(view.render('/a.png'), 'blob:a-old')
  assert.equal(view.render('/b.png'), null)
  assert.equal(first.callCount(), 1)
  assert.equal(view.render('/a.png'), null)
  const obsolete = resolveLease(t, view.pending[1], 'blob:b')
  const current = resolveLease(t, view.pending[2], 'blob:a-new')
  await Promise.resolve()
  assert.equal(obsolete.callCount(), 1)
  assert.equal(view.render('/a.png'), 'blob:a-new')
  view.unmount()
  assert.equal(current.callCount(), 1)
})

test('切会话和卸载后才返回的租约立即归还', async t => {
  const view = await mountHook(t)
  view.render('/a.png')
  view.switchSession()
  const stale = resolveLease(t, view.pending[0], 'blob:old-session')
  await Promise.resolve()
  assert.equal(stale.callCount(), 1)
  assert.equal(view.render('/a.png', 0), null, '即使旧 effect 尚未清理，也不能显示跨会话结果')
  view.render('/a.png')
  view.unmount()
  const unmounted = resolveLease(t, view.pending[1], 'blob:unmounted')
  await Promise.resolve()
  assert.equal(unmounted.callCount(), 1)
})

test('取图异常时回退到当前原始地址，错误态不再持有或请求图片', async t => {
  const view = await mountHook(t)
  view.render('/a.png')
  view.pending[0].reject(new Error('无法读入缓存'))
  await Promise.resolve()
  assert.equal(view.render('/a.png'), '/a.png')
  assert.equal(view.render(null), null)
  assert.equal(view.pending.length, 1)
})
