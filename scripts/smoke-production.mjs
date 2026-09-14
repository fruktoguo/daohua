/**
 * daohua 新基线跨边界烟测。
 * 使用 Node 内置 net/tls 实现 WebSocket 握手与帧读写，不引入 ws 运行时依赖，
 * 可以正确携带 Cookie/Authorization，并且只使用固定烟测账号避免每次运行创建新账号。
 */
import crypto from 'node:crypto'
import http from 'node:http'
import https from 'node:https'
import net from 'node:net'
import tls from 'node:tls'
import process from 'node:process'

const baseUrl = new URL(process.argv[2] || 'http://127.0.0.1:5128')
const timeoutMs = Number(process.env.SMOKE_TIMEOUT_MS || 15_000)
const requireStatic = process.env.SMOKE_REQUIRE_STATIC !== '0'
const runWebSocket = process.env.SMOKE_RUN_WEBSOCKET !== '0'

async function main() {
  const health = await fetchJson('/healthz')
  if (health.status !== 'ok') throw new Error('健康状态不符合预期。')
  if (typeof health.version !== 'string' || health.version.length === 0) throw new Error('健康端点缺少版本信息。')
  const ready = await fetchJson('/readyz')
  if (!['ok', 'ready'].includes(ready.status)) throw new Error('就绪状态不符合预期。')
  if (requireStatic) {
    const html = await fetchText('/')
    if (!html.includes('id="root"')) throw new Error('首页不是预期的前端入口。')
  }
  const meta = await fetchJson('/api/v1/meta')

  let firstPacket = null
  let authMode = null
  let warning = null
  if (runWebSocket) {
    const auth = await getWebSocketAuthHeaders()
    authMode = auth.mode
    firstPacket = await smokeWebSocket(meta.gameDataFingerprint || '', auth.headers)
  } else {
    warning = 'SMOKE_RUN_WEBSOCKET=0，已跳过 WebSocket。'
  }

  console.log(JSON.stringify({
    baseUrl: baseUrl.origin,
    version: health.version || health.contractVersion || meta.version || null,
    gameDataFingerprint: meta.gameDataFingerprint || health.gameDataFingerprint || null,
    readyStatus: ready.status,
    authMode,
    webSocketFirstPacket: firstPacket?.type || null,
    webSocketProtocolVersion: firstPacket?.protocolVersion || null,
    warning,
  }, null, 2))
}

async function fetchJson(path) {
  const response = await requestHttp(path, 'application/json')
  return JSON.parse(response.body.toString('utf8'))
}

async function fetchText(path) {
  const response = await requestHttp(path, 'text/html')
  return response.body.toString('utf8')
}

function requestHttp(path, accept, method = 'GET', body = undefined) {
  return new Promise((resolve, reject) => {
    const target = new URL(path, baseUrl)
    const transport = target.protocol === 'https:' ? https : http
    const headers = { Accept: accept }
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    const request = transport.request(target, {
      method,
      headers,
      timeout: timeoutMs,
    }, response => {
      const chunks = []
      response.on('data', chunk => chunks.push(Buffer.from(chunk)))
      response.on('end', () => {
        const result = { statusCode: response.statusCode || 0, headers: response.headers, body: Buffer.concat(chunks) }
        if (result.statusCode < 200 || result.statusCode >= 300) {
          reject(new Error(`${path} 返回 HTTP ${result.statusCode}: ${result.body.toString('utf8').slice(0, 240)}`))
        } else {
          resolve(result)
        }
      })
    })
    request.once('timeout', () => request.destroy(new Error(`${path} 请求超时`)))
    request.once('error', reject)
    if (body !== undefined) request.write(JSON.stringify(body))
    request.end()
  })
}

async function getWebSocketAuthHeaders() {
  const explicitCookie = process.env.SMOKE_COOKIE || ''
  if (explicitCookie) return { mode: 'cookie', headers: { Cookie: explicitCookie } }
  const sessionToken = process.env.SMOKE_SESSION_TOKEN || ''
  if (sessionToken) return { mode: 'bearer-session', headers: { Authorization: `Bearer ${sessionToken}` } }

  const account = process.env.SMOKE_ACCOUNT || 'smoke_probe'
  const password = process.env.SMOKE_PASSWORD || 'Daohua-Smoke-Only-Change-Me-2026!'
  if (!/^[a-zA-Z0-9_]{3,32}$/.test(account) || password.length < 6 || password.length > 24) {
    throw new Error('SMOKE_ACCOUNT/SMOKE_PASSWORD 不符合账号鉴权要求。')
  }

  let loginError = null
  try {
    const login = await requestHttp('/api/v1/auth/login', 'application/json', 'POST', { account, password })
    return { mode: 'login', headers: { Cookie: cookieHeader(login.headers) } }
  } catch (error) {
    loginError = error
  }

  if (process.env.SMOKE_REGISTER === '0') {
    throw new Error(`烟测账号登录失败，且 SMOKE_REGISTER=0: ${loginError?.message || 'unknown'}`)
  }
  try {
    const registration = await requestHttp('/api/v1/auth/register', 'application/json', 'POST', {
      account,
      password,
      displayName: process.env.SMOKE_DISPLAY_NAME || `部署烟测-${account}`,
    })
    return { mode: 'register', headers: { Cookie: cookieHeader(registration.headers) } }
  } catch (registrationError) {
    // 账号可能已经存在但密码正确：再次登录获取当前 session。
    try {
      const login = await requestHttp('/api/v1/auth/login', 'application/json', 'POST', { account, password })
      return { mode: 'login-after-register-conflict', headers: { Cookie: cookieHeader(login.headers) } }
    } catch (loginError) {
      throw new Error(`烟测账号注册/登录均失败：注册=${registrationError.message}；登录=${loginError.message}`)
    }
  }
}

function cookieHeader(headers) {
  const setCookie = headers['set-cookie']
  if (!setCookie || setCookie.length === 0) throw new Error('鉴权响应缺少会话 Cookie。')
  return setCookie[0].split(';', 1)[0]
}

async function smokeWebSocket(gameDataFingerprint, authHeaders) {
  const target = new URL('/ws', baseUrl)
  const socket = await openWebSocket(target, authHeaders)
  try {
    const requestId = crypto.randomUUID()
    socket.write(encodeClientFrame(encodeEnvelope(1, {
      clientVersion: 'smoke',
      gameDataFingerprint,
    })))

    let helloAck = null
    const deadline = Date.now() + timeoutMs
    while (Date.now() < deadline) {
      const frame = await socket.nextFrame(deadline - Date.now())
      if (frame.opcode === 0x8) throw new Error(`WebSocket 在首帧前关闭，关闭码: ${frame.payload.readUInt16BE(0) || 1000}`)
      if (frame.opcode === 0x9) {
        socket.write(encodeClientFrame(frame.payload, 0xA))
        continue
      }
      if (frame.opcode !== 0x2) continue
      const packet = decodeEnvelope(frame.payload)
      if (packet.protocolVersion !== 1) throw new Error('WebSocket 协议版本异常。')
      if (packet.type === 2) {
        helloAck = packet
        continue
      }
      if (packet.type === 3) {
        if (!helloAck || packet.sequence <= helloAck.sequence) throw new Error('WebSocket hello/snapshot 序列不符合 wire.v1。')
        return packet
      }
      if (packet.type === 10) throw new Error('WebSocket 返回错误帧。')
    }
    throw new Error('WebSocket 烟测超时。')
  } finally {
    socket.close()
  }
}

function encodeEnvelope(messageType, payload) {
  const body = Buffer.from(JSON.stringify(payload), 'utf8')
  const output = Buffer.allocUnsafe(36 + body.length)
  output.writeUInt32BE(0x44485731, 0)
  output.writeUInt16BE(1, 4)
  output.writeUInt8(messageType, 6)
  output.writeUInt8(1, 7) // WireCodec.Json
  crypto.randomBytes(16).copy(output, 8)
  output.writeBigInt64BE(1n, 24)
  output.writeUInt32BE(body.length, 32)
  body.copy(output, 36)
  return output
}

function decodeEnvelope(buffer) {
  if (buffer.length < 36 || buffer.readUInt32BE(0) !== 0x44485731) throw new Error('WebSocket 返回非 DHW1 二进制帧。')
  const payloadLength = buffer.readUInt32BE(32)
  if (buffer.readUInt16BE(4) !== 1 || buffer.readUInt8(7) !== 1 || payloadLength !== buffer.length - 36 || payloadLength > 1_048_576) {
    throw new Error('WebSocket DHW1 帧头校验失败。')
  }
  return {
    protocolVersion: buffer.readUInt16BE(4),
    type: buffer.readUInt8(6),
    sequence: Number(buffer.readBigInt64BE(24)),
  }
}

function encodeClientFrame(payload, opcode = 0x2) {
  if (payload.length > 1_048_576) throw new Error('烟测帧超过 1 MiB。')
  const mask = crypto.randomBytes(4)
  let header
  if (payload.length < 126) {
    header = Buffer.alloc(2)
    header[1] = 0x80 | payload.length
  } else if (payload.length <= 0xffff) {
    header = Buffer.alloc(4)
    header[1] = 0x80 | 126
    header.writeUInt16BE(payload.length, 2)
  } else {
    header = Buffer.alloc(10)
    header[1] = 0x80 | 127
    header.writeBigUInt64BE(BigInt(payload.length), 2)
  }
  header[0] = 0x80 | opcode
  const masked = Buffer.allocUnsafe(payload.length)
  for (let index = 0; index < payload.length; index++) masked[index] = payload[index] ^ mask[index % 4]
  return Buffer.concat([header, mask, masked])
}

function openWebSocket(target, headers) {
  return new Promise((resolve, reject) => {
    const key = crypto.randomBytes(16).toString('base64')
    const transport = target.protocol === 'wss:' ? tls : net
    const options = {
      host: target.hostname,
      port: Number(target.port || (target.protocol === 'wss:' ? 443 : 80)),
      ...(target.protocol === 'wss:' ? { servername: target.hostname, rejectUnauthorized: process.env.SMOKE_TLS_VERIFY !== '0' } : {}),
    }
    const raw = transport.connect(options)
    let buffer = Buffer.alloc(0)
    let settled = false
    const timeout = setTimeout(() => raw.destroy(new Error('WebSocket 握手超时。')), timeoutMs)
    const fail = error => {
      if (settled) return
      settled = true
      clearTimeout(timeout)
      raw.destroy()
      reject(error)
    }
    raw.once('error', fail)
    const connectedEvent = target.protocol === 'wss:' ? 'secureConnect' : 'connect'
    raw.once(connectedEvent, () => {
      const path = `${target.pathname || '/'}${target.search || ''}`
      const host = target.port ? `${target.hostname}:${target.port}` : target.hostname
      const lines = [
        `GET ${path} HTTP/1.1`,
        `Host: ${host}`,
        'Upgrade: websocket',
        'Connection: Upgrade',
        `Sec-WebSocket-Key: ${key}`,
        'Sec-WebSocket-Version: 13',
      ]
      for (const [name, value] of Object.entries(headers || {})) lines.push(`${name}: ${value}`)
      raw.write(`${lines.join('\r\n')}\r\n\r\n`)
    })
    const onData = chunk => {
      buffer = Buffer.concat([buffer, chunk])
      const boundary = buffer.indexOf('\r\n\r\n')
      if (boundary < 0) return
      clearTimeout(timeout)
      const responseHead = buffer.subarray(0, boundary).toString('latin1')
      buffer = buffer.subarray(boundary + 4)
      const lines = responseHead.split('\r\n')
      if (!/^HTTP\/1\.1 101(?: |$)/.test(lines[0])) return fail(new Error(`WebSocket 握手失败: ${lines[0]}`))
      const responseHeaders = new Map()
      for (const line of lines.slice(1)) {
        const separator = line.indexOf(':')
        if (separator > 0) responseHeaders.set(line.slice(0, separator).toLowerCase(), line.slice(separator + 1).trim())
      }
      const expectedAccept = crypto.createHash('sha1').update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64')
      if (responseHeaders.get('sec-websocket-accept') !== expectedAccept) return fail(new Error('WebSocket Sec-WebSocket-Accept 校验失败。'))
      settled = true
      clearTimeout(timeout)
      raw.off('data', onData)
      resolve(new RawWebSocket(raw, buffer))
    }
    raw.on('data', onData)
  })
}

class RawWebSocket {
  #socket
  #buffer
  #waiters = []
  #closed = false

  constructor(socket, initialBuffer) {
    this.#socket = socket
    this.#buffer = initialBuffer
    socket.on('data', chunk => {
      this.#buffer = Buffer.concat([this.#buffer, chunk])
      this.#drain()
    })
    socket.on('close', () => {
      this.#closed = true
      this.#drain()
    })
    socket.on('error', error => {
      this.#closed = true
      for (const waiter of this.#waiters.splice(0)) waiter.reject(error)
    })
  }

  write(frame) { this.#socket.write(frame) }

  nextFrame(timeout) {
    const frame = this.#tryReadFrame()
    if (frame) return Promise.resolve(frame)
    if (this.#closed) return Promise.reject(new Error('WebSocket 已关闭。'))
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        const index = this.#waiters.findIndex(waiter => waiter.resolve === resolve)
        if (index >= 0) this.#waiters.splice(index, 1)
        reject(new Error('WebSocket 帧读取超时。'))
      }, Math.max(1, timeout))
      this.#waiters.push({ resolve: value => { clearTimeout(timer); resolve(value) }, reject: error => { clearTimeout(timer); reject(error) } })
    })
  }

  close() {
    if (this.#closed) return
    try { this.#socket.write(encodeClientFrame(Buffer.from([0x03, 0xE8]), 0x8)) } catch {}
    this.#socket.end()
    this.#closed = true
  }

  #drain() {
    while (this.#waiters.length > 0) {
      const frame = this.#tryReadFrame()
      if (!frame) break
      this.#waiters.shift().resolve(frame)
    }
    if (this.#closed) {
      for (const waiter of this.#waiters.splice(0)) waiter.reject(new Error('WebSocket 已关闭。'))
    }
  }

  #tryReadFrame() {
    if (this.#buffer.length < 2) return null
    const first = this.#buffer[0]
    const second = this.#buffer[1]
    const fin = (first & 0x80) !== 0
    const opcode = first & 0x0f
    const masked = (second & 0x80) !== 0
    let length = second & 0x7f
    let offset = 2
    if (length === 126) {
      if (this.#buffer.length < 4) return null
      length = this.#buffer.readUInt16BE(2)
      offset = 4
    } else if (length === 127) {
      if (this.#buffer.length < 10) return null
      const value = this.#buffer.readBigUInt64BE(2)
      if (value > 1_048_576n) throw new Error('WebSocket 帧超过 1 MiB。')
      length = Number(value)
      offset = 10
    }
    if (length > 1_048_576) throw new Error('WebSocket 帧超过 1 MiB。')
    const maskOffset = masked ? 4 : 0
    if (this.#buffer.length < offset + maskOffset + length) return null
    const mask = masked ? this.#buffer.subarray(offset, offset + 4) : null
    const payloadStart = offset + maskOffset
    const payload = Buffer.from(this.#buffer.subarray(payloadStart, payloadStart + length))
    this.#buffer = this.#buffer.subarray(payloadStart + length)
    if (mask) for (let index = 0; index < payload.length; index++) payload[index] ^= mask[index % 4]
    if (!fin && opcode !== 0x0) throw new Error('烟测不支持未完成控制帧。')
    return { fin, opcode, payload }
  }
}

await main()
