import { useState, type CSSProperties } from 'react'
import { useT } from '../i18n'
import { useThemeStore } from '../game/themeStore'
import { FloatingTooltip } from './FloatingPopover'

/** 太极主题开关：对称正置双鱼太极，常态静止；深色主题整体翻转 180° 令阴鱼居上，点击再翻转半圈并以按钮为圆心做全屏扩散换色。 */
export function TaijiToggle(): React.JSX.Element {
  const t = useT()
  const theme = useThemeStore(state => state.theme)
  const toggleTheme = useThemeStore(state => state.toggleTheme)
  const [spin, setSpin] = useState(() => theme === 'dark' ? 180 : 0)
  const label = t(theme === 'dark' ? 'app.topbar.themeToLight' : 'app.topbar.themeToDark')

  return (
    <FloatingTooltip displayContents placement="bottom" title={label} tone="gold">
      <button
        aria-label={label}
        className="taiji-toggle"
        onClick={event => {
          const rect = event.currentTarget.getBoundingClientRect()
          setSpin(deg => deg + 180)
          toggleTheme({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 })
        }}
        type="button"
      >
        <span className="taiji-spin" style={{ '--taiji-spin': `${spin}deg` } as CSSProperties}>
          <svg aria-hidden="true" className="taiji-icon" viewBox="0 0 100 100">
            <circle className="taiji-yang-bg" cx="50" cy="50" r="50" />
            <path className="taiji-yin" d="M50,0 a50,50 0 0,1 0,100 a25,25 0 0,1 0,-50 a25,25 0 0,0 0,-50 z" />
            <circle className="taiji-yang" cx="50" cy="75" r="9" />
            <circle className="taiji-yin" cx="50" cy="25" r="9" />
          </svg>
        </span>
      </button>
    </FloatingTooltip>
  )
}
