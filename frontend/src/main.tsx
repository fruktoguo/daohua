import { createRoot } from 'react-dom/client'
import App from './App'
import './styles.css'
import './styles/selection-grid.css'
import './styles/expedition.css'

const root = document.getElementById('root')
if (!root) throw new Error('找不到应用挂载点。')

createRoot(root).render(<App />)
