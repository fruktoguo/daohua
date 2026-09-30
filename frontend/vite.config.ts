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
    fs: {
      // 战斗特效真源在 docs/design/战斗演武画廊/vfx/（frontend 之外），
      // dev server 需放开到仓库根目录才能让 import.meta.glob 引入共享脚本。
      allow: ['..'],
    },
    proxy: {
      '/api': 'http://127.0.0.1:5128',
      // 运维面板数据端点并入主端口；面板页面本身（/ops）由本 dev server 的 SPA 兜底提供，
      // 因此这里只代理 /ops/api 前缀，避免把页面请求也转给后端。
      '/ops/api': 'http://127.0.0.1:5128',
      '/ws': {
        target: 'ws://127.0.0.1:5128',
        ws: true,
      },
    },
  },
  build: {
    // 图标注册表（game/itemIcons.ts）在构建期 eager 引用 assets/items 下的每一张图。
    // 默认 4KB 以下内联成 base64，会让入口 JS 随图标数量线性膨胀
    // （实测 64 张 64px WebP 约 240KB base64，几乎全部塞进 index.js）。
    // 因此图标一律产出独立文件，由浏览器按 URL 缓存；其余资源保持 Vite 默认行为。
    assetsInlineLimit: (filePath: string) =>
      filePath.replace(/\\/g, '/').includes('/assets/items/') ? false : undefined,
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
