import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import JavaScriptObfuscator from 'javascript-obfuscator'

/** 只保护采集和标签互斥代码，避免对游戏高频逻辑引入混淆开销。 */
function accountSignalObfuscation(): Plugin {
  return {
    name: 'daohua-account-signal-obfuscation',
    apply: 'build',
    enforce: 'post',
    transform(code, id) {
      if (!id.replace(/\\/g, '/').includes('/src/antiMulti/') || id.endsWith('.tsx')) return null
      const result = JavaScriptObfuscator.obfuscate(code, {
        compact: true,
        target: 'browser',
        controlFlowFlattening: true,
        controlFlowFlatteningThreshold: 0.35,
        stringArray: true,
        stringArrayEncoding: ['rc4'],
        stringArrayThreshold: 1,
        renameGlobals: false,
        selfDefending: false,
        debugProtection: false,
        sourceMap: false,
      })
      return { code: result.getObfuscatedCode(), map: null }
    },
  }
}

export default defineConfig({
  plugins: [react(), accountSignalObfuscation()],
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
      // 运维面板数据端点并入主端口；面板页面本身（/ops 或 /ops-<hex>）由本 dev server 的 SPA 兜底提供，
      // 因此这里只代理 /ops(-*)?/api 前缀，避免把页面请求也转给后端。
      '^/ops(-[a-zA-Z0-9_-]+)?/api': 'http://127.0.0.1:5128',
      '/ws': {
        target: 'ws://127.0.0.1:5128',
        ws: true,
      },
    },
  },
  build: {
    sourcemap: false,
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
