import { registerBaseAttributeAnchors, type BaseAttributeAnchor } from '../game/combatConstants'
import type { ActionDefinition, BuildingDefinition, CombatAbilityDefinition, CombatEncounterDefinition, CombatEquipmentDefinition, ItemDefinition, MapNodeBuilding, MarketBook, MarketItemStats, MarketOrder, MarketVariantSummary, ShopState, TriggerBuffOption, TriggerSpec, WorldMapDefinition } from '../game/types'
import type { SimulatorEnvironmentDto, SimulatorProfileDto } from '../game/simulator/simulatorTypes'
import {
  createRequestId,
  type LeaderboardIndexDto,
  type LeaderboardSnapshotDto,
  type MailClaimResponseDto,
  type MailDeleteResponseDto,
  type MailDetailResponseDto,
  type MailListResponseDto,
  type MailReadResponseDto,
} from './protocol'
import { getApiUrl, getHttpBase } from './apiUrl'

export { getApiUrl, getHttpBase }

/** API 请求的最大等待时间，避免网络半断开时页面永久停留在加载态。 */
const REQUEST_TIMEOUT_MS = 15_000

/**
 * 最近一次配表响应的游戏数据指纹（ETag 裸值，已去引号与压缩代理追加的编码后缀）。
 * WebSocket Hello 握手回传该值，服务端不一致时以 GAME_DATA_MISMATCH 拒绝并触发配表重载。
 */
let catalogFingerprint: string | null = null

/** 当前客户端持有的游戏数据指纹；未拉取过配表时为 null。 */
export function getCatalogFingerprint(): string | null {
  return catalogFingerprint
}

/**
 * 拉取游戏静态配表全集（行动/地图/遭遇/物品/装备/基础属性锚点/功法/可建造建筑）。
 * 副作用：baseAttributes 拉取后注册进 combatConstants 锚点表，境界/属性推算依赖其结果。
 * items 端点失败时容忍为空列表（前端保留静态物品兜底），其余端点失败整体抛错。
 */
export async function fetchGameCatalog(signal?: AbortSignal): Promise<{
  actions: ActionDefinition[]
  maps: WorldMapDefinition[]
  encounters: CombatEncounterDefinition[]
  items: ItemDefinition[]
  equipment: CombatEquipmentDefinition[]
  abilities: CombatAbilityDefinition[]
  buildings: BuildingDefinition[]
  buffs: TriggerBuffOption[]
  triggerSpec: TriggerSpec
}> {
  const [actions, maps, encounters, items, equipment, baseAttributes, abilities, buildings, buffs, triggerSpec] = await Promise.all([
    requestJson<ActionDefinition[]>('/api/v1/catalog/actions', { signal }),
    requestJson<WorldMapDefinition[]>('/api/v1/catalog/maps', { signal }),
    requestJson<CombatEncounterDefinition[]>('/api/v1/catalog/encounters', { signal }),
    requestJson<ItemDefinition[]>('/api/v1/catalog/items', { signal }).catch(() => [] as ItemDefinition[]),
    requestJson<CombatEquipmentDefinition[]>('/api/v1/catalog/equipment', { signal }),
    requestJson<BaseAttributeAnchor[]>('/api/v1/catalog/base-attributes', { signal }),
    requestJson<CombatAbilityDefinition[]>('/api/v1/catalog/abilities', { signal }),
    requestJson<BuildingDefinition[]>('/api/v1/catalog/buildings', { signal }),
    // Buff 词表与触发条件规约常量：服务端单一权威，前端条件编辑器不得另写一份。
    requestJson<TriggerBuffOption[]>('/api/v1/catalog/buffs', { signal }).catch(() => [] as TriggerBuffOption[]),
    requestJson<TriggerSpec>('/api/v1/catalog/triggers', { signal })
      .catch(() => ({ maxTriggersPerList: 4, equalTolerance: 1e-6 }) as TriggerSpec),
  ])
  registerBaseAttributeAnchors(baseAttributes)
  return { actions, maps, encounters, items, equipment, abilities, buildings, buffs, triggerSpec }
}

/**
 * 拉取全图节点公共建筑索引（nodeId → 建筑摘要），供大地图地点详情合并展示玩家建筑。
 * 动态共享数据、非配表目录，故不走 catalog 指纹缓存通道。
 */
export async function fetchNodeBuildingIndex(signal?: AbortSignal): Promise<Record<string, MapNodeBuilding[]>> {
  return requestJson<Record<string, MapNodeBuilding[]>>('/api/v1/world/node-buildings', { signal })
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

async function parseError(response: Response): Promise<Error> {
  let detail = `HTTP ${response.status}`
  try {
    const body = await response.json() as ProblemDetailsLike
    detail = body.detail || body.message || body.title || body.code || detail
  } catch {
    // 非 JSON 错误只保留状态码，避免把服务端 HTML 泄漏到 UI。
  }
  return new Error(detail)
}

/**
 * 同源 JSON API 请求基座。
 * 携带 HttpOnly Cookie（credentials: include）、强制 JSON 头、15 秒超时中止，
 * 并转发调用方 AbortSignal；非 2xx 解析错误体抛出可读 Error，204 返回 undefined。
 */
export async function requestJson<T>(path: string, init: RequestInit = {}): Promise<T> {
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
      credentials: 'include',
      cache: init.method && init.method !== 'GET' ? 'no-store' : 'no-cache',
    })
    if (!response.ok) throw await parseError(response)
    // 配表端点的 ETag 即游戏数据指纹，随每次成功响应刷新，供 Hello 握手回传。
    // 压缩代理会改写 ETag 表示压缩变体（Caddy encode 追加 -zstd/-gzip 等后缀），
    // 指纹必须与传输层解耦：剥离编码后缀还原服务端权威值，否则握手恒 GAME_DATA_MISMATCH。
    if (path.startsWith('/api/v1/catalog/')) {
      const etag = response.headers.get('ETag')
      if (etag) catalogFingerprint = etag.replace(/^(?:W\/)?"|"$/g, '').replace(/-(?:zstd|gzip|br|deflate)$/, '')
    }
    if (response.status === 204) return undefined as T
    return response.json() as Promise<T>
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
