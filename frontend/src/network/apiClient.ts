import { registerBaseAttributeAnchors, type BaseAttributeAnchor } from '../game/combatConstants'
import type { ActionDefinition, BuildingDefinition, CombatAbilityDefinition, CombatEncounterDefinition, CombatEquipmentDefinition, DungeonDefinition, ItemDefinition, MapNodeBuilding, MarketBook, MarketItemStats, MarketOrder, MarketVariantSummary, PlayerDuelRecordsResponseDto, ShopState, TriggerBuffOption, TriggerSpec, WorldMapDefinition } from '../game/types'
import type { SimulatorEnvironmentDto, SimulatorProfileDto } from '../game/simulator/simulatorTypes'
import {
  createRequestId,
  type CheckinClaimResponseDto,
  type CheckinStatusDto,
  type GuideListResponseDto,
  type GuidePostDetailDto,
  type GuidePostUpsertPayload,
  type LeaderboardIndexDto,
  type LeaderboardSnapshotDto,
  type MailClaimResponseDto,
  type MailDeleteResponseDto,
  type MailDetailResponseDto,
  type MailListResponseDto,
  type MailReadResponseDto,
  type RedeemCodeResponseDto,
  type SetGuideLikeResponseDto,
  type TipGuidePostResponseDto,
  type UnlockPlayerLoadoutResponseDto,
} from './protocol'
import { getApiUrl, getHttpBase } from './apiUrl'

export { getApiUrl, getHttpBase }

/** API 请求的最大等待时间，避免网络半断开时页面永久停留在加载态。 */
const REQUEST_TIMEOUT_MS = 15_000

/**
 * 最近一批通过一致性校验的配表指纹，与 HTTP 表示的 ETag 独立。
 * WebSocket Hello 握手回传该值，服务端不一致时以 GAME_DATA_MISMATCH 拒绝并触发配表重载。
 */
let catalogFingerprint: string | null = null

/** 当前客户端持有的游戏数据指纹；未拉取过配表时为 null。 */
export function getCatalogFingerprint(): string | null {
  return catalogFingerprint
}

/**
 * 拉取游戏静态配表全集（行动/地图/遭遇/物品/装备/基础属性锚点/功法/可建造建筑/Buff 词表/触发规约）。
 * 副作用：baseAttributes 拉取后注册进 combatConstants 锚点表，境界/属性推算依赖其结果。
 * 任一端点失败，或整批重拉后指纹仍缺失或不一致，均不发布新指纹。
 */
export async function fetchGameCatalog(signal?: AbortSignal): Promise<{
  actions: ActionDefinition[]
  maps: WorldMapDefinition[]
  encounters: CombatEncounterDefinition[]
  dungeons: DungeonDefinition[]
  items: ItemDefinition[]
  equipment: CombatEquipmentDefinition[]
  abilities: CombatAbilityDefinition[]
  buildings: BuildingDefinition[]
  buffs: TriggerBuffOption[]
  triggerSpec: TriggerSpec
}> {
  const load = (cache: RequestCache) => {
    // catalog 为公开静态数据：不发 Cookie；首轮允许浏览器条件验证，冲突时整批 reload。
    const init: RequestInit = { signal, cache, credentials: 'omit' }
    return Promise.all([
      requestJsonDetailed<ActionDefinition[]>('/api/v1/catalog/actions', init),
      requestJsonDetailed<WorldMapDefinition[]>('/api/v1/catalog/maps', init),
      requestJsonDetailed<CombatEncounterDefinition[]>('/api/v1/catalog/encounters', init),
      requestJsonDetailed<DungeonDefinition[]>('/api/v1/catalog/dungeons', init),
      requestJsonDetailed<ItemDefinition[]>('/api/v1/catalog/items', init),
      requestJsonDetailed<CombatEquipmentDefinition[]>('/api/v1/catalog/equipment', init),
      requestJsonDetailed<BaseAttributeAnchor[]>('/api/v1/catalog/base-attributes', init),
      requestJsonDetailed<CombatAbilityDefinition[]>('/api/v1/catalog/abilities', init),
      requestJsonDetailed<BuildingDefinition[]>('/api/v1/catalog/buildings', init),
      // Buff 词表与触发条件规约常量：服务端单一权威，前端条件编辑器不得另写一份。
      requestJsonDetailed<TriggerBuffOption[]>('/api/v1/catalog/buffs', init),
      requestJsonDetailed<TriggerSpec>('/api/v1/catalog/triggers', init),
    ])
  }
  const getAlignedFingerprint = (batch: Awaited<ReturnType<typeof load>>) => {
    const fingerprint = batch[0].fingerprint
    return fingerprint && batch.every(entry => entry.fingerprint === fingerprint) ? fingerprint : null
  }
  let batch = await load('no-cache')
  let fingerprint = getAlignedFingerprint(batch)
  // 并发请求可能跨越发版窗口；缺头（含未正确暴露的跨域响应）或混版都不能通过握手。
  if (!fingerprint) {
    batch = await load('reload')
    fingerprint = getAlignedFingerprint(batch)
    if (!fingerprint) throw new Error('游戏配表版本不一致，请稍后重试。')
  }
  signal?.throwIfAborted()
  const [actions, maps, encounters, dungeons, items, equipment, baseAttributes, abilities, buildings, buffs, triggerSpec]
    = batch.map(entry => entry.data) as [
      ActionDefinition[], WorldMapDefinition[], CombatEncounterDefinition[], DungeonDefinition[], ItemDefinition[],
      CombatEquipmentDefinition[], BaseAttributeAnchor[], CombatAbilityDefinition[],
      BuildingDefinition[], TriggerBuffOption[], TriggerSpec,
    ]
  registerBaseAttributeAnchors(baseAttributes)
  catalogFingerprint = fingerprint
  return { actions, maps, encounters, dungeons, items, equipment, abilities, buildings, buffs, triggerSpec }
}

/**
 * 拉取全图节点公共建筑索引（nodeId → 建筑摘要），供大地图地点详情合并展示玩家建筑。
 * 动态共享数据、非配表目录，故不走 catalog 指纹缓存通道。
 */
export async function fetchNodeBuildingIndex(signal?: AbortSignal): Promise<Record<string, MapNodeBuilding[]>> {
  return requestJson<Record<string, MapNodeBuilding[]>>('/api/v1/world/node-buildings', { signal })
}

/** 按需拉取当前行动内的 PVP 斗法紧凑记录（WS 快照已剥离明细以节省流量）。 */
export async function fetchPlayerDuelRecords(
  playerId: string,
  signal?: AbortSignal,
): Promise<PlayerDuelRecordsResponseDto> {
  return requestJson<PlayerDuelRecordsResponseDto>(`/api/v1/players/${encodeURIComponent(playerId)}/duel-records`, { signal })
}

function parseNumericVariant(value: string | null, fallback: number): number {
  if (value === null) return fallback
  const parsed = Number(value)
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback
}

/** 服务端错误响应体的宽松形态（RFC 7807 ProblemDetails 及自定义 code/message 变体） */
export interface ProblemDetailsLike {
  title?: string
  detail?: string
  code?: string
  message?: string
}

/** 保留接口错误码供调用方本地化；原有可读文案仍通过 message 提供。 */
export class ApiResponseError extends Error {
  readonly code: string | undefined

  constructor(message: string, code: string | undefined) {
    super(message)
    this.code = code
  }
}

async function parseError(response: Response): Promise<ApiResponseError> {
  let detail = `HTTP ${response.status}`
  let code: string | undefined
  try {
    const body = await response.json() as ProblemDetailsLike
    detail = body.detail || body.message || body.title || body.code || detail
    code = typeof body.code === 'string' ? body.code : typeof body.title === 'string' ? body.title : undefined
  } catch {
    // 非 JSON 错误只保留状态码或明确网关提示，避免把服务端 HTML 泄漏到 UI。
    if (response.status === 503) detail = '服务器正在维护或恢复中，请稍候重试。'
    else if (response.status === 502 || response.status === 504) detail = '服务器当前响应异常或正在重启恢复，请稍候重试。'
  }
  return new ApiResponseError(detail, code)
}

/**
 * 同源 JSON API 请求基座。
 * 默认携带 HttpOnly Cookie（credentials: include）、强制 JSON 头、15 秒超时中止，
 * 并转发调用方 AbortSignal；非 2xx 解析错误体抛出可读 Error，204 返回 undefined。
 */
export async function requestJson<T>(path: string, init: RequestInit = {}): Promise<T> {
  return (await requestJsonDetailed<T>(path, init)).data
}

/**
 * requestJson 的明细变体：返回配表指纹响应头供批次校验，不用 HTTP 缓存验证器作为指纹。
 */
async function requestJsonDetailed<T>(path: string, init: RequestInit = {}): Promise<{ data: T; fingerprint: string | null }> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body !== undefined && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  const controller = new AbortController()
  let timedOut = false
  const timeoutId = window.setTimeout(() => {
    timedOut = true
    controller.abort()
  }, REQUEST_TIMEOUT_MS)
  const forwardAbort = () => controller.abort()
  if (init.signal) {
    if (init.signal.aborted) controller.abort()
    else init.signal.addEventListener('abort', forwardAbort, { once: true })
  }
  try {
    const response = await fetch(getApiUrl(path), {
      ...init,
      signal: controller.signal,
      headers,
      credentials: init.credentials ?? 'include',
      cache: init.cache ?? (init.method && init.method !== 'GET' ? 'no-store' : 'no-cache'),
    })
    if (!response.ok) throw await parseError(response)
    const fingerprint = path.startsWith('/api/v1/catalog/') ? response.headers.get('X-Game-Data-Fingerprint') : null
    if (response.status === 204) return { data: undefined as T, fingerprint }
    return { data: await response.json() as T, fingerprint }
  } catch (error) {
    if (timedOut) throw new Error('网络请求超时，请稍后重试。')
    throw error
  } finally {
    window.clearTimeout(timeoutId)
    init.signal?.removeEventListener('abort', forwardAbort)
  }
}

/**
 * HTTP 玩家命令通道（WebSocket 之外的兜底/管理面路径）。
 * requestId 同时放入信封顶层与 payload 内供服务端幂等关联；
 * expectedStateVersion 为乐观并发校验（0 表示不校验）。
 */
export function postPlayerCommand<TPayload extends object, TResult = unknown>(
  playerId: string,
  command: string,
  payload: TPayload,
  expectedStateVersion = 0,
  requestId = createRequestId(),
): Promise<TResult> {
  return requestJson<TResult>(`/api/v1/players/${encodeURIComponent(playerId)}/commands`, {
    method: 'POST',
    body: JSON.stringify({
      requestId,
      command,
      payload: { ...payload, requestId },
      expectedStateVersion,
    }),
  })
}

interface MarketBookDto {
  assetId: string
  variant: string | null
  asks: MarketBook['asks']
  bids: MarketBook['bids']
  version: number
  capturedAt: string
}

/** 拉取指定标的盘口深度（wire 的 assetId/variant 映射为 itemId/enhancementLevel，version 映射为 sequence） */
export async function fetchMarketBook(
  itemId: string,
  enhancementLevel = 0,
  depth = 20,
  signal?: AbortSignal,
): Promise<MarketBook> {
  // 基础变体在服务端归一为空串：挂单 market.place 以 variant=null 落在 (itemId, "") 簿上，
  // 这里若发 "0" 会查询到一本不存在的幽灵盘口（还会向 _books 插入空簿），永远显示空深度。
  // 强化变体必须与钱包资产键同构发送 "+N"（EnhancementRules.ParseLevel 只认 '+' 前缀）。
  const query = new URLSearchParams({ assetId: itemId, depth: String(depth) })
  if (enhancementLevel > 0) query.set('variant', `+${enhancementLevel}`)
  const dto = await requestJson<MarketBookDto>(`/api/v1/market/book?${query.toString()}`, { signal })
  return {
    itemId: dto.assetId,
    enhancementLevel: parseNumericVariant(dto.variant, enhancementLevel),
    asks: dto.asks,
    bids: dto.bids,
    activeOrderCount: dto.asks.reduce((count, level) => count + level.orderCount, 0)
      + dto.bids.reduce((count, level) => count + level.orderCount, 0),
    sequence: dto.version,
  }
}

interface MarketVariantDto {
  assetId: string
  variant: string | null
  bestAsk: number | null
  bestBid: number | null
}

/**
 * 拉取指定标的存在活动挂单（买或卖任一侧）的全部资产变体摘要。
 * 服务端只回有挂单的变体簿；variant 为 "+N"/null，经 parseNumericVariant 归一为强化等级（基础=0）。
 */
export async function fetchMarketVariants(itemId: string, signal?: AbortSignal): Promise<MarketVariantSummary[]> {
  const query = new URLSearchParams({ assetId: itemId })
  const rows = await requestJson<MarketVariantDto[]>(`/api/v1/market/variants?${query.toString()}`, { signal })
  return rows.map(row => ({
    enhancementLevel: parseNumericVariant(row.variant, 0),
    bestAsk: row.bestAsk,
    bestBid: row.bestBid,
  }))
}

interface MarketItemStatsDto {
  assetId: string
  variant: string | null
  referencePrice?: number | null
  bestAskPrice: number | null
  askQuantity: number
  bestBidPrice: number | null
  bidQuantity: number
  dayVolume: number
  weekVolume: number
}
/** 行情统计缓存键：`itemId|强化等级`，与服务端 (asset_id, variant) 变体簿一一对应（基础簿=0）。 */
export function marketStatsKey(itemId: string, enhancementLevel: number): string {
  return `${itemId}|${enhancementLevel}`
}

/**
 * 拉取全部品种行情统计（服务端 market.item_stats 小表全量读）。
 * 按 `itemId|level` 键入缓存，悬浮「市场价格」副卡渲染时 O(1) 查表、不再产生逐项请求。
 */
export async function fetchMarketStats(signal?: AbortSignal): Promise<Record<string, MarketItemStats>> {
  const rows = await requestJson<MarketItemStatsDto[]>('/api/v1/market/stats', { signal })
  const map: Record<string, MarketItemStats> = {}
  for (const row of rows) {
    const enhancementLevel = parseNumericVariant(row.variant, 0)
    map[marketStatsKey(row.assetId, enhancementLevel)] = {
      itemId: row.assetId,
      enhancementLevel,
      referencePrice: row.referencePrice ?? null,
      bestAskPrice: row.bestAskPrice,
      askQuantity: row.askQuantity,
      bestBidPrice: row.bestBidPrice,
      bidQuantity: row.bidQuantity,
      dayVolume: row.dayVolume,
      weekVolume: row.weekVolume,
    }
  }
  return map
}

/** wire 下发的挂单状态枚举（snake_case，经 mapMarketOrderStatus 归一化为前端 kebab-case） */
type MarketOrderStatusWire = 'active' | 'partially_filled' | 'filled' | 'cancelled' | 'expired'

interface MarketOrderDto {
  orderId: string
  ownerId: string
  assetId: string
  variant: string | null
  side: 'buy' | 'sell'
  price: number
  originalQuantity: number
  remainingQuantity: number
  status: MarketOrderStatusWire
  sequence: number
  createdAt: string
  expiresAt: string | null
}

function mapMarketOrderStatus(status: MarketOrderStatusWire): MarketOrder['status'] {
  switch (status) {
    case 'active': return 'active'
    case 'partially_filled': return 'partially-filled'
    case 'filled': return 'filled'
    case 'cancelled': return 'cancelled'
    case 'expired': return 'expired'
  }
}

/** 拉取系统商店货架状态（商品、官价与当日限购进度）；价格与限购由服务端裁决，客户端只读 */
export async function fetchShopState(playerId: string, signal?: AbortSignal): Promise<ShopState> {
  const state = await requestJson<ShopState>(`/api/v1/players/${encodeURIComponent(playerId)}/shop`, { signal })
  return {
    dayIndex: state.dayIndex,
    offers: (state.offers ?? []).map(offer => ({
      offerId: offer.offerId,
      itemId: offer.itemId,
      unitPrice: offer.unitPrice,
      dailyLimit: offer.dailyLimit ?? null,
      purchasedToday: offer.purchasedToday ?? 0,
    })),
  }
}

/** 拉取指定玩家的全部挂单（wire 状态/字段名归一化为前端 MarketOrder 形态） */
export async function fetchMarketOrders(playerId: string, signal?: AbortSignal): Promise<MarketOrder[]> {
  const rows = await requestJson<MarketOrderDto[]>(`/api/v1/players/${encodeURIComponent(playerId)}/market/orders`, { signal })
  return rows.map(row => ({
    orderId: row.orderId,
    itemId: row.assetId,
    enhancementLevel: parseNumericVariant(row.variant, 0),
    side: row.side,
    price: row.price,
    originalQuantity: row.originalQuantity,
    remainingQuantity: row.remainingQuantity,
    filledQuantity: Math.max(0, row.originalQuantity - row.remainingQuantity),
    status: mapMarketOrderStatus(row.status),
    sequence: row.sequence,
    createdAt: row.createdAt,
    expiresAt: row.expiresAt,
  }))
}

export function fetchSimulatorProfile(playerId: string, targetPlayerId?: string, signal?: AbortSignal): Promise<SimulatorProfileDto> {
  const query = targetPlayerId && targetPlayerId !== playerId ? `?targetPlayerId=${encodeURIComponent(targetPlayerId)}` : ''
  return requestJson<SimulatorProfileDto>(`/api/v1/players/${encodeURIComponent(playerId)}/simulator-profile${query}`, { signal })
}

export function fetchSimulatorEnvironment(playerId: string, signal?: AbortSignal): Promise<SimulatorEnvironmentDto> {
  return requestJson<SimulatorEnvironmentDto>(`/api/v1/players/${encodeURIComponent(playerId)}/simulator-environment`, { signal })
}

/** 排行榜 HTTP 接口异常（保留 HTTP 状态码与服务端错误标题，便于 UI 精确区分 503 整点凝练等待态与 404/401） */
export class LeaderboardApiError extends Error {
  readonly status: number
  readonly code?: string
  readonly retryAfterSeconds?: number

  constructor(message: string, status: number, code?: string, retryAfterSeconds?: number) {
    super(message)
    this.name = 'LeaderboardApiError'
    this.status = status
    this.code = code
    this.retryAfterSeconds = retryAfterSeconds
  }
}

interface LeaderboardEtagCacheEntry<T> {
  etag: string
  data: T
}

let leaderboardCacheViewerKey = ''
const leaderboardEtagCache = new Map<string, LeaderboardEtagCacheEntry<unknown>>()

async function fetchLeaderboardWithEtag<T>(
  path: string,
  viewerId: string,
  signal?: AbortSignal,
): Promise<T> {
  if (viewerId !== leaderboardCacheViewerKey) {
    leaderboardCacheViewerKey = viewerId
    leaderboardEtagCache.clear()
  }

  const cached = leaderboardEtagCache.get(path) as LeaderboardEtagCacheEntry<T> | undefined
  const controller = new AbortController()
  let timedOut = false
  const timeoutId = window.setTimeout(() => {
    timedOut = true
    controller.abort()
  }, REQUEST_TIMEOUT_MS)
  const forwardAbort = () => controller.abort()
  if (signal) {
    if (signal.aborted) controller.abort()
    else signal.addEventListener('abort', forwardAbort, { once: true })
  }

  const headers = new Headers()
  if (cached?.etag) headers.set('If-None-Match', cached.etag)

  try {
    const response = await fetch(getApiUrl(path), {
      method: 'GET',
      signal: controller.signal,
      headers,
      credentials: 'include',
      cache: 'no-cache',
    })
    if (response.status === 304 && cached) {
      return cached.data
    }
    if (!response.ok) {
      const retryRaw = response.headers.get('Retry-After')
      const retrySeconds = retryRaw ? Number.parseInt(retryRaw, 10) : undefined
      let message = `请求失败 (${response.status})`
      let code: string | undefined
      try {
        const payload = await response.clone().json() as { detail?: string; message?: string; title?: string }
        message = payload.detail ?? payload.message ?? payload.title ?? message
        code = payload.title
      } catch {
        const text = await response.text().catch(() => '')
        if (text) message = text
      }
      throw new LeaderboardApiError(
        message,
        response.status,
        code,
        Number.isFinite(retrySeconds) ? retrySeconds : undefined,
      )
    }

    const data = await response.json() as T
    const etag = response.headers.get('ETag')
    if (etag) {
      leaderboardEtagCache.set(path, { etag, data })
    }
    return data
  } catch (error) {
    if (timedOut) throw new Error('网络请求超时，请稍后重试。')
    throw error
  } finally {
    window.clearTimeout(timeoutId)
    signal?.removeEventListener('abort', forwardAbort)
  }
}

/** 拉取全服排行榜目录（12 张榜单元信息 + 当前与下一次整点刷新时间；支持 If-None-Match / 304 复用） */
export function fetchLeaderboardIndex(viewerId = '', signal?: AbortSignal): Promise<LeaderboardIndexDto> {
  return fetchLeaderboardWithEtag<LeaderboardIndexDto>('/api/v1/leaderboards', viewerId, signal)
}

/** 拉取指定全服排行榜 Top 50 与当前登录角色个人名次（含防串号 ETag / 304 复用） */
export function fetchLeaderboardBoard(
  boardId: string,
  viewerId = '',
  signal?: AbortSignal,
): Promise<LeaderboardSnapshotDto> {
  return fetchLeaderboardWithEtag<LeaderboardSnapshotDto>(
    `/api/v1/leaderboards/${encodeURIComponent(boardId)}`,
    viewerId,
    signal,
  )
}

/** 拉取玩家可见邮件列表（列表投影不含正文 body）与摘要计数 */
export function fetchMailList(signal?: AbortSignal): Promise<MailListResponseDto> {
  return requestJson<MailListResponseDto>('/api/v1/mails', { signal })
}

/** 按需拉取单封邮件详情（首次打开未读邮件时服务端同步落库已读并返回最新摘要） */
export function fetchMailDetail(mailKey: string, signal?: AbortSignal): Promise<MailDetailResponseDto> {
  return requestJson<MailDetailResponseDto>(`/api/v1/mails/${encodeURIComponent(mailKey)}`, { signal })
}

/** 显式标记指定邮件为已读 */
export function markMailRead(mailKey: string): Promise<MailReadResponseDto> {
  return requestJson<MailReadResponseDto>(`/api/v1/mails/${encodeURIComponent(mailKey)}/read`, { method: 'POST' })
}

/** 领取单封邮件全部附件（幂等 + Serializable 经济入账） */
export function claimMailAttachments(mailKey: string): Promise<MailClaimResponseDto> {
  return requestJson<MailClaimResponseDto>(`/api/v1/mails/${encodeURIComponent(mailKey)}/claim`, {
    method: 'POST',
    body: JSON.stringify({ requestId: createRequestId() }),
  })
}

/** 一键领取当前所有可领邮件附件 */
export function claimAllMailAttachments(): Promise<MailClaimResponseDto> {
  return requestJson<MailClaimResponseDto>('/api/v1/mails/claim-all', {
    method: 'POST',
    body: JSON.stringify({ requestId: createRequestId() }),
  })
}

/** 删除单封已无未领附件的邮件 */
export function deleteMail(mailKey: string): Promise<MailDeleteResponseDto> {
  return requestJson<MailDeleteResponseDto>(`/api/v1/mails/${encodeURIComponent(mailKey)}`, {
    method: 'DELETE',
  })
}

/** 批量删除已读且无可领附件的邮件 */
export function deleteReadMails(): Promise<MailDeleteResponseDto> {
  return requestJson<MailDeleteResponseDto>('/api/v1/mails/delete-read', {
    method: 'POST',
  })
}

/** 查询当前角色每日签到状态（含连续签到、当日可签状态与期望均值预览） */
export function fetchCheckinStatus(signal?: AbortSignal): Promise<CheckinStatusDto> {
  return requestJson<CheckinStatusDto>('/api/v1/checkin', { signal })
}

/** 领取今日每日签到绑定功德奖励（服务端原子入账与防重） */
export function claimDailyCheckin(): Promise<CheckinClaimResponseDto> {
  return requestJson<CheckinClaimResponseDto>('/api/v1/checkin/claim', {
    method: 'POST',
  })
}

/** 提交兑换码核销并直接入账背包 */
export function redeemGiftCode(code: string): Promise<RedeemCodeResponseDto> {
  return requestJson<RedeemCodeResponseDto>('/api/v1/redeem', {
    method: 'POST',
    body: JSON.stringify({ code }),
  })
}

/** 拉取道友攻略列表（支持热度 hot / 打赏 reward 排序及本人发布筛选） */
export function fetchGuideList(
  sort: 'hot' | 'reward',
  mineOnly = false,
  limit = 50,
  offset = 0,
  signal?: AbortSignal,
): Promise<GuideListResponseDto> {
  const query = new URLSearchParams({
    sort,
    mine: mineOnly ? 'true' : 'false',
    limit: String(limit),
    offset: String(offset),
  })
  return requestJson<GuideListResponseDto>(`/api/v1/guides?${query.toString()}`, { signal })
}

/** 拉取单篇攻略详情（已解锁时服务端自动去重记录阅读） */
export function fetchGuideDetail(postId: string, signal?: AbortSignal): Promise<GuidePostDetailDto> {
  return requestJson<GuidePostDetailDto>(`/api/v1/guides/${encodeURIComponent(postId)}`, { signal })
}

/** 发布新攻略（要求个人榜 Top 50 且当日未发帖） */
export function createGuidePost(payload: GuidePostUpsertPayload): Promise<GuidePostDetailDto> {
  return requestJson<GuidePostDetailDto>('/api/v1/guides', {
    method: 'POST',
    body: JSON.stringify(payload),
  })
}

/** 编辑本人已发布的攻略 */
export function updateGuidePost(postId: string, payload: GuidePostUpsertPayload): Promise<GuidePostDetailDto> {
  return requestJson<GuidePostDetailDto>(`/api/v1/guides/${encodeURIComponent(postId)}`, {
    method: 'PUT',
    body: JSON.stringify(payload),
  })
}

/** 删除本人已发布的攻略（当日已耗额度不退回） */
export function deleteGuidePost(postId: string): Promise<{ deleted: boolean; postId: string }> {
  return requestJson<{ deleted: boolean; postId: string }>(`/api/v1/guides/${encodeURIComponent(postId)}`, {
    method: 'DELETE',
  })
}

/** 点赞或取消点赞攻略 */
export function setGuidePostLike(postId: string, liked: boolean): Promise<SetGuideLikeResponseDto> {
  return requestJson<SetGuideLikeResponseDto>(`/api/v1/guides/${encodeURIComponent(postId)}/like`, {
    method: 'POST',
    body: JSON.stringify({ liked }),
  })
}

/** 支付灵石永久解锁付费攻略正文 */
export function unlockGuidePost(postId: string, expectedPrice: number): Promise<GuidePostDetailDto> {
  return requestJson<GuidePostDetailDto>(`/api/v1/guides/${encodeURIComponent(postId)}/unlock`, {
    method: 'POST',
    body: JSON.stringify({ requestId: createRequestId(), expectedPrice }),
  })
}

/** 向攻略打赏灵石 */
export function tipGuidePost(postId: string, amount: number): Promise<TipGuidePostResponseDto> {
  return requestJson<TipGuidePostResponseDto>(`/api/v1/guides/${encodeURIComponent(postId)}/tip`, {
    method: 'POST',
    body: JSON.stringify({ requestId: createRequestId(), amount }),
  })
}

/** 支付灵石解锁查阅目标玩家的角色名片配装（维持至北京时间当日结束零点） */
export function unlockPlayerCardLoadout(
  targetPlayerId: string,
  expectedPrice: number,
): Promise<UnlockPlayerLoadoutResponseDto> {
  return requestJson<UnlockPlayerLoadoutResponseDto>(
    `/api/v1/players/${encodeURIComponent(targetPlayerId)}/unlock-loadout`,
    {
      method: 'POST',
      body: JSON.stringify({ requestId: createRequestId(), expectedPrice }),
    },
  )
}
