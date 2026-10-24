import test from 'node:test'
import assert from 'node:assert/strict'
import {
  chatService,
  selectAggregateMessages,
  selectChannelMessages,
  selectFriendMessages,
} from './chatService.ts'

const message = (id, channel, scopeKey, body, sentAtMs, senderId = 'p-sender', senderName = '青云子') => ({
  messageId: id, channel, scopeKey, senderId, senderName, body, sentAtUnixMilliseconds: sentAtMs,
})

test('chatService：实时消息按会话分组、同 ID 去重、组内时间正序', () => {
  chatService.reset()
  chatService.activate('p-self')
  chatService.appendMessage(message('m2', 'world', '', '第二条', 2000))
  chatService.appendMessage(message('m1', 'world', '', '第一条', 1000))
  chatService.appendMessage(message('m1', 'world', '', '第一条', 1000))

  const snapshot = chatService.getSnapshot()
  const world = selectChannelMessages(snapshot, 'world')
  assert.deepEqual(world.map(entry => entry.body), ['第一条', '第二条'])
  assert.equal(snapshot.receivedCount, 2)
})

test('chatService：历史回放整段替换会话并登记作用域键', () => {
  chatService.reset()
  chatService.activate('p-self')
  chatService.appendMessage(message('stale', 'nearby', 'node-a', '旧节点消息', 500))
  chatService.applyHistory({
    channel: 'nearby',
    scopeKey: 'node-b',
    targetPlayerId: null,
    messages: [message('h1', 'nearby', 'node-b', '新节点消息', 1000)],
  })

  const snapshot = chatService.getSnapshot()
  assert.deepEqual(selectChannelMessages(snapshot, 'nearby').map(entry => entry.body), ['新节点消息'])
})

test('chatService：好友私聊按 targetPlayerId 登记的作用域键隔离不同会话', () => {
  chatService.reset()
  chatService.activate('p-self')
  chatService.applyHistory({
    channel: 'friend',
    scopeKey: 'a:b',
    targetPlayerId: 'friend-a',
    messages: [message('f1', 'friend', 'a:b', '给甲的密语', 1000)],
  })
  chatService.applyHistory({
    channel: 'friend',
    scopeKey: 'a:c',
    targetPlayerId: 'friend-b',
    messages: [message('f2', 'friend', 'a:c', '给乙的密语', 2000)],
  })

  const snapshot = chatService.getSnapshot()
  assert.deepEqual(selectFriendMessages(snapshot, 'friend-a').map(entry => entry.body), ['给甲的密语'])
  assert.deepEqual(selectFriendMessages(snapshot, 'friend-b').map(entry => entry.body), ['给乙的密语'])
})

test('chatService：综合分组只合并好友私聊与队伍群聊，不包含世界/附近', () => {
  chatService.reset()
  chatService.activate('p-self')
  chatService.applyHistory({
    channel: 'party', scopeKey: 'party-1', targetPlayerId: null,
    messages: [message('p1', 'party', 'party-1', '队伍消息', 3000)],
  })
  chatService.applyHistory({
    channel: 'friend', scopeKey: 'a:b', targetPlayerId: 'friend-a',
    messages: [message('f1', 'friend', 'a:b', '好友消息', 1000)],
  })
  chatService.appendMessage(message('w1', 'world', '', '世界消息', 2000))
  chatService.appendMessage(message('n1', 'nearby', 'node-a', '附近消息', 2500))

  const aggregate = selectAggregateMessages(chatService.getSnapshot())
  assert.deepEqual(aggregate.map(entry => entry.body), ['好友消息', '队伍消息'])
})

test('chatService：每个会话只保留最近 100 条', () => {
  chatService.reset()
  chatService.activate('p-self')
  for (let index = 0; index < 130; index += 1) {
    chatService.appendMessage(message(`m${index}`, 'world', '', `第 ${index} 条`, 1000 + index))
  }

  const world = selectChannelMessages(chatService.getSnapshot(), 'world')
  assert.equal(world.length, 100)
  assert.equal(world[0].body, '第 30 条')
  assert.equal(world[99].body, '第 129 条')
})
