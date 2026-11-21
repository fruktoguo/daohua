import test from 'node:test'
import assert from 'node:assert/strict'
import {
  getInteractionStats,
  installInteractionGuard,
  resetInteractionGuardForTest,
} from './interactionGuard.ts'

test('交互探针：在捕获阶段拦截非信任 click/pointerdown 并累计 untrustedClicks，放行真实事件', () => {
  const listeners = new Map()
  const prevWindow = globalThis.window
  globalThis.window = {
    addEventListener(type, handler) {
      listeners.set(type, handler)
    },
  }
  try {
    resetInteractionGuardForTest()
    installInteractionGuard()
    assert.deepEqual([...listeners.keys()], ['click', 'pointerdown'])

    let prevented = false
    let stopped = false
    listeners.get('click')({
      isTrusted: true,
      preventDefault() { prevented = true },
      stopImmediatePropagation() { stopped = true },
    })
    assert.equal(prevented, false)
    assert.equal(stopped, false)
    assert.equal(getInteractionStats().untrustedClicks, 0)

    listeners.get('pointerdown')({
      isTrusted: false,
      preventDefault() { prevented = true },
      stopImmediatePropagation() { stopped = true },
    })
    assert.equal(prevented, true)
    assert.equal(stopped, true)
    assert.equal(getInteractionStats().untrustedClicks, 1)
  } finally {
    globalThis.window = prevWindow
    resetInteractionGuardForTest()
  }
})
