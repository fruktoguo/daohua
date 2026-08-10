import type { ActionDefinition, CombatAbilityDefinition, CombatEncounterDefinition, CombatEquipmentDefinition, ItemDefinition, MarketBook, MarketMetrics, MarketOrder, WorldMapDefinition } from '../game/types'

function getHttpBase(): string {
  const configuredApi = import.meta.env.VITE_API_URL as string | undefined
  if (configuredApi) return configuredApi.replace(/\/$/, '')

  const configuredWebSocket = import.meta.env.VITE_WS_URL as string | undefined
  if (configuredWebSocket) {
    const endpoint = new URL(configuredWebSocket)
    endpoint.protocol = endpoint.protocol === 'wss:' ? 'https:' : 'http:'
    return endpoint.origin
  }

  return window.location.origin
}

export function getApiUrl(path: string): string {
  return `${getHttpBase()}${path.startsWith('/') ? path : `/${path}`}`
}

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(getApiUrl(path), {
    headers: { Accept: 'application/json' },
    signal,
  })
  if (!response.ok) throw new Error(`HTTP ${response.status}`)
  return response.json() as Promise<T>
}

async function getConditionalJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(getApiUrl(path), {
    headers: { Accept: 'application/json' },
    // 由浏览器使用服务端 ETag 做重验证；跨域时不会因脚本自定义
    // If-None-Match 请求头而为每次轮询额外触发 CORS 预检。
    cache: 'no-cache',
    signal,
  })
  if (!response.ok) throw new Error(`HTTP ${response.status}`)
  return response.json() as Promise<T>
}

export function fetchActionDefinitions(signal?: AbortSignal): Promise<ActionDefinition[]> {
  return getJson<ActionDefinition[]>('/api/actions', signal)
}

export function fetchItemDefinitions(signal?: AbortSignal): Promise<ItemDefinition[]> {
  return getJson<ItemDefinition[]>('/api/items', signal)
}

export function fetchCombatAbilities(signal?: AbortSignal): Promise<CombatAbilityDefinition[]> {
  return getJson<CombatAbilityDefinition[]>('/api/combat/abilities', signal)
}

export function fetchCombatEquipment(signal?: AbortSignal): Promise<CombatEquipmentDefinition[]> {
  return getJson<CombatEquipmentDefinition[]>('/api/combat/equipment', signal)
}

export function fetchCombatEncounters(signal?: AbortSignal): Promise<CombatEncounterDefinition[]> {
  return getJson<CombatEncounterDefinition[]>('/api/combat/encounters', signal)
}

export function fetchWorldMaps(signal?: AbortSignal): Promise<WorldMapDefinition[]> {
  return getJson<WorldMapDefinition[]>('/api/world/maps', signal)
}

export function fetchMarketBook(itemId: string, enhancementLevel = 0, depth = 20, signal?: AbortSignal): Promise<MarketBook> {
  const query = new URLSearchParams({
    itemId,
    enhancementLevel: String(enhancementLevel),
    depth: String(depth),
  })
  return getConditionalJson<MarketBook>(`/api/market/book?${query.toString()}`, signal)
}

export function fetchMarketMetrics(signal?: AbortSignal): Promise<MarketMetrics> {
  return getConditionalJson<MarketMetrics>('/api/market/metrics', signal)
}

export function fetchMarketOrders(playerId: string, signal?: AbortSignal): Promise<MarketOrder[]> {
  return getConditionalJson<MarketOrder[]>(`/api/players/${encodeURIComponent(playerId)}/market/orders`, signal)
}
