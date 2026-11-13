export interface SimulatorBridge {
  warm(): Promise<void>
  catalogFingerprint(): Promise<string>
  listCombatNodeIds(): Promise<string>
  createDefaultProfile(): Promise<string>
  resolveProfiles(requestJson: string): Promise<string>
  run(requestJson: string): Promise<string>
  cancelAll(): void
}

export interface SimulatorConcurrencyEnv {
  userAgent?: string
  deviceMemory?: number
  hardwareConcurrency?: number
  coarsePointer?: boolean
  innerWidth?: number
}

export interface SimulatorBatchHandle {
  concurrency: number
  promise: Promise<string[]>
  cancel: () => void
}

export interface SimulatorBatchOptions {
  primaryBridge: SimulatorBridge
  workerUrl?: string | URL
  concurrency?: number
  durationSeconds?: number
  onItemStart?: (index: number, startedCount: number, totalCount: number, activeConcurrency: number) => void
  onItemComplete?: (index: number, resultJson: string, completedCount: number, totalCount: number, activeConcurrency: number) => void
}

/** 计算适合当前设备的并行推演 Worker 数：移动端与低内存设备严格限制为 1，桌面端封顶为 2。 */
export function recommendedSimulatorConcurrency(env?: SimulatorConcurrencyEnv): number {
  const nav = typeof navigator !== 'undefined' ? navigator : undefined
  const win = typeof window !== 'undefined' ? window : undefined
  const ua = env?.userAgent ?? (typeof nav?.userAgent === 'string' ? nav.userAgent : '')
  if (/Android|iPhone|iPad|iPod|Mobile/i.test(ua)) return 1

  const mem = env?.deviceMemory ?? (nav as { deviceMemory?: number } | undefined)?.deviceMemory
  if (typeof mem === 'number' && mem > 0 && mem <= 4) return 1

  const coarse = env?.coarsePointer ?? (typeof win?.matchMedia === 'function' ? win.matchMedia('(pointer: coarse)').matches : false)
  if (coarse) return 1

  const width = env?.innerWidth ?? (typeof win?.innerWidth === 'number' ? win.innerWidth : 0)
  if (width > 0 && width <= 960) return 1

  const cores = env?.hardwareConcurrency ?? (typeof nav?.hardwareConcurrency === 'number' ? nav.hardwareConcurrency : 2)
  return cores >= 4 ? 2 : 1
}

interface PendingCall {
  resolve: (value: string) => void
  reject: (error: Error) => void
}

export function createSimulatorBridge(
  workerUrl?: string | URL,
): SimulatorBridge {
  let readyWorker: Worker | null = null
  let startingWorker: Worker | null = null
  let readyPromise: Promise<Worker> | null = null
  let startupReject: ((error: Error) => void) | null = null
  let readyTimer: ReturnType<typeof setTimeout> | null = null
  let generation = 0
  let sequence = 0
  let warmed = false
  let warmPromise: Promise<void> | null = null
  const pending = new Map<number, PendingCall>()

  const teardown = (error: Error, expected: number) => {
    if (expected !== generation) return
    generation += 1
    warmed = false
    warmPromise = null
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

  const ensureWorker = (): Promise<Worker> => {
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
        // 必须保留字面量 new Worker(new URL(...)) 以便 Vite 生产构建自动打包编译 Worker 及其懒加载配表
        instance = workerUrl !== undefined
          ? new Worker(workerUrl, { type: 'module' })
          : new Worker(new URL('./simulatorWorker.ts', import.meta.url), { type: 'module' })
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
    const instance = await ensureWorker()
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

  /** 预热 Worker 与懒加载配表反序列化，避免首次点击模拟冷启动等待。 */
  const warm = (): Promise<void> => {
    if (warmed && readyWorker) return Promise.resolve()
    if (warmPromise) return warmPromise
    const ticket = generation
    warmPromise = call('fingerprint')
      .then(() => {
        if (generation === ticket) warmed = true
      })
      .finally(() => {
        if (generation === ticket && !warmed) warmPromise = null
      })
    return warmPromise
  }

  return {
    warm,
    catalogFingerprint: () => call('fingerprint'),
    listCombatNodeIds: () => call('listCombatNodeIds'),
    createDefaultProfile: () => call('createDefaultProfile'),
    resolveProfiles: (requestJson) => call('resolveProfiles', requestJson),
    run: (requestJson) => call('run', requestJson),
    cancelAll: () => teardown(new Error('战斗预演已取消。'), generation),
  }
}

/** 批量执行多组推演请求：移动端固定单 Worker；桌面端在重负载多组合时按需拉起临时辅助 Worker 并行。 */
export function runSimulatorBatch(
  requests: readonly string[],
  options: SimulatorBatchOptions,
): SimulatorBatchHandle {
  const { primaryBridge, workerUrl, durationSeconds = 3600, onItemStart, onItemComplete } = options
  if (requests.length === 0) {
    return { concurrency: 1, promise: Promise.resolve([]), cancel: () => {} }
  }

  const requestedConcurrency = Math.max(1, Math.min(2, options.concurrency ?? recommendedSimulatorConcurrency()))
  // 辅助 Worker 需要一次独立模块与配表初始化；仅在多组合长时长下开启双 Worker 分担计算
  const shouldParallel = requestedConcurrency > 1
    && requests.length >= 3
    && (durationSeconds >= 3600 || requests.length >= 4)
  const workerCount = shouldParallel ? requestedConcurrency : 1
  const helpers: SimulatorBridge[] = []
  for (let i = 1; i < workerCount; i += 1) {
    helpers.push(createSimulatorBridge(workerUrl))
  }
  const bridges = [primaryBridge, ...helpers]

  let cancelled = false
  const cancel = () => {
    cancelled = true
    for (const b of bridges) b.cancelAll()
  }

  const promise = (async () => {
    const outputs = new Array<string>(requests.length)
    let nextIndex = 0
    let started = 0
    let completed = 0
    let firstError: unknown = null

    const runWorkerLoop = async (workerBridge: SimulatorBridge) => {
      while (!cancelled && firstError === null) {
        const index = nextIndex
        if (index >= requests.length) break
        nextIndex += 1
        started += 1
        onItemStart?.(index, started, requests.length, workerCount)
        try {
          const resultJson = await workerBridge.run(requests[index])
          if (cancelled || firstError !== null) break
          outputs[index] = resultJson
          completed += 1
          onItemComplete?.(index, resultJson, completed, requests.length, workerCount)
        } catch (err) {
          if (firstError === null) firstError = err
          break
        }
      }
    }

    try {
      await Promise.all(bridges.map(runWorkerLoop))
      if (cancelled) throw new Error('战斗预演已取消。')
      if (firstError !== null) throw firstError
      return outputs
    } finally {
      // 批处理结束或中途取消立即销毁辅助 Worker，释放内存
      for (const helper of helpers) helper.cancelAll()
    }
  })()

  return { concurrency: workerCount, promise, cancel }
}
