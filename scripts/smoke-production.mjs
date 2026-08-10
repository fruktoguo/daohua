import process from 'node:process'

const baseUrl = new URL(process.argv[2] || 'http://127.0.0.1:5128')
const timeoutMs = Number(process.env.SMOKE_TIMEOUT_MS || 15_000)

async function fetchChecked(path, expectedContentType) {
  const response = await fetch(new URL(path, baseUrl), {
    signal: AbortSignal.timeout(timeoutMs),
    headers: { Accept: expectedContentType },
  })
  if (!response.ok) {
    throw new Error(`${path} 返回 HTTP ${response.status}`)
  }
  const contentType = response.headers.get('content-type') || ''
  if (!contentType.includes(expectedContentType)) {
    throw new Error(`${path} Content-Type 异常: ${contentType}`)
  }
  return response
}

const health = await (await fetchChecked('/healthz', 'application/json')).json()
const ready = await (await fetchChecked('/readyz', 'application/json')).json()
const actions = await (await fetchChecked('/api/actions', 'application/json')).json()
const html = await (await fetchChecked('/', 'text/html')).text()

if (health.status !== 'ok' || ready.status !== 'ready') {
  throw new Error('健康状态不符合预期。')
}
if (!health.gameDataVersion || !health.gameDataFingerprint) {
  throw new Error('健康端点缺少游戏数据版本或指纹。')
}
if (ready.deployment?.snapshotProvider !== 'local-json' || ready.deployment?.singleWriter !== true) {
  throw new Error('当前生产存储能力声明与预期不一致。')
}
if (!Array.isArray(actions) || actions.length === 0) {
  throw new Error('行动目录为空。')
}
if (!html.includes('id="root"')) {
  throw new Error('首页不是预期的前端入口。')
}

const wsUrl = new URL('/ws', baseUrl)
wsUrl.protocol = baseUrl.protocol === 'https:' ? 'wss:' : 'ws:'
const playerId = crypto.randomUUID()
wsUrl.searchParams.set('playerId', playerId)
wsUrl.searchParams.set('name', '部署烟测')

const firstPacket = await new Promise((resolve, reject) => {
  const socket = new WebSocket(wsUrl)
  const timeout = setTimeout(() => {
    socket.close()
    reject(new Error('WebSocket 烟测超时。'))
  }, timeoutMs)

  socket.addEventListener('open', () => {
    socket.send(JSON.stringify({
      kind: 'hello',
      requestId: `smoke-${playerId}`,
      sequence: 0,
      payload: { name: '部署烟测' },
    }))
  })
  socket.addEventListener('message', async event => {
    try {
      const value = typeof event.data === 'string'
        ? event.data
        : event.data instanceof Blob
          ? await event.data.text()
          : new TextDecoder().decode(event.data)
      const packet = JSON.parse(value)
      clearTimeout(timeout)
      socket.close(1000, '烟测完成')
      resolve(packet)
    } catch (error) {
      clearTimeout(timeout)
      socket.close()
      reject(error)
    }
  })
  socket.addEventListener('error', () => {
    clearTimeout(timeout)
    reject(new Error('WebSocket 连接失败。'))
  })
})

if (!['snapshot', 'ack', 'delta'].includes(firstPacket.kind)) {
  throw new Error(`WebSocket 首包类型异常: ${String(firstPacket.kind)}`)
}

console.log(JSON.stringify({
  baseUrl: baseUrl.origin,
  version: health.version,
  gameDataVersion: health.gameDataVersion,
  gameDataFingerprint: health.gameDataFingerprint,
  loadedPlayerCount: ready.loadedPlayerCount,
  actionCount: actions.length,
  webSocketFirstPacket: firstPacket.kind,
}, null, 2))
