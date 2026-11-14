/**
 * 纯环境 URL 拼接工具（无业务/i18n 依赖，可直接在 Node 单元测试中以 ESM 导入）。
 * HTTP API 只使用同源或显式配置的 VITE_API_URL。
 */

/** 获取 API 根路径。 */
export function getHttpBase(): string {
  const configured =
    typeof import.meta !== 'undefined' && import.meta.env
      ? (import.meta.env.VITE_API_URL as string | undefined)
      : undefined
  if (configured) return configured.replace(/\/$/, '')
  return typeof window !== 'undefined' && window.location ? window.location.origin : ''
}

/** 拼接完整 API 地址（path 自动补前导斜杠）。 */
export function getApiUrl(path: string): string {
  return `${getHttpBase()}${path.startsWith('/') ? path : `/${path}`}`
}
