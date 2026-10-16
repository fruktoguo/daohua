import test from 'node:test'
import assert from 'node:assert/strict'
import {
  notifyIncomingChatMessage,
  notifyIncomingLogEvents,
  useLogUnreadStore,
} from './logUnreadStore.ts'

test('logUnreadStore：消息未读计数递增、切换分组自动清零、战斗分组严格不计未读', () => {
  useLogUnreadStore.getState().resetAll()
  useLogUnreadStore.getState().setActiveGroup('all')

  // 1. 收到世界频道消息：world 未读 +1，all 作为当前选中激活组保持 0
  notifyIncomingChatMessage({
    messageId: 'msg-1',
    channel: 'world',
    scopeKey: '',
    senderId: 'other-player',
    senderName: '道友甲',
    body: '世界好',
    sentAtUnixMilliseconds: 1000,
  }, 'self-player')

  let state = useLogUnreadStore.getState()
  assert.equal(state.unreadCounts.world, 1)
  assert.equal(state.unreadCounts.all, 0)

  // 2. 自己发出的消息严格不计未读
  notifyIncomingChatMessage({
    messageId: 'msg-2',
    channel: 'world',
    scopeKey: '',
    senderId: 'self-player',
    senderName: '自己',
    body: '我发的话',
    sentAtUnixMilliseconds: 1001,
  }, 'self-player')

  state = useLogUnreadStore.getState()
  assert.equal(state.unreadCounts.world, 1)

  // 3. 收到队伍消息：party +1，且因当前激活在 all，all 保持 0；若切到 world，再收队伍消息 all 应 +1
  useLogUnreadStore.getState().setActiveGroup('world')
  state = useLogUnreadStore.getState()
  // 切换到 world 时，world 未读自动清零！
  assert.equal(state.unreadCounts.world, 0)

  notifyIncomingChatMessage({
    messageId: 'msg-3',
    channel: 'party',
    scopeKey: 'party-1',
    senderId: 'teammate',
    senderName: '队友',
    body: '准备开怪',
    sentAtUnixMilliseconds: 1002,
  }, 'self-player')

  state = useLogUnreadStore.getState()
  assert.equal(state.unreadCounts.party, 1)
  assert.equal(state.unreadCounts.all, 1)

  // 4. 收到日志批次：system 增加未读，combat 严格不增加未读
  notifyIncomingLogEvents([
    { sequence: 1, timestampUnixMilliseconds: 1003, template: '系统提示：服务器将在 10 分钟后维护', channel: 'system' },
    { sequence: 2, timestampUnixMilliseconds: 1004, template: '你施放 连环，对铁甲熊造成 141.92 点伤害。', channel: 'combat' },
  ])

  state = useLogUnreadStore.getState()
  assert.equal(state.unreadCounts.system, 1)
  assert.equal(state.unreadCounts.combat, 0, '战斗频道绝不增加未读')
  assert.equal(state.unreadCounts.all, 2)

  // 5. 切换到 party：party 清零，其他组保持
  useLogUnreadStore.getState().setActiveGroup('party')
  state = useLogUnreadStore.getState()
  assert.equal(state.unreadCounts.party, 0)
  assert.equal(state.unreadCounts.system, 1)
  assert.equal(state.unreadCounts.all, 2)

  // 6. resetAll 全部清零
  useLogUnreadStore.getState().resetAll()
  state = useLogUnreadStore.getState()
  assert.equal(state.unreadCounts.system, 0)
  assert.equal(state.unreadCounts.all, 0)
})
