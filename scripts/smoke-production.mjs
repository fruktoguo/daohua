/**
 * ==============================================================================
 * 脚本名称: smoke-production.mjs
 * 主要职责: 服务端与前端部署可用性端到端（E2E）闭环烟测
 *
 * 核心检查项:
 *   1. /healthz 端点: 校验服务健康状态为 ok，核对游戏数据版本 (gameDataVersion) 与数据指纹；
 *   2. /api/actions: 校验基础静态行动目录接口返回非空行动列表；
 *   3. / 首页入口: 校验前端 HTML 页面可达且包含 root 挂载点 (`id="root"`)；
 *   4. /ws WebSocket 握手: 创建真实客户端连接，发送 hello 登录握手包，验证接收首包
 *      （snapshot / ack / delta）；未收到首包前连接被服务端关闭则立即失败并携带关闭码。
 *
 * 说明: /readyz 已从公网烟测中移除（公网侧返回 404），其能力位校验由 scripts/deploy-prod.sh
 *       的容器内回环检查承担。
 *
 * 环境变量:
 *   SMOKE_PLAYER_ID: 固定烟测账号 ID（缺省使用下方内置常量 UUID；账号幂等性由服务端
 *                    EnsurePlayerAsync 保证，不再每次运行新建账号）；
 *   SMOKE_ACCESS_TOKEN: 可选访问令牌，注入 WS 连接 query 的 token 参数（未设置时不附带）；
 *   SMOKE_TIMEOUT_MS: 单请求与 WebSocket 首包等待超时毫秒数。
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
const actions = await (await fetchChecked('/api/actions', 'application/json')).json()
const html = await (await fetchChecked('/', 'text/html')).text()
if (health.status !== 'ok') {
  throw new Error('健康状态不符合预期。')
}
if (!health.gameDataVersion || !health.gameDataFingerprint) {
  throw new Error('健康端点缺少游戏数据版本或指纹。')
}
if (!Array.isArray(actions) || actions.length === 0) {
  throw new Error('行动目录为空。')
}
if (!html.includes('id="root"')) {
  throw new Error('首页不是预期的前端入口。')
}

const wsUrl = new URL('/ws', baseUrl)
wsUrl.protocol = baseUrl.protocol === 'https:' ? 'wss:' : 'ws:'
// 身份固化：使用固定烟测账号（可用 SMOKE_PLAYER_ID 覆盖），账号幂等性由服务端
// EnsurePlayerAsync 保证，重复运行不会新建账号。
const playerId = process.env.SMOKE_PLAYER_ID || '5e0f6a2c-9b1d-4c7e-8a3f-2d4b6c8e0a11'
wsUrl.searchParams.set('playerId', playerId)
wsUrl.searchParams.set('name', '部署烟测')
if (process.env.SMOKE_ACCESS_TOKEN) {
  // 可选访问令牌：仅在设置 SMOKE_ACCESS_TOKEN 时附带，服务端校验 query 的 token 参数。
  wsUrl.searchParams.set('token', process.env.SMOKE_ACCESS_TOKEN)
}
const firstPacket = await new Promise((resolve, reject) => {
  const socket = new WebSocket(wsUrl)
  // 首包已收妥（或已因错误失败）标记：避免后续 close 事件覆盖既有结论。
  let settled = false
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
      settled = true
      clearTimeout(timeout)
      socket.close(1000, '烟测完成')
      resolve(packet)
    } catch (error) {
      settled = true
      clearTimeout(timeout)
      socket.close()
      reject(error)
    }
  })
  socket.addEventListener('error', () => {
    if (!settled) {
      settled = true
      clearTimeout(timeout)
      reject(new Error('WebSocket 连接失败。'))
    }
  })
  // 未收到首包前连接被服务端关闭（如令牌校验失败）：立即失败并携带关闭码，不再等满超时。
  socket.addEventListener('close', event => {
    if (!settled) {
      settled = true
      clearTimeout(timeout)
      reject(new Error(`WebSocket 在收到首包前被关闭，关闭码: ${event.code}`))
    }
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
  actionCount: actions.length,
  webSocketFirstPacket: firstPacket.kind,
}, null, 2))
