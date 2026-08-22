/**
 * 道化太初 () 前端应用入口脚本
 * 负责挂载 React 根节点并引入全局与组件样式表
 */
import { createRoot } from 'react-dom/client'
import App from './App'
import './styles.css'
import './styles/selection-grid.css'
import './styles/expedition.css'
import './styles/floating-popover.css'

const root = document.getElementById('root')
if (!root) throw new Error('找不到应用挂载点。')

createRoot(root).render(<App />)
