import type { ActionDefinition, CombatAbilityDefinition, CombatEncounterDefinition, CombatEquipmentDefinition, ItemDefinition, MarketBook, MarketMetrics, MarketOrder, WorldMapDefinition } from '../game/types'
import { getAccessToken } from './access_token'

/**
 * 获取 HTTP API 请求基准地址（同源优先，支持环境变量覆盖）
 */
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

/**
 * 组装完整的 HTTP API 接口路径
 * @param path 相对 API 路径
 */
export function getApiUrl(path: string): string {
  return `${getHttpBase()}${path.startsWith('/') ? path : `/${path}`}`
}

/**
 * 动态构造通用 HTTP 请求头（自动附加 X-Access-Token）
 */
// 每次请求动态构造头部：令牌保存在 localStorage，用户可能随时修改。
function buildHeaders(): Record<string, string> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  const token = getAccessToken()
  if (token) headers['X-Access-Token'] = token
  return headers
}

/**
 * 发送标准 GET JSON 请求
 */
async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(getApiUrl(path), {
    headers: buildHeaders(),
    signal,
  })
  if (!response.ok) throw new Error(`HTTP ${response.status}`)
  return response.json() as Promise<T>
}

/**
 * 发送带 ETag 协商缓存的 GET JSON 请求
 */
async function getConditionalJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(getApiUrl(path), {
    headers: buildHeaders(),
    // 由浏览器使用服务端 ETag 做重验证；跨域时不会因脚本自定义
    // If-None-Match 请求头而为每次轮询额外触发 CORS 预检。
    cache: 'no-cache',
    signal,
  })
  if (!response.ok) throw new Error(`HTTP ${response.status}`)
  return response.json() as Promise<T>
}

/**
 * 从服务端拉取全量行动定义目录 (/api/actions)
 */
export function fetchActionDefinitions(signal?: AbortSignal): Promise<ActionDefinition[]> {
  return getJson<ActionDefinition[]>('/api/actions', signal)
}

/**
 * 从服务端拉取全量物品定义目录 (/api/items)
 */
export function fetchItemDefinitions(signal?: AbortSignal): Promise<ItemDefinition[]> {
  return getJson<ItemDefinition[]>('/api/items', signal)
}

/**
 * 从服务端拉取全量功法技能目录 (/api/combat/abilities)
 */
export function fetchCombatAbilities(signal?: AbortSignal): Promise<CombatAbilityDefinition[]> {
  return getJson<CombatAbilityDefinition[]>('/api/combat/abilities', signal)
}

/**
 * 从服务端拉取全量战斗装备配置目录 (/api/combat/equipment)
 */
export function fetchCombatEquipment(signal?: AbortSignal): Promise<CombatEquipmentDefinition[]> {
  return getJson<CombatEquipmentDefinition[]>('/api/combat/equipment', signal)
}

/**
 * 从服务端拉取全量遭遇战副本目录 (/api/combat/encounters)
 */
export function fetchCombatEncounters(signal?: AbortSignal): Promise<CombatEncounterDefinition[]> {
  return getJson<CombatEncounterDefinition[]>('/api/combat/encounters', signal)
}

/**
 * 从服务端拉取全量大地图配置目录 (/api/world/maps)
 */
export function fetchWorldMaps(signal?: AbortSignal): Promise<WorldMapDefinition[]> {
  return getJson<WorldMapDefinition[]>('/api/world/maps', signal)
}

/**
 * 查询指定物品与强化等级的市场订单薄深度 (/api/market/book)
 * @param itemId 物品 ID
 * @param enhancementLevel 强化等级
 * @param depth 深度档位数量（默认 20）
 * @param signal AbortSignal
 */
export function fetchMarketBook(itemId: string, enhancementLevel = 0, depth = 20, signal?: AbortSignal): Promise<MarketBook> {
  const query = new URLSearchParams({
    itemId,
    enhancementLevel: String(enhancementLevel),
    depth: String(depth),
  })
  return getConditionalJson<MarketBook>(`/api/market/book?${query.toString()}`, signal)
}

/**
 * 查询市场全局度量指标 (/api/market/metrics)
 */
export function fetchMarketMetrics(signal?: AbortSignal): Promise<MarketMetrics> {
  return getConditionalJson<MarketMetrics>('/api/market/metrics', signal)
}

/**
 * 查询指定玩家的个人市场挂单列表 (/api/players/{playerId}/market/orders)
 */
export function fetchMarketOrders(playerId: string, signal?: AbortSignal): Promise<MarketOrder[]> {
  return getConditionalJson<MarketOrder[]>(`/api/players/${encodeURIComponent(playerId)}/market/orders`, signal)
}
