/**
 * 图标资源注册表。
 *
 * 约定：只要在 `frontend/src/assets/items/` 下放入以「资源 ID」命名的图片，
 * 前端就会自动用该图片替代纯文本名称渲染；没有对应图片的条目自动回退为原有的文字瓦片。
 *
 * 资源 ID 即配置表里的 `id`，覆盖三类：
 * - 物品 / 材料：`item.qi-gathering-pill`、`currency.spirit-stone`
 * - 装备：`equipment.qingfeng-sword`
 * - 功法战技：`ability.heavy-slash`
 *
 * 支持两种后缀：`.webp`（运行时资源，48px 网页版，由 `art/` 的 64px 切图管线降采样产出）与
 * `.png`（早期直接落到本目录的历史图片）。同名时以 `.webp` 为准。
 *
 * 因此新增图标只需要丢一个文件，无需在任何地方维护清单。
 */
const pngModules = import.meta.glob('../assets/items/*.png', {
  eager: true,
  query: '?url',
  import: 'default',
}) as Record<string, string>

const webpModules = import.meta.glob('../assets/items/*.webp', {
  eager: true,
  query: '?url',
  import: 'default',
}) as Record<string, string>

const ICONS_BY_ITEM_ID = new Map<string, string>()

/** 按「去扩展名的文件名 = 资源 ID」把 glob 结果登记进映射。 */
function registerIconModules(modules: Record<string, string>): void {
  for (const [modulePath, url] of Object.entries(modules)) {
    const fileName = modulePath.slice(modulePath.lastIndexOf('/') + 1)
    ICONS_BY_ITEM_ID.set(fileName.replace(/\.(png|webp)$/i, ''), url)
  }
}

registerIconModules(pngModules)
// 后登记者覆盖先登记者：同名资源优先使用 .webp（运行时资源）
registerIconModules(webpModules)

/**
 * 解析资源图标地址。
 * @param itemId 资源唯一 ID（物品 / 装备 / 功法，如 `item.qi-gathering-pill`、`ability.heavy-slash`）
 * @returns 图标 URL；该资源没有配备图片时返回 `undefined`，调用方需回退到文本渲染。
 */
export function resolveItemIconUrl(itemId?: string | null): string | undefined {
  if (!itemId) return undefined
  return ICONS_BY_ITEM_ID.get(itemId.trim())
}

/** 该资源是否配有专用图标。 */
export function hasItemIcon(itemId?: string | null): boolean {
  return resolveItemIconUrl(itemId) !== undefined
}
