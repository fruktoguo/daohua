import type { CombatEncounterDefinition, MarketBook, MarketMetrics, MarketOrder, WorldMapDefinition } from '../game/types'
import { createRequestId } from './protocol'

/** HTTP API 只使用同源或显式配置的 v1 地址，认证由 HttpOnly Cookie 承担。 */
export function getHttpBase(): string {
  const configured = import.meta.env.VITE_API_URL as string | undefined
  return configured ? configured.replace(/\/$/, '') : window.location.origin
}

export function getApiUrl(path: string): string {
  return `${getHttpBase()}${path.startsWith('/') ? path : `/${path}`}`
}

export async function fetchGameCatalog(signal?: AbortSignal): Promise<{
  maps: WorldMapDefinition[]
  encounters: CombatEncounterDefinition[]
}> {
  const [maps, encounters] = await Promise.all([
    requestJson<WorldMapDefinition[]>('/api/v1/catalog/maps', { signal }),
    requestJson<CombatEncounterDefinition[]>('/api/v1/catalog/encounters', { signal }),
  ])
  return { maps, encounters }
}

function parseNumericVariant(value: string | null, fallback: number): number {
  if (value === null) return fallback
  const parsed = Number(value)
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback
}

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

export async function requestJson<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body !== undefined && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  const response = await fetch(getApiUrl(path), {
    ...init,
    headers,
    credentials: 'include',
    cache: init.method && init.method !== 'GET' ? 'no-store' : 'no-cache',
  })
  if (!response.ok) throw await parseError(response)
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

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

export async function fetchMarketBook(
  itemId: string,
  enhancementLevel = 0,
  depth = 20,
  signal?: AbortSignal,
): Promise<MarketBook> {
  const query = new URLSearchParams({ assetId: itemId, variant: String(enhancementLevel), depth: String(depth) })
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

interface MarketMetricsDto {
  partitionCount: number
  activeOrderCount: number
  placementCount: number
  matchCount: number
  cancelCount: number
  rejectedCount: number
}

export async function fetchMarketMetrics(signal?: AbortSignal): Promise<MarketMetrics> {
  const dto = await requestJson<MarketMetricsDto>('/api/v1/market/metrics', { signal })
  return {
    shardCount: dto.partitionCount,
    activeOrderCount: dto.activeOrderCount,
    placementCount: dto.placementCount,
    matchCount: dto.matchCount,
    cancelCount: dto.cancelCount,
    rejectedCount: dto.rejectedCount,
    tradedQuantity: 0,
    tradedGrossAmount: 0,
  }
}

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
    expiresAt: row.createdAt,
  }))
}
