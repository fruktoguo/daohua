import test from 'node:test'
import assert from 'node:assert/strict'

globalThis.Worker = class {
  static instances = []
  constructor(url) {
    this.url = url
    this.listeners = new Map()
    this.messages = []
    this.terminated = false
    Worker.instances.push(this)
  }
  addEventListener(type, fn) {
    const list = this.listeners.get(type) ?? []
    list.push(fn)
    this.listeners.set(type, list)
  }
  postMessage(data) { this.messages.push(data) }
  terminate() { this.terminated = true }
  emit(type, data) { setTimeout(() => { for (const fn of this.listeners.get(type) ?? []) fn(data) }, 0) }
  ready() { this.emit('message', { data: { type: 'ready' } }) }
  respond(id, result, ok = true) { this.emit('message', { data: ok ? { id, ok: true, result } : { id, ok: false, error: result } }) }
  respondLast(result, ok = true) { setTimeout(() => this.respond(this.messages[this.messages.length - 1].id, result, ok), 0) }
  crash(message = 'boom') { this.emit('error', { message }) }
}

const {
  createSimulatorBridge,
  recommendedSimulatorConcurrency,
  runSimulatorBatch,
} = await import('./simulatorBridge.ts')

function reset() { Worker.instances = [] }

test('并发调用只创建一个 Worker 且 ready 前不发送', async () => {
  reset()
  const bridge = createSimulatorBridge()
  const a = bridge.run('{}')
  const b = bridge.run('{}')
  assert.equal(Worker.instances.length, 1)
  const worker = Worker.instances[0]
  assert.equal(worker.messages.length, 0, 'ready 前不得发送')
  worker.ready()
  worker.respond(1, 'ra')
  worker.respond(2, 'rb')
  assert.equal(await a, 'ra')
  assert.equal(await b, 'rb')
  assert.equal(worker.messages.length, 2)
})

test('ready 前 cancel 终止启动且迟到的 ready 不复活实例', async () => {
  reset()
  const bridge = createSimulatorBridge()
  const call = bridge.run('{}')
  bridge.cancelAll()
  const worker = Worker.instances[0]
  assert.equal(worker.terminated, true)
  worker.ready()
  await assert.rejects(call, /取消/)
})

test('ready 后 error 拒绝全部 pending 并可重启', async () => {
  reset()
  const bridge = createSimulatorBridge()
  const first = bridge.run('{}')
  Worker.instances[0].ready()
  Worker.instances[0].respondLast('warm')
  assert.equal(await first, 'warm')
  const pending = bridge.run('{}')
  Worker.instances[0].crash('kernel died')
  await assert.rejects(pending, /kernel died/)
  const again = bridge.run('{}')
  assert.equal(Worker.instances.length, 2)
  Worker.instances[1].ready()
  Worker.instances[1].respondLast('ok')
  assert.equal(await again, 'ok')
})

test('启动失败后可重新初始化', async () => {
  reset()
  const bridge = createSimulatorBridge()
  const bad = bridge.run('{}')
  Worker.instances[0].crash('load failed')
  await assert.rejects(bad)
  const retry = bridge.run('{}')
  assert.equal(Worker.instances.length, 2)
  Worker.instances[1].ready()
  Worker.instances[1].respondLast('ok')
  assert.equal(await retry, 'ok')
})

test('迟到响应不污染新 generation', async () => {
  reset()
  const bridge = createSimulatorBridge()
  const call = bridge.run('{}')
  const worker = Worker.instances[0]
  worker.ready()
  bridge.cancelAll()
  worker.respond(1, 'stale')
  await assert.rejects(call, /取消/)
  const next = bridge.run('{}')
  const worker2 = Worker.instances[1]
  worker2.ready()
  worker2.respondLast('fresh')
  assert.equal(await next, 'fresh')
})

test('warm 幂等预热配表并在 cancelAll 后重置', async () => {
  reset()
  const bridge = createSimulatorBridge()
  const p1 = bridge.warm()
  const p2 = bridge.warm()
  assert.equal(Worker.instances.length, 1)
  const w1 = Worker.instances[0]
  w1.ready()
  await new Promise(resolve => setTimeout(resolve, 0))
  assert.equal(w1.messages.length, 1)
  assert.equal(w1.messages[0].op, 'fingerprint')
  w1.respondLast('{"fingerprint":"abc"}')
  await Promise.all([p1, p2])
  // 已预热完毕再次 warm 不再重复发消息
  await bridge.warm()
  assert.equal(w1.messages.length, 1)
  // cancelAll 后再次 warm 需重建 Worker
  bridge.cancelAll()
  assert.equal(w1.terminated, true)
  const p3 = bridge.warm()
  assert.equal(Worker.instances.length, 2)
  const w2 = Worker.instances[1]
  w2.ready()
  await new Promise(resolve => setTimeout(resolve, 0))
  w2.respondLast('{"fingerprint":"abc"}')
  await p3
})

test('recommendedSimulatorConcurrency 在移动端与低内存设备严格限制为 1', () => {
  assert.equal(recommendedSimulatorConcurrency({
    userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) Mobile',
    hardwareConcurrency: 8,
    deviceMemory: 8,
    coarsePointer: false,
    innerWidth: 1200,
  }), 1)
  assert.equal(recommendedSimulatorConcurrency({
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
    hardwareConcurrency: 8,
    deviceMemory: 4,
    coarsePointer: false,
    innerWidth: 1280,
  }), 1)
  assert.equal(recommendedSimulatorConcurrency({
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
    hardwareConcurrency: 8,
    deviceMemory: 8,
    coarsePointer: true,
    innerWidth: 1280,
  }), 1)
  assert.equal(recommendedSimulatorConcurrency({
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
    hardwareConcurrency: 8,
    deviceMemory: 8,
    coarsePointer: false,
    innerWidth: 960,
  }), 1)
  assert.equal(recommendedSimulatorConcurrency({
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
    hardwareConcurrency: 8,
    deviceMemory: 8,
    coarsePointer: false,
    innerWidth: 1440,
  }), 2)
})

test('runSimulatorBatch 按并发上限调度并在完成后回收辅助 Worker', async () => {
  reset()
  const primary = createSimulatorBridge()
  const batch = runSimulatorBatch(['{"i":0}', '{"i":1}', '{"i":2}', '{"i":3}'], {
    primaryBridge: primary,
    concurrency: 2,
    durationSeconds: 3600,
  })
  assert.equal(batch.concurrency, 2)
  assert.equal(Worker.instances.length, 2)
  const [w0, w1] = Worker.instances
  w0.ready()
  w1.ready()
  await new Promise(resolve => setTimeout(resolve, 0))
  w0.respondLast('r0')
  w1.respondLast('r1')
  await new Promise(resolve => setTimeout(resolve, 0))
  w0.respondLast('r2')
  w1.respondLast('r3')
  const out = await batch.promise
  assert.deepEqual(out, ['r0', 'r1', 'r2', 'r3'])
  // 辅助 Worker 在完成后自动 terminate，主 Worker 保持存活复用
  assert.equal(w0.terminated, false)
  assert.equal(w1.terminated, true)
})

test('runSimulatorBatch 少于 3 项或并发为 1 时不创建额外 Worker 且 cancel 会停止全部 Worker', async () => {
  reset()
  const primary = createSimulatorBridge()
  const seenConcurrencies = []
  const smallBatch = runSimulatorBatch(['{"i":0}', '{"i":1}'], {
    primaryBridge: primary,
    concurrency: 2,
    durationSeconds: 3600,
    onItemStart: (_index, _started, _total, activeConcurrency) => seenConcurrencies.push(['start', activeConcurrency]),
    onItemComplete: (_index, _res, _done, _total, activeConcurrency) => seenConcurrencies.push(['done', activeConcurrency]),
  })
  assert.equal(smallBatch.concurrency, 1)
  assert.equal(Worker.instances.length, 1)
  Worker.instances[0].ready()
  await new Promise(resolve => setTimeout(resolve, 0))
  Worker.instances[0].respondLast('s0')
  await new Promise(resolve => setTimeout(resolve, 0))
  Worker.instances[0].respondLast('s1')
  assert.deepEqual(await smallBatch.promise, ['s0', 's1'])
  assert.deepEqual(seenConcurrencies, [
    ['start', 1],
    ['done', 1],
    ['start', 1],
    ['done', 1],
  ])

  const bigBatch = runSimulatorBatch(['{"i":0}', '{"i":1}', '{"i":2}'], {
    primaryBridge: primary,
    concurrency: 2,
    durationSeconds: 3600,
  })
  assert.equal(Worker.instances.length, 2)
  bigBatch.cancel()
  assert.equal(Worker.instances[0].terminated, true)
  assert.equal(Worker.instances[1].terminated, true)
  await assert.rejects(bigBatch.promise, /取消/)
})

test('listCombatNodeIds 通过 Worker 转发 op 并返回 JSON 节点清单', async () => {
  reset()
  const bridge = createSimulatorBridge()
  const callPromise = bridge.listCombatNodeIds()
  assert.equal(Worker.instances.length, 1)
  Worker.instances[0].ready()
  await new Promise(resolve => setTimeout(resolve, 0))
  const sent = Worker.instances[0].messages.at(-1)
  assert.equal(sent.op, 'listCombatNodeIds')
  Worker.instances[0].respondLast('["node.yunxi-village"]')
  assert.equal(await callPromise, '["node.yunxi-village"]')
})
