import test from 'node:test'
import assert from 'node:assert/strict'
import {
  getInteractionStats,
  resetInteractionGuardForTest,
  verifyTrustedAction,
} from './interactionGuard.ts'

test('交互探针：verifyTrustedAction 正确识别 isTrusted 标记', () => {
  resetInteractionGuardForTest()

  // 1. 原生信任事件
  const trustedEvent = { isTrusted: true }
  assert.equal(verifyTrustedAction(trustedEvent), true)
  assert.equal(getInteractionStats().untrustedClicks, 0)

  // 2. 脚本合成非信任事件 (element.click / dispatchEvent)
  const untrustedEvent = { isTrusted: false }
  assert.equal(verifyTrustedAction(untrustedEvent), false)
  assert.equal(getInteractionStats().untrustedClicks, 1)

  // 3. React 合成事件带 nativeEvent
  const reactSyntheticEvent = { nativeEvent: { isTrusted: false } }
  assert.equal(verifyTrustedAction(reactSyntheticEvent), false)
  assert.equal(getInteractionStats().untrustedClicks, 2)
})

test('交互探针：空事件安全放行', () => {
  resetInteractionGuardForTest()
  assert.equal(verifyTrustedAction(null), true)
  assert.equal(verifyTrustedAction(undefined), true)
})
