/**
 * 道化太初前端应用入口脚本
 * 负责挂载 React 根节点并引入全局与组件样式表
 */
import { createRoot } from 'react-dom/client'
import App from './App'
import { ErrorBoundary } from './components/ErrorBoundary'
import './styles.css'
import './styles/selection-grid.css'
import './styles/expedition.css'
import './styles/floating-popover.css'
import './styles/vfx.css'
import './styles/theme.css'
import './styles/tutorial.css'
import './styles/codex.css'

const container = document.getElementById('root')
if (!container) throw new Error('找不到应用挂载点。')
const root = createRoot(container)

/**
 * 运维面板仅在开发构建下注册。
 * `import.meta.env.DEV` 在构建时被静态替换为 false，整个分支连同动态 import 一起被摇树，
 * 面板入口不会进入生产产物——避免生产环境 SPA 兜底把面板页面吐给线上访问者。
 */
const isOpsRoute = import.meta.env.DEV && /^\/ops(\/|$)/.test(window.location.pathname)

async function mount(): Promise<void> {
  if (isOpsRoute) {
    const { OpsApp } = await import('./ops/OpsApp')
    root.render(
      <ErrorBoundary>
        <OpsApp />
      </ErrorBoundary>,
    )
    return
  }

  root.render(
    <ErrorBoundary>
      <App />
    </ErrorBoundary>,
  )
}

void mount()
