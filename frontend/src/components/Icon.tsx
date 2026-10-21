import { memo } from 'react'
import type { ActionType } from '../game/types'

/** 全站矢量图标名称：页签、行动类型与若干功能入口共用一套描边风格 */
export type IconName = 'insight' | ActionType | 'crafting' | 'forging' | 'talisman' | 'formation' | 'map' | 'market' | 'shop' | 'social' | 'inventory' | 'log' | 'settings' | 'spirit' | 'loop' | 'travel' | 'codex' | 'more' | 'records'

/**
 * 矢量图标组件（按名称渲染内联 SVG）
 */
export const Icon = memo(function Icon({ name }: { name: IconName }): React.JSX.Element {
  const common = {
    fill: 'none',
    stroke: 'currentColor',
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
    strokeWidth: 1.7,
    viewBox: '0 0 24 24',
  }

  switch (name) {
    case 'insight':
      return <svg {...common}><circle cx="12" cy="12" r="9" /><circle cx="12" cy="12" r="3.5" /><path d="M12 3v3M12 18v3M3 12h3M18 12h3" /></svg>
    case 'cultivation':
      return <svg {...common}><path d="M12 4.5c2 2.4 2 6.2 0 8.6-2-2.4-2-6.2 0-8.6Z" /><path d="M4.8 8.8c3 .3 5.4 2.4 6.1 5.4-3.2-.2-5.7-2.3-6.1-5.4Z" /><path d="M19.2 8.8c-3 .3-5.4 2.4-6.1 5.4 3.2-.2 5.7-2.3 6.1-5.4Z" /><path d="M3.5 15.5c2.6 2.8 5.4 4 8.5 4s5.9-1.2 8.5-4" /></svg>
    case 'exploration':
      return <svg {...common}><circle cx="17.5" cy="6" r="2.2" /><path d="M3 19 9.5 7l4.5 7 2.5-3.5L21 19H3Z" /></svg>
    case 'alchemy':
      return <svg {...common}><path d="M8.5 10V7.5M15.5 10V7.5" /><path d="M5.5 10.5h13v3.5a6.5 6.5 0 0 1-13 0v-3.5Z" /><path d="M9 21v-2M15 21v-2" /></svg>
    case 'crafting':
      return <svg {...common}><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76Z" /></svg>
    case 'forging':
      return <svg {...common}><path d="M15 12l-8.5 8.5a2.12 2.12 0 1 1-3-3L12 9" /><path d="M17.64 4.36a3 3 0 0 0-4.24 0L12 5.76l4.24 4.24 1.4-1.4a3 3 0 0 0 0-4.24Z" /><path d="m14 7 3 3" /></svg>
    case 'talisman':
      return <svg {...common}><path d="M7 3h10v18l-2.5-1.8L12 21l-2.5-1.8L7 21V3Z" /><path d="m10 7 4 4-4 4" /></svg>
    case 'formation':
      return <svg {...common}><circle cx="12" cy="12" r="8" /><path d="M12 4v3M12 17v3M4 12h3M17 12h3" /><path d="M12 8.5 15.5 12 12 15.5 8.5 12 12 8.5Z" /></svg>
    case 'enhancement':
      return <svg {...common}><path d="m12 3 6 6h-3.5v6h-5V9H6l6-6Z" /><path d="M5 20h14" /></svg>
    case 'map':
      return <svg {...common}><path d="M3 6l6-3 6 3 6-3v15l-6 3-6-3-6 3V6Z" /><path d="M9 3v15M15 6v15" /></svg>
    case 'combat':
      return <svg {...common}><path d="m5 4 15 15M19 4 4 19" /><path d="m7 7 3-3 3 3M17 17l-3 3-3-3" /></svg>
    case 'market':
      return <svg {...common}><path d="M4 8.5h16M6 8.5V20h12V8.5" /><path d="m5 4h14l1 4.5H4L5 4Z" /><path d="M9 12h6M9 15.5h6" /></svg>
    case 'shop':
      return <svg {...common}><path d="M4 9.5h16M5.5 9.5V20h13V9.5" /><path d="M3.8 9.5 5.6 4h12.8l1.8 5.5" /><path d="M3.8 9.5c0 1.4 1 2.4 2.2 2.4s2.2-1 2.2-2.4c0 1.4 1 2.4 2.2 2.4s2.2-1 2.2-2.4c0 1.4 1 2.4 2.2 2.4s2.2-1 2.2-2.4c0 1.4 1 2.4 2.2 2.4s2.2-1 2.2-2.4" /><path d="M9.8 20v-4.5h4.4V20" /></svg>
    case 'social':
      return <svg {...common}><circle cx="8.5" cy="8" r="3.2" /><circle cx="16.5" cy="9.5" r="2.6" /><path d="M3.5 19.5c.6-3.4 2.6-5.4 5-5.4s4.4 2 5 5.4M13.5 19.5c.4-2.6 1.6-4.2 3.2-4.2 1.5 0 2.7 1.4 3.3 3.6" /></svg>
    case 'inventory':
      return <svg {...common}><path d="M9 7c0-1.8 1.2-2.8 3-2.8s3 1 3 2.8" /><path d="M6.5 9.5C5.5 14.5 7.5 20 12 20s6.5-5.5 5.5-10.5c-1.7 1-3.6 1.5-5.5 1.5s-3.8-.5-5.5-1.5Z" /><path d="M9.5 13.5h5" /></svg>
    case 'log':
      return <svg {...common}><path d="M8 4.5h9.5a2 2 0 0 1 2 2V18a2 2 0 0 1-2 2H8" /><path d="M8 4.5a2 2 0 0 0-2 2V18a2 2 0 0 0 2 2" /><path d="M11 9.5h5M11 13.5h5" /></svg>
    case 'records':
      return <svg {...common}><rect x="4" y="3.5" width="16" height="17" rx="1.5" /><path d="M8 16.5v-4M12 16.5V8M16 16.5v-6" /></svg>
    case 'settings':
      return <svg {...common}><circle cx="12" cy="12" r="3.2" /><path d="M12 3v2.6M12 18.4V21M3 12h2.6M18.4 12H21M5.6 5.6l1.9 1.9M16.5 16.5l1.9 1.9M18.4 5.6l-1.9 1.9M7.5 16.5l-1.9 1.9" /></svg>
    case 'spirit':
      return <svg {...common}><path d="M12 2.5 18.5 8 12 21.5 5.5 8 12 2.5Z" /><path d="M5.5 8h13M12 2.5 9.5 8 12 21.5" /></svg>
    case 'loop':
      return <svg {...common}><path d="M5.5 12.5a6.5 6.5 0 0 1 11.5-4.2" /><path d="M17.5 3.5v4h-4" /><path d="M18.5 11.5a6.5 6.5 0 0 1-11.5 4.2" /><path d="M6.5 20.5v-4h4" /></svg>
    case 'travel':
      return <svg {...common}><circle cx="5" cy="18.5" r="1.8" /><path d="M7.8 18.5h4.7a4 4 0 0 0 4-4v-2a3 3 0 0 1 3-3" /><path d="m16.5 6.5 3 3-3 3" /></svg>
    case 'codex':
      return <svg {...common}><path d="M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H20v20H6.5a2.5 2.5 0 0 1-2.5-2.5Z" /><path d="M6 6h10" /><path d="M6 10h10" /><path d="M6 14h6" /></svg>
    case 'more':
      return <svg {...common}><rect x="4" y="4" width="6" height="6" rx="1" /><rect x="14" y="4" width="6" height="6" rx="1" /><rect x="4" y="14" width="6" height="6" rx="1" /><rect x="14" y="14" width="6" height="6" rx="1" /></svg>
  }
})
