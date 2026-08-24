import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  optimizeDeps: {
    include: ['react', 'react-dom', 'react-dom/client', 'react/jsx-runtime', 'react/jsx-dev-runtime', 'zustand'],
    ignoreOutdatedRequests: true,
  },
  server: {
    host: '127.0.0.1',
    port: 15172,
    strictPort: true,
    proxy: {
      '/api': 'http://127.0.0.1:5128',
      '/ws': {
        target: 'ws://127.0.0.1:5128',
        ws: true,
      },
    },
  },
  build: {
    rollupOptions: {
      output: {
        manualChunks: {
          'vendor-react': ['react', 'react-dom', 'react-dom/client'],
          'vendor-store': ['zustand'],
        },
      },
    },
  },
})
