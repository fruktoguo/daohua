import type { MarketBook, MarketMetrics, MarketOrder } from '../game/types'

/** HTTP API 只使用同源或显式配置的 v1 地址，认证由 HttpOnly Cookie 承担。 */
export function getHttpBase(): string {
  const configured = import.meta.env.VITE_API_URL as string | undefined
  return configured ? configured.replace(/\/$/, '') : window.location.origin
}

export function getApiUrl(path: string): string {
  return `${getHttpBase()}${path.startsWith('/') ? path : `/${path}`}`
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
): Promise<TResult> {
  return requestJson<TResult>(`/api/v1/players/${encodeURIComponent(playerId)}/commands`, {
    method: 'POST',
    body: JSON.stringify({ command, payload }),
  })
}

interface MarketBookDto {
  assetId?: string
  itemId?: string
  variant?: string | null
  enhancementLevel?: number
  asks: MarketBook['asks']
  bids: MarketBook['bids']
  activeOrderCount?: number
  version?: number
  sequence?: number
  capturedAt?: string
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
    itemId: dto.itemId ?? dto.assetId ?? itemId,
    enhancementLevel: dto.enhancementLevel ?? enhancementLevel,
    asks: dto.asks ?? [],
    bids: dto.bids ?? [],
    activeOrderCount: dto.activeOrderCount ?? 0,
    sequence: dto.sequence ?? dto.version ?? 0,
  }
}

interface MarketMetricsDto {
  shardCount?: number
  activeOrderCount?: number
  placementCount?: number
  matchCount?: number
  cancelCount?: number
  rejectedCount?: number
  tradedQuantity?: number
  tradedGrossAmount?: number
}

export async function fetchMarketMetrics(signal?: AbortSignal): Promise<MarketMetrics> {
  const dto = await requestJson<MarketMetricsDto>('/api/v1/market/metrics', { signal })
  return {
    shardCount: dto.shardCount ?? 1,
    activeOrderCount: dto.activeOrderCount ?? 0,
    placementCount: dto.placementCount ?? 0,
    matchCount: dto.matchCount ?? 0,
    cancelCount: dto.cancelCount ?? 0,
    rejectedCount: dto.rejectedCount ?? 0,
    tradedQuantity: dto.tradedQuantity ?? 0,
    tradedGrossAmount: dto.tradedGrossAmount ?? 0,
  }
}

interface MarketOrderDto {
  orderId: string
  ownerId?: string
  itemId?: string
  assetId?: string
  variant?: string | null
  enhancementLevel?: number
  side: MarketOrder['side']
  price: number
  originalQuantity: number
  remainingQuantity: number
  filledQuantity?: number
  status: MarketOrder['status']
  sequence: number
  createdAt: string
  expiresAt?: string
}

export async function fetchMarketOrders(playerId: string, signal?: AbortSignal): Promise<MarketOrder[]> {
  const rows = await requestJson<MarketOrderDto[]>(`/api/v1/players/${encodeURIComponent(playerId)}/market/orders`, { signal })
  return rows.map(row => ({
    orderId: row.orderId,
    itemId: row.itemId ?? row.assetId ?? '',
    enhancementLevel: row.enhancementLevel ?? Number(row.variant ?? 0),
    side: row.side,
    price: row.price,
    originalQuantity: row.originalQuantity,
    remainingQuantity: row.remainingQuantity,
    filledQuantity: row.filledQuantity ?? Math.max(0, row.originalQuantity - row.remainingQuantity),
    status: row.status,
    sequence: row.sequence,
    createdAt: row.createdAt,
    expiresAt: row.expiresAt ?? row.createdAt,
  }))
}
