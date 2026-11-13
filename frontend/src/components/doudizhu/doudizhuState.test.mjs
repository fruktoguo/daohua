import test from 'node:test'
import assert from 'node:assert/strict'
import { normalizeDouDiZhuGame, resolveDouDiZhuBidChoice } from './doudizhuState.ts'

test('斗地主状态归一化：还原服务端 WhenWritingNull 省略的可空字段', () => {
  // 模拟服务端 ProtocolJson.Options (WhenWritingNull) 下发的 JSON：所有 null 字段键均不存在
  const wirePayload = JSON.parse(JSON.stringify({
    gameId: '00000000-0000-0000-0000-000000000001',
    playerIds: ['p0', 'p1', 'p2'],
    baseStake: 10,
    phase: 'bidding',
    currentSeat: 1,
    hand: [0, 1, 2],
    handsRemaining: [17, 17, 17],
    bottomCards: [],
    robCount: 0,
    bombCount: 0,
    currentTrickCards: [],
    consecutivePasses: 0,
    history: [],
    version: 0,
    spring: false,
    finalMultiplier: 1,
    settlementDeltas: [0, 0, 0],
    turnDeadlineAtUnixMilliseconds: 10000,
    hostedSeats: [false, false, false],
    dealNumber: 0,
    dealStartedAtUnixMilliseconds: 0,
    actionAvailableAtUnixMilliseconds: 9000,
    serverNowUnixMilliseconds: 9500,
    dealHand: [0, 1, 2],
    bids: [],
  }))

  assert.equal(wirePayload.landlordSeat, undefined)
  assert.equal(resolveDouDiZhuBidChoice(wirePayload), 'call')

  const normalized = normalizeDouDiZhuGame(wirePayload)
  assert.ok(normalized !== null)
  assert.equal(normalized.landlordSeat, null)
  assert.equal(normalized.lastPlayedSeat, null)
  assert.equal(normalized.winnerSeat, null)
  assert.equal(normalized.landlordWon, null)
  assert.equal(normalized.landlordConfirmedAtUnixMilliseconds, null)
  assert.equal(resolveDouDiZhuBidChoice(normalized), 'call')
})

test('斗地主叫抢操作判定：0 号位已叫地主时不得误判为未叫', () => {
  const normalizedZero = normalizeDouDiZhuGame({
    gameId: '00000000-0000-0000-0000-000000000001',
    playerIds: ['p0', 'p1', 'p2'],
    baseStake: 10,
    phase: 'bidding',
    currentSeat: 1,
    hand: [0, 1, 2],
    handsRemaining: [17, 17, 17],
    bottomCards: [],
    landlordSeat: 0,
    robCount: 0,
    bombCount: 0,
    currentTrickCards: [],
    lastPlayedSeat: null,
    consecutivePasses: 0,
    history: [],
    version: 1,
    winnerSeat: null,
    landlordWon: null,
    spring: false,
    finalMultiplier: 1,
    settlementDeltas: [0, 0, 0],
    turnDeadlineAtUnixMilliseconds: 10000,
    hostedSeats: [false, false, false],
    dealNumber: 0,
    dealStartedAtUnixMilliseconds: 0,
    actionAvailableAtUnixMilliseconds: 9000,
    landlordConfirmedAtUnixMilliseconds: null,
    serverNowUnixMilliseconds: 9500,
    dealHand: [0, 1, 2],
    bids: [{ seat: 0, action: 'call', version: 1 }],
  })

  assert.ok(normalizedZero !== null)
  assert.equal(normalizedZero.landlordSeat, 0)
  assert.equal(resolveDouDiZhuBidChoice(normalizedZero), 'rob')
  assert.equal(resolveDouDiZhuBidChoice({ landlordSeat: 2 }), 'rob')
  assert.equal(normalizeDouDiZhuGame(null), null)
  assert.equal(normalizeDouDiZhuGame(undefined), null)
})
