import test from 'node:test'
import assert from 'node:assert/strict'
import {
  HEAVEN_GATE_GUIDE_SEEN_STORAGE_KEY,
  consumeFirstOpenHeavenGateGuide,
  hasSeenHeavenGateGuide,
  markHeavenGateGuideSeen,
  resetHeavenGateGuideSessionFallbackForTest,
} from './heavenGateGuide.ts'

test('heavenGateGuide：首次消费返回 true 并写入存储，再次打开不再重复触发', () => {
  resetHeavenGateGuideSessionFallbackForTest()
  const store = new Map()
  const storage = {
    getItem(key) {
      return store.get(key) ?? null
    },
    setItem(key, value) {
      store.set(key, value)
    },
  }

  assert.equal(hasSeenHeavenGateGuide(storage), false)
  assert.equal(consumeFirstOpenHeavenGateGuide(storage), true)
  assert.equal(store.get(HEAVEN_GATE_GUIDE_SEEN_STORAGE_KEY), '1')
  assert.equal(hasSeenHeavenGateGuide(storage), true)
  assert.equal(consumeFirstOpenHeavenGateGuide(storage), false)
})

test('heavenGateGuide：主动打开说明弹窗后同样记为已读，避免后续自动弹窗', () => {
  resetHeavenGateGuideSessionFallbackForTest()
  const store = new Map()
  const storage = {
    getItem(key) {
      return store.get(key) ?? null
    },
    setItem(key, value) {
      store.set(key, value)
    },
  }

  markHeavenGateGuideSeen(storage)
  assert.equal(hasSeenHeavenGateGuide(storage), true)
  assert.equal(consumeFirstOpenHeavenGateGuide(storage), false)
})

test('heavenGateGuide：本地存储异常或受限时优雅降级为会话内仅弹出一次', () => {
  resetHeavenGateGuideSessionFallbackForTest()
  const brokenStorage = {
    getItem() {
      throw new Error('storage blocked')
    },
    setItem() {
      throw new Error('quota exceeded')
    },
  }

  assert.equal(consumeFirstOpenHeavenGateGuide(brokenStorage), true)
  assert.equal(hasSeenHeavenGateGuide(brokenStorage), true)
  assert.equal(consumeFirstOpenHeavenGateGuide(brokenStorage), false)

  resetHeavenGateGuideSessionFallbackForTest()
  assert.equal(consumeFirstOpenHeavenGateGuide(null), true)
  assert.equal(consumeFirstOpenHeavenGateGuide(null), false)
})
