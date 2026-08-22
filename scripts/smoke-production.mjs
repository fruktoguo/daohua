/**
 * ==============================================================================
 * 脚本名称: smoke-production.mjs
 * 主要职责: 服务端与前端部署可用性端到端（E2E）闭环烟测
 *
 * 核心检查项:
 *   1. /healthz 端点: 校验服务健康状态为 ok，核对游戏数据版本 (gameDataVersion) 与数据指纹；
 *   2. /readyz 端点: 校验就绪状态为 ready，断言 PostgreSQL 快照与市场账本存储提供方声明；
 *   3. /api/actions: 校验基础静态行动目录接口返回非空行动列表；
 *   4. / 首页入口: 校验前端 HTML 页面可达且包含 root 挂载点 (`id="root"`)；
 *   5. /ws WebSocket 握手: 创建真实客户端连接，发送 hello 登录握手包，验证接收首包（snapshot / ack / delta）；
 *   6. 结构化输出: 打印测试通过的关键版本、行动数与数据指纹信息。
 *
 * 使用方式:
 *   node scripts/smoke-production.mjs [目标服务基础URL]
 *   例如: node scripts/smoke-production.mjs http://127.0.0.1:5128
 *         node scripts/smoke-production.mjs https://xx.yuohira.com
 * ==============================================================================
 */

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
if (ready.deployment?.snapshotProvider !== 'postgres'
    || ready.deployment?.marketProvider !== 'postgres'
    || ready.deployment?.scaleOutReady !== true
    || ready.deployment?.zeroDowntimeReady !== false) {
  throw new Error('当前生产存储能力声明与 PostgreSQL 目标不一致。')
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
