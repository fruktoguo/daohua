import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { startEngine } from './game/store'
import './global.css'

startEngine()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>
)
