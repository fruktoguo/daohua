/**
 * 道化太初前端应用入口脚本
 * 负责挂载 React 根节点并引入全局与组件样式表
 */
import { createRoot } from 'react-dom/client'
import App from './App'
import { ErrorBoundary } from './components/ErrorBoundary'
import './styles.css'
import './styles/heaven-gate-artifact.css'
import './styles/selection-grid.css'
import './styles/expedition.css'
import './styles/floating-popover.css'
import './styles/vfx.css'
import './styles/theme.css'
import './styles/tutorial.css'
import './styles/codex.css'
import './styles/shop.css'
import './styles/simulator.css'
import { installChunkLoadErrorWatcher, startVersionWatcher } from './network/versionManager'
import { installInteractionGuard } from './network/interactionGuard'

installChunkLoadErrorWatcher()
startVersionWatcher()
installInteractionGuard()
const container = document.getElementById('root')
if (!container) throw new Error('找不到应用挂载点。')
const root = createRoot(container)

/**
 * 运维监控面板路由匹配：支持动态随机路径 `/ops-<hex>` 与本地开发 `/ops`。
 * 面板通过动态 `import('./ops/OpsApp')` 独立分包，普通玩家访问主页不加载面板代码；
 * 服务端对未命中当前激活随机路径的所有 `/ops*` 请求直接返回 404。
 */
const isOpsRoute = /^\/ops(-[a-zA-Z0-9_-]+)?(\/|$)/.test(window.location.pathname)

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
