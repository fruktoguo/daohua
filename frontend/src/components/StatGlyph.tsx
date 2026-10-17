import React, { memo } from 'react'
import type { PillTone } from './PillLabel'

/**
 * 属性小图标静态映射表 (由 Vite import.meta.glob 自动收集)
 */
const GLYPH_MODULES = import.meta.glob('../assets/glyphs/glyph.*.webp', {
  eager: true,
  import: 'default',
}) as Record<string, string>

/**
 * 根据属性标识解析小图标资产 URL
 * @param glyphId 属性名或 glyph 标识（如 'hp', 'mp', 'phys-atk', 'spell-atk'）
 */
export function resolveGlyphUrl(glyphId: string): string | null {
  const normalized = glyphId.replace(/^glyph\./, '')
  const path = `../assets/glyphs/glyph.${normalized}.webp`
  return GLYPH_MODULES[path] ?? null
}

/**
 * 极简二值属性小图标组件 (StatGlyph)
 *
 * 采用 CSS mask 遮罩体系：
 * 资产本身为纯白二值剪影，通过 mask-image 与 currentColor / background-color 动态着色，
 * 天然自适应父级色系（--emerald / --cyan / --crimson / --gold 等），无需额外请求不同颜色切片。
 */
export const StatGlyph = memo(function StatGlyph({
  glyph,
  tone,
  className = '',
  size = 14,
}: {
  glyph: string
  tone?: PillTone
  className?: string
  size?: number
}): React.JSX.Element | null {
  const url = resolveGlyphUrl(glyph)
  if (!url) return null

  return (
    <span
      className={`stat-glyph ${tone ? `pill-tone--${tone}` : ''} ${className}`.trim()}
      style={{
        display: 'inline-block',
        width: size,
        height: size,
        maskImage: `url(${url})`,
        WebkitMaskImage: `url(${url})`,
        maskSize: 'contain',
        WebkitMaskSize: 'contain',
        maskRepeat: 'no-repeat',
        WebkitMaskRepeat: 'no-repeat',
        maskPosition: 'center',
        WebkitMaskPosition: 'center',
        backgroundColor: 'currentColor',
        verticalAlign: '-0.15em',
        flexShrink: 0,
      }}
      aria-hidden="true"
    />
  )
})
