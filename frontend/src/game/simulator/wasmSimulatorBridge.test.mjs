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

const { createSimulatorWasmBridge } = await import('./wasmSimulatorBridge.ts')

function reset() { Worker.instances = [] }

test('并发调用只创建一个 Worker 且 ready 前不发送', async () => {
  reset()
  const bridge = createSimulatorWasmBridge()
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
  const bridge = createSimulatorWasmBridge()
  const call = bridge.run('{}')
  bridge.cancelAll()
  const worker = Worker.instances[0]
  assert.equal(worker.terminated, true)
  worker.ready()
  await assert.rejects(call, /取消/)
})

test('ready 后 error 拒绝全部 pending 并可重启', async () => {
  reset()
  const bridge = createSimulatorWasmBridge()
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
  const bridge = createSimulatorWasmBridge()
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
  const bridge = createSimulatorWasmBridge()
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
