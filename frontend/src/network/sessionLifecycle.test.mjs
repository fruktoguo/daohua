import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { createRequire } from 'node:module'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { build } from 'esbuild'

// 真实前端 Store、解码器和 GameClient；仅替代浏览器时钟、HTTP 与 WebSocket 传输。
const modules = {
  account: ['useAccountStore'],
  gameClient: ['gameClient'],
  protocol: ['WireMessageType', 'createRequestId', 'encodeJsonEnvelope', 'decodeEnvelope'],
  sessionState: ['setSessionAccount', 'getSessionAccount', 'getSessionGeneration'],
  snapshot: ['SNAPSHOT_SCHEMA_VERSION', 'decodePlayerSnapshotDto'],
}
const stores = ['game', 'social', 'mail', 'checkin', 'avatar'].map(name => `export * from '../game/${name}Store'`)
const { outputFiles: [bundle] } = await build({
  stdin: {
    resolveDir: fileURLToPath(new URL('.', import.meta.url)),
    contents: [...Object.entries(modules).map(([file, names]) => `export { ${names.join(', ')} } from './${file}'`), ...stores].join('\n'),
  },
  bundle: true,
  platform: 'node',
  format: 'cjs',
  write: false,
  logOverride: { 'empty-import-meta': 'silent' },
  define: {
    'import.meta.env.DEV': 'true',
    'import.meta.env.VITE_APP_VERSION': '"dev"',
    'import.meta.env.VITE_WS_URL': 'undefined',
    'import.meta.env.VITE_API_URL': 'undefined',
  },
})
const compiled = { exports: {} }
new Function('require', 'module', 'exports', bundle.text)(createRequire(import.meta.url), compiled, compiled.exports)
const {
  useAccountStore, useGameStore, useSocialStore, useMailStore, useCheckinStore, useAvatarStore,
  gameClient, WireMessageType: Wire, createRequestId, encodeJsonEnvelope, decodeEnvelope,
  setSessionAccount, getSessionAccount, getSessionGeneration, SNAPSHOT_SCHEMA_VERSION, decodePlayerSnapshotDto,
} = compiled.exports

const now = '2026-11-17T15:00:00.000Z'
const account = (name = 'a') => ({ account: name, accountId: `acct.${name}`, playerId: `player.${name}`, name })
const authDto = name => ({ ...account(name), displayName: name })
const hash = text => createHash('sha256').update(text).digest('hex')
const json = (body, status = 200) => Response.json(body, { status })
function deferred() {
  let resolve
  const promise = new Promise(res => { resolve = res })
  return { promise, resolve }
}

function setup(t, fetch = async input => assert.fail(`未预期 HTTP 请求：${input}`)) {
  const previous = { window: globalThis.window, document: globalThis.document, WebSocket: globalThis.WebSocket, fetch: globalThis.fetch }
  let id = 0
  const timers = new Map()
  const sockets = []
  const data = new Map()
  const setTimeout = (fn, delay) => { timers.set(++id, { fn, delay }); return id }
  const clearTimeout = id => timers.delete(id)
  globalThis.window = {
    location: { origin: 'http://localhost', protocol: 'http:', host: 'localhost' },
    screen: { width: 500, height: 500, colorDepth: 24 },
    localStorage: { getItem: key => data.get(key) ?? null, setItem: (key, val) => data.set(key, val), removeItem: key => data.delete(key) },
    setTimeout, clearTimeout, setInterval: () => ++id, clearInterval: clearTimeout,
    addEventListener() {}, removeEventListener() {}, dispatchEvent() { return true },
  }
  globalThis.document = {
    visibilityState: 'hidden', cookie: '', createElement: () => ({ getContext: () => null }),
    addEventListener() {}, removeEventListener() {},
  }
  globalThis.fetch = fetch
  globalThis.WebSocket = class Socket extends EventTarget {
    static CONNECTING = 0
    static OPEN = 1
    static CLOSED = 3
    readyState = 0
    sent = []
    sequence = 0
    constructor(url) { super(); assert.equal(url, 'ws://localhost/ws'); sockets.push(this) }
    send(data) { this.sent.push(decodeEnvelope(new Uint8Array(data))) }
    accept() { this.readyState = 1 }
    close(code = 1006) { this.readyState = 3; this.dispatchEvent(Object.assign(new Event('close'), { code })) }
    deliver(type, body, requestId = createRequestId()) {
      const data = encodeJsonEnvelope(type, requestId, ++this.sequence, body)
      this.dispatchEvent(new MessageEvent('message', { data }))
      return gameClient.messageChain
    }
    async hello() {
      this.accept()
      await this.deliver(Wire.HelloAck, { protocolVersion: 1, serverVersion: 'dev', gameDataFingerprint: 'test', serverSequence: this.sequence + 1 })
    }
  }
  useAccountStore.setState({ currentAccount: null, sessionChecked: false, logoutState: 'idle', savedAccounts: [] })
  const reset = () => {
    gameClient.dispose()
    useGameStore.getState().resetGameStore()
    useMailStore.getState().reset()
    useCheckinStore.getState().reset()
  }
  reset()
  t.after(() => { reset(); setSessionAccount(null); Object.assign(globalThis, previous) })
  return {
    sockets,
    login(name = 'a') {
      reset()
      setSessionAccount(account(name))
      gameClient.connect()
      return sockets.at(-1)
    },
    fire(delay) {
      const entry = [...timers].find(([, value]) => value.delay === delay)
      assert.ok(entry, `缺少 ${delay} ms 定时器`)
      timers.delete(entry[0]); entry[1].fn()
      return sockets.at(-1)
    },
    pendingRetries: () => [...timers.values()].filter(({ delay }) => delay >= 500 && delay <= 8000).length,
  }
}

function section(section, data, schemaVersion = SNAPSHOT_SCHEMA_VERSION) {
  return { section, data, schemaVersion, sectionVersion: 1, updatedAt: now, checksum: hash(JSON.stringify(data)) }
}
function snapshot(playerId, stateVersion = 5, schemaVersion = SNAPSHOT_SCHEMA_VERSION) {
  const attrs = Object.fromEntries(`
    maxHitpoints maxManapoints physicalAttack spellAttack armor magicResistance accuracy evasion critical criticalResistance
    criticalDamagePoints breakPower parryResolve hpRegenPerFiveSeconds mpRegenPerFiveSeconds attackIntervalMilliseconds
    physicalDamageBonusPoints spellDamageBonusPoints metalDamageBonusPoints woodDamageBonusPoints waterDamageBonusPoints
    fireDamageBonusPoints earthDamageBonusPoints totalDamageBonusPoints basicAttackBonusPoints skillBonusPoints
    physicalDamageReductionPoints spellDamageReductionPoints metalDamageReductionPoints woodDamageReductionPoints
    waterDamageReductionPoints fireDamageReductionPoints earthDamageReductionPoints damageAbsorptionPoints
    fixedDamageBonus fixedDamageReduction fixedPhysicalDamageBonus fixedPhysicalDamageReduction fixedSpellDamageBonus fixedSpellDamageReduction attackSpeedPoints
    actionSpeedPoints cooldownReductionPoints threatBonusPoints cultivationExpBonusPoints combatExpBonusPoints
    hpDrainPoints mpDrainPoints damageReflectPoints healingBonusPoints
  `.trim().split(/\s+/).map(key => [key, 1]))
  const sections = [
    section('progression', { realmLevel: 1 }),
    section('world', { availableActionIds: [], encounterIds: [], npcs: [], buildings: [], nodeBuildings: [], monsters: [] }),
    section('combat', {
      attributes: attrs, resources: { hitpoints: 100, manapoints: 100, maxHitpoints: 100, maxManapoints: 100 },
      skills: { castRemainingMilliseconds: 0, cooldowns: {} },
    }),
    section('settings', { settings: { avatar: 'default', loopEnabled: false, autoTravelEnabled: false, queuePaused: false, autoUseSlots: [] }, presence: 1 }),
    section('loadout', {
      coreAttributes: { staminaLevel: 1, meridiansLevel: 1, attackLevel: 1, boneLevel: 1, agilityLevel: 1, consciousnessLevel: 1 },
      equipment: [], abilitySlots: [], learnedAbilityIds: [],
    }),
    section('buffs', []), section('action_queue', []), section('duel', { encounterCharges: 0, cooldownUntilUnixSeconds: 0 }),
  ]
  const headChecksum = hash([...sections].sort((a, b) => a.section.localeCompare(b.section))
    .map(row => `${row.section}:${row.sectionVersion}:${row.checksum}`).join('|'))
  return {
    playerId, stateVersion, displayName: '测试', lastSimulatedAt: now, assets: [{ assetId: 'item.lingshi', quantity: 2 }], sections,
    head: { playerId, stateVersion, schemaVersion, checksum: headChecksum },
  }
}
const delta = (version, changes = {}) => ({ playerId: account().playerId, stateVersion: version, lastSimulatedAt: now, changedSections: [], ...changes })

// C2：坏帧、期限、真实字节 checksum、资产补丁、重连预算和版本终态都通过线上信封入口。
test('坏首快照立即重连；有效全量、合法跳版本和同版本资产增量可以提交', async t => {
  const env = setup(t)
  let socket = env.login()
  await socket.hello()
  const bad = snapshot(account().playerId)
  bad.head.checksum = '0'.repeat(64)
  await socket.deliver(Wire.Snapshot, bad)
  assert.equal(socket.readyState, WebSocket.CLOSED)
  assert.equal(useGameStore.getState().hasReceivedSnapshot, false)
  socket = env.fire(500)
  await socket.hello()
  assert.notEqual(useGameStore.getState().connection, 'online')
  await socket.deliver(Wire.SnapshotDelta, delta(16))
  assert.equal(useGameStore.getState().hasReceivedSnapshot, false)
  const full = snapshot(account().playerId)
  await decodePlayerSnapshotDto(full, Buffer.from(JSON.stringify(full)))
  await socket.deliver(Wire.Snapshot, full)
  assert.equal(useGameStore.getState().connection, 'online')
  await socket.deliver(Wire.SnapshotDelta, delta(16))
  assert.equal(useGameStore.getState().version, 16)
  await socket.deliver(Wire.SnapshotDelta, delta(16, { changedAssets: [{ assetId: 'item.lingshi', quantity: 9 }] }))
  assert.equal(useGameStore.getState().inventory[0].amount, 9)
  const avatarBefore = useAvatarStore.getState().avatarId
  await socket.deliver(Wire.SnapshotDelta, delta(17, { avatarId: 'male', changedAssets: [{ assetId: 'item.lingshi', quantity: -1 }] }))
  assert.equal(socket.readyState, WebSocket.CLOSED)
  assert.equal(useGameStore.getState().inventory[0].amount, 9)
  assert.equal(useAvatarStore.getState().avatarId, avatarBefore, '坏资产帧不可留下半提交的头像')
  socket = env.fire(500)
  await socket.hello()
  await socket.deliver(Wire.Snapshot, snapshot(account().playerId, 17))
  assert.equal(useGameStore.getState().connection, 'online')
  assert.equal(useGameStore.getState().inventory[0].amount, 2)
})

test('首快照缺席且仍有好帧时限期重连；连续坏命令快照熔断', async t => {
  const env = setup(t)
  let socket = env.login()
  await socket.hello()
  await socket.deliver(Wire.LogBatch, { events: [] })
  await socket.deliver(Wire.Pong, {})
  env.fire(20_000)
  assert.equal(socket.readyState, WebSocket.CLOSED)
  for (const delay of [500, 1000, 2000]) {
    socket = env.fire(delay)
    await socket.hello()
    const requestId = createRequestId()
    await socket.deliver(Wire.CommandResult, { requestId, snapshotForm: 'delta', snapshot: { malformed: true } }, requestId)
    assert.equal(socket.readyState, WebSocket.CLOSED)
  }
  assert.equal(useGameStore.getState().disconnectReason, 'common.net.snapshotUnavailable')
  assert.equal(env.pendingRetries(), 0)
  gameClient.connect()
  assert.equal(env.sockets.length, 5, '重建失败后仍可手动重连')
})

test('不可读版本进入更新终态；更高标签但可读地板仍正常上线', async t => {
  const env = setup(t)
  let socket = env.login()
  await socket.hello()
  const next = snapshot(account().playerId, 5, SNAPSHOT_SCHEMA_VERSION + 1)
  next.head.minReaderSchemaVersion = SNAPSHOT_SCHEMA_VERSION
  await socket.deliver(Wire.Snapshot, next)
  assert.equal(useGameStore.getState().connection, 'online')
  await socket.deliver(Wire.SnapshotDelta, delta(6, { changedSections: [section('world', {}, SNAPSHOT_SCHEMA_VERSION + 1)] }))
  assert.equal(socket.readyState, WebSocket.CLOSED)
  assert.equal(useGameStore.getState().disconnectReason, 'common.net.snapshotUpdateRequired')
  gameClient.connect()
  assert.equal(env.sockets.length, 1)
  assert.equal(env.pendingRetries(), 0)
  socket = env.login('b')
  await socket.hello()
  await socket.deliver(Wire.Snapshot, snapshot(account('b').playerId))
  assert.equal(useGameStore.getState().connection, 'online')
})

// C4：触发真实 close → 退避 → Probe 路径，跨登录后分别释放 HTTP 头与错误正文。
for (const outcome of ['401', '200', '503-body']) {
  test(`旧连接 Probe 的 ${outcome} 不改变新会话状态或 socket 数`, async t => {
    const fetchDone = deferred()
    const bodyRead = deferred()
    const bodyDone = deferred()
    const env = setup(t, input => {
      assert.equal(new URL(input).pathname, '/api/v1/auth/session')
      return fetchDone.promise
    })
    let socket = env.login()
    socket.close()
    env.fire(500).close()
    env.fire(1000).close()
    if (outcome === '503-body') {
      fetchDone.resolve({ status: 503, json: () => { bodyRead.resolve(); return bodyDone.promise } })
      await bodyRead.promise
    }
    // 200 分支明确覆盖同一角色重登，没有可用的 playerId 差异可做保护。
    const name = outcome === '200' ? 'a' : 'b'
    socket = env.login(name)
    await socket.hello()
    await socket.deliver(Wire.Snapshot, snapshot(account(name).playerId))
    const prevNotice = useGameStore.getState().notice
    const count = env.sockets.length
    if (outcome === '503-body') bodyDone.resolve({ code: 'SERVER_UPDATING' })
    else fetchDone.resolve(new Response(null, { status: Number(outcome) }))
    await new Promise(resolve => setImmediate(resolve))
    assert.equal(useGameStore.getState().connection, 'online')
    assert.equal(useGameStore.getState().notice, prevNotice)
    assert.equal(useGameStore.getState().disconnectReason, null)
    assert.equal(env.sockets.length, count)
    assert.equal(env.pendingRetries(), 0)
  })
}

// C5：生产协议帧配对而不是直接调用 Store 的失败方法。
test('在线名片与队友战报按 requestId 处理 Error、超时、迟到帧与重试', async t => {
  const env = setup(t)
  const socket = env.login()
  await socket.hello()
  await socket.deliver(Wire.Snapshot, snapshot(account().playerId))
  const target = 'player.teammate'
  const social = useSocialStore.getState
  social().requestPlayerCard(target)
  const modalId = socket.sent.at(-1).requestId
  social().requestTeammateReport(target)
  const reportId = socket.sent.at(-1).requestId
  const error = { requestId: modalId, code: 'MESSAGE_FAILED', message: '暂不可用' }
  await socket.deliver(Wire.Error, error, reportId)
  assert.equal(social().playerCardRequestId, modalId)
  await socket.deliver(Wire.Error, error, modalId)
  assert.equal(social().playerCardRequestId, null)
  assert.equal(social().playerCardError?.code, 'MESSAGE_FAILED')
  assert.equal(social().teammateReports[target].loading, true)
  env.fire(15_000)
  assert.equal(social().teammateReports[target].loading, false)
  assert.equal(social().teammateReports[target].error, 'timeout')
  social().requestTeammateReport(target)
  const erroredReport = socket.sent.at(-1).requestId
  await socket.deliver(Wire.Error, { ...error, requestId: erroredReport }, erroredReport)
  assert.equal(social().teammateReports[target].error, 'MESSAGE_FAILED')
  social().requestTeammateReport(target)
  const freshReport = socket.sent.at(-1).requestId
  await socket.deliver(Wire.PlayerCard, { requestId: reportId, playerId: target }, reportId)
  assert.equal(social().teammateReports[target].loading, true)
  await socket.deliver(Wire.PlayerCard, { requestId: freshReport, playerId: target }, freshReport)
  assert.equal(social().teammateReports[target].error, null)
  social().requestPlayerCard(target)
  env.fire(15_000)
  assert.equal(social().playerCardError?.code, 'timeout')
  social().requestPlayerCard(target)
  const freshCard = socket.sent.at(-1).requestId
  await socket.deliver(Wire.PlayerCard, { requestId: freshCard, playerId: target }, freshCard)
  assert.equal(social().playerCard?.requestId, freshCard)
  assert.equal(useGameStore.getState().connection, 'online')
})

// C1：同角色重登，成功正文与 HTTP 错误晚于新会话，不能污染红点、回执、提示或请求锁。
test('同角色重登丢弃旧邮件与签到正文，并保留新签到刷新在途归并', async t => {
  const pending = new Map()
  const env = setup(t, input => {
    const path = new URL(input).pathname
    const request = deferred()
    const list = pending.get(path) ?? []
    list.push(request)
    pending.set(path, list)
    return request.promise
  })
  env.login()
  const mail = useMailStore.getState
  const checkin = useCheckinStore.getState
  const oldMail = mail().claimMail('mail.1')
  const oldClaim = checkin().claim()
  const oldRefresh = checkin().refreshStatus()
  const mailBody = deferred()
  pending.get('/api/v1/mails/mail.1/claim')[0].resolve({ ok: true, status: 200, json: () => mailBody.promise })
  await Promise.resolve()
  env.login()
  const newMail = mail().claimMail('mail.1')
  const newRefresh = checkin().refreshStatus()
  mailBody.resolve({ summary: { unreadCount: 99 }, claimedMailKeys: ['mail.1'], grantedAttachments: [] })
  pending.get('/api/v1/checkin/claim')[0].resolve(json({ message: '旧会话错误' }, 503))
  pending.get('/api/v1/checkin')[0].resolve(json({ canClaimToday: false, dayIndex: 1 }))
  await Promise.all([oldMail, oldClaim, oldRefresh])
  assert.equal(mail().actionBusyKey, 'claim:mail.1')
  assert.equal(mail().summary.unreadCount, 0)
  assert.equal(checkin().error, null)
  assert.equal(checkin().status, null)
  assert.equal(useGameStore.getState().notice, null)
  const dedupedRefresh = checkin().refreshStatus()
  assert.equal(pending.get('/api/v1/checkin').length, 2)
  pending.get('/api/v1/checkin')[1].resolve(json({ canClaimToday: true, dayIndex: 2 }))
  pending.get('/api/v1/mails/mail.1/claim')[1].resolve(json({ summary: { unreadCount: 1 }, claimedMailKeys: [], grantedAttachments: [] }))
  await Promise.all([newMail, newRefresh, dedupedRefresh])
  assert.equal(checkin().status.dayIndex, 2)
  assert.equal(mail().summary.unreadCount, 1)
})

// C3：验证 Store 真实 HTTP 入口及退出失败后的恢复；浏览器 Cookie 应用时序另做回环验证。
test('退出完成前不发出下一次登录；真实 HTTP 失败进入可恢复状态', async t => {
  const releaseLogout = deferred()
  const logoutStarted = deferred()
  const calls = []
  let failLogout = true
  setup(t, async (input, init) => {
    const path = new URL(input).pathname
    calls.push(path)
    if (path === '/api/v1/auth/session') return json(authDto('a'))
    if (path === '/api/v1/auth/logout') {
      if (failLogout) return json({ message: '暂不可用' }, 503)
      logoutStarted.resolve()
      return releaseLogout.promise
    }
    if (path === '/api/v1/auth/login') return json(authDto(JSON.parse(init.body).account))
    if (path === '/api/v1/chat/stickers/sync') return json({ stickers: [], tombstones: [] })
    assert.fail(`未预期请求 ${path}`)
  })
  const store = useAccountStore.getState
  assert.equal(await store().restoreSession(), true)
  const originalGeneration = getSessionGeneration()
  store().updateDisplayName('新道号')
  assert.equal(getSessionGeneration(), originalGeneration)
  assert.equal(await store().logout(), false)
  assert.equal(store().logoutState, 'failed')
  assert.equal(getSessionAccount(), null)
  failLogout = false
  const logout = store().logout()
  const login = store().loginWithAuth('b', 'test-password')
  await logoutStarted.promise
  assert.equal(store().logoutState, 'pending')
  assert.ok(!calls.includes('/api/v1/auth/login'))
  releaseLogout.resolve(new Response(null, { status: 204 }))
  assert.equal(await logout, true)
  assert.equal((await login).success, true)
  assert.equal(store().logoutState, 'idle')
  assert.equal(getSessionAccount()?.playerId, account('b').playerId)
})
