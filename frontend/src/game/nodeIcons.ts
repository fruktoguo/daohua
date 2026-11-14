import { buildNodeIconLookup, lookupNodeIconFromMap } from '../components/worldMapVisibility'

/**
 * 地图节点图像资源注册表。
 *
 * 约定：在 `frontend/src/assets/nodes/` 下放入以 `nodeId` 命名的图片
 * （如 `node.cloud-gate.webp`、`node.zongmen.webp`），
 * 前端会自动渲染为「上方灵境图像 + 下方紧凑名称牌匾」的地标形态；
 * 同 ID 存在多格式时按 `.webp > .png > .svg` 覆盖，无图像节点维持纯牌匾形态。
 */
const svgModules = import.meta.glob('../assets/nodes/*.svg', {
  eager: true,
  query: '?url',
  import: 'default',
}) as Record<string, string>

const pngModules = import.meta.glob('../assets/nodes/*.png', {
  eager: true,
  query: '?url',
  import: 'default',
}) as Record<string, string>

const webpModules = import.meta.glob('../assets/nodes/*.webp', {
  eager: true,
  query: '?url',
  import: 'default',
}) as Record<string, string>

const NODE_ICON_MAP = buildNodeIconLookup([svgModules, pngModules, webpModules])

/**
 * 解析地图节点地标图像 URL。
 * @param nodeId 节点 ID（`node.zongmen.*` 未配置专属图时自动回退 `node.zongmen`）
 */
export function resolveNodeIconUrl(nodeId?: string | null): string | null {
  return nodeId ? lookupNodeIconFromMap(NODE_ICON_MAP, nodeId) : null
}
