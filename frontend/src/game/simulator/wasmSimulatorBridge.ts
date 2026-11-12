export interface SimulatorWasmBridge {
  catalogFingerprint(): Promise<string>
  createDefaultProfile(): Promise<string>
  resolveProfiles(requestJson: string): Promise<string>
  run(requestJson: string): Promise<string>
  cancelAll(): void
}

interface PendingCall {
  resolve: (value: string) => void
  reject: (error: Error) => void
}

export function createSimulatorWasmBridge(workerUrl = '/simulator/main.js'): SimulatorWasmBridge {
  let readyWorker: Worker | null = null
  let startingWorker: Worker | null = null
  let readyPromise: Promise<Worker> | null = null
  let startupReject: ((error: Error) => void) | null = null
  let readyTimer: ReturnType<typeof setTimeout> | null = null
  let generation = 0
  let sequence = 0
  const pending = new Map<number, PendingCall>()

  const teardown = (error: Error, expected: number) => {
    if (expected !== generation) return
    generation += 1
    for (const call of pending.values()) call.reject(error)
    pending.clear()
    startupReject?.(error)
    startupReject = null
    if (readyTimer) { clearTimeout(readyTimer); readyTimer = null }
    const instance = startingWorker ?? readyWorker
    startingWorker = null
    readyWorker = null
    readyPromise = null
    instance?.terminate()
  }

  const warm = (): Promise<Worker> => {
    if (readyWorker) return Promise.resolve(readyWorker)
    if (readyPromise) return readyPromise
    const ticket = generation
    readyPromise = new Promise<Worker>((resolve, reject) => {
      startupReject = reject
      if (typeof Worker === 'undefined') {
        return reject(new Error('当前浏览器不支持 Web Worker，无法运行战斗预演内核。'))
      }
      let instance: Worker
      try {
        instance = new Worker(workerUrl, { type: 'module' })
      } catch (error) {
        return reject(error instanceof Error ? error : new Error(String(error)))
      }
      startingWorker = instance
      const ownsInstance = () => startingWorker === instance || readyWorker === instance
      const fail = (error: Error) => {
        reject(error)
        teardown(error, ticket)
      }
      readyTimer = setTimeout(() => fail(new Error('战斗预演内核加载超时。')), 300_000)
      instance.addEventListener('error', (event) => {
        if (!ownsInstance()) return
        teardown(new Error(event.message || '战斗预演内核发生错误。'), ticket)
      })
      instance.addEventListener('messageerror', () => {
        if (ownsInstance()) teardown(new Error('战斗预演内核消息解码失败。'), ticket)
      })
      instance.addEventListener('message', (event) => {
        if (!ownsInstance()) return
        const data = event.data as { type?: string; id?: number; ok?: boolean; result?: string; error?: string }
        if (data.type === 'ready') {
          if (readyTimer) { clearTimeout(readyTimer); readyTimer = null }
          if (startingWorker === instance) startingWorker = null
          readyWorker = instance
          return resolve(instance)
        }
        if (typeof data.id !== 'number') return
        const call = pending.get(data.id)
        if (!call) return
        pending.delete(data.id)
        if (data.ok) call.resolve(data.result ?? '')
        else call.reject(new Error(data.error || '战斗预演内核返回未知错误。'))
      })
    }).catch((error: unknown) => {
      teardown(error instanceof Error ? error : new Error(String(error)), ticket)
      throw error
    })
    return readyPromise
  }

  const call = async (op: string, payload?: string): Promise<string> => {
    const instance = await warm()
    if (readyWorker !== instance) throw new Error('战斗预演已取消。')
    const id = ++sequence
    return new Promise<string>((resolve, reject) => {
      pending.set(id, { resolve, reject })
      try {
        instance.postMessage({ id, op, payload })
      } catch (error) {
        pending.delete(id)
        const failure = error instanceof Error ? error : new Error(String(error))
        teardown(failure, generation)
        reject(failure)
      }
    })
  }

  return {
    catalogFingerprint: () => call('fingerprint'),
    createDefaultProfile: () => call('createDefaultProfile'),
    resolveProfiles: (requestJson) => call('resolveProfiles', requestJson),
    run: (requestJson) => call('run', requestJson),
    cancelAll: () => teardown(new Error('战斗预演已取消。'), generation)
  }
}
