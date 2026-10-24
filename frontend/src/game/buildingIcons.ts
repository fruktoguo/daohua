/**
 * 建筑图标资源注册表。
 *
 * 约定：只要在 `frontend/src/assets/buildings/` 下放入以「建筑 ID」命名的图片，
 * 前端就会自动用该图片替代 Emoji 🏛️ 或纯文本名称渲染；没有对应图片的条目自动回退为原有占位。
 *
 * 建筑 ID 即配置表里的 `id`（如 `building.meditation-circle`、`building.demon-seal-array` 等）。
 */
const webpModules = import.meta.glob('../assets/buildings/*.webp', {
  eager: true,
  query: '?url',
  import: 'default',
}) as Record<string, string>

const ICONS_BY_BUILDING_ID = new Map<string, string>()

for (const [modulePath, url] of Object.entries(webpModules)) {
  const fileName = modulePath.slice(modulePath.lastIndexOf('/') + 1)
  ICONS_BY_BUILDING_ID.set(fileName.replace(/\.webp$/i, ''), url)
}

/**
 * 解析建筑图标地址。
 * @param buildingId 建筑唯一 ID（如 `building.meditation-circle`）
 * @returns 图标 URL；未配置时返回 undefined
 */
export function resolveBuildingIconUrl(buildingId?: string | null): string | undefined {
  if (!buildingId) return undefined
  return ICONS_BY_BUILDING_ID.get(buildingId.trim())
}

/** 该建筑是否配有专用图标。 */
export function hasBuildingIcon(buildingId?: string | null): boolean {
  return resolveBuildingIconUrl(buildingId) !== undefined
}
