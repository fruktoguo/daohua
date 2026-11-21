import '../../test-loader.mjs'
import assert from 'node:assert/strict'
import test from 'node:test'

const { selectPartyLoadoutLocked, useSocialStore } = await import('./socialStore.ts')

const applyParty = (sessionId = null) => useSocialStore.getState().applyState({
  friends: [], incomingRequests: [], outgoingRequests: [], nearbyPlayers: [],
  party: { activeSession: sessionId === null ? null : { sessionId } },
})
const isLocked = () => selectPartyLoadoutLocked(useSocialStore.getState())
const unlock = sessionId => useSocialStore.getState().unlockPartyLoadout(sessionId)

test('组队装配确认只解锁当前会话，不随重复推送重锁或传到下一场', () => {
  assert.equal(isLocked(), false)
  applyParty()
  assert.equal(isLocked(), false, '组队但未开打时不限装配')
  applyParty('first')
  assert.equal(isLocked(), true)

  unlock('first')
  applyParty('first')
  assert.equal(isLocked(), false)

  applyParty('second')
  unlock('first')
  assert.equal(isLocked(), true, '上一场尚未确认的弹窗不能解锁新一场')

  unlock('second')
  applyParty()
  assert.equal(isLocked(), false, '散场后恢复常规编辑')
  assert.equal(useSocialStore.getState().unlockedPartyLoadoutSessionId, null)
})
