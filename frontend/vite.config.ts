import { existsSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import JavaScriptObfuscator from 'javascript-obfuscator'

// 私有客户端模块挂载点（scripts/sync-private-modules.mjs 以 junction/symlink 挂载）：
// 存在即完整版构建（impl 重定向到私有实现入口），缺失即开源版（impl 固定为 stub）。
const frontendDir = fileURLToPath(new URL('.', import.meta.url))
const privateImplEntry = resolve(frontendDir, 'src/private-impl/index.ts')
const privateModulesAvailable = existsSync(privateImplEntry)

/** 只保护采集和标签互斥代码，避免对游戏高频逻辑引入混淆开销。 */
function accountSignalObfuscation(): Plugin {
  return {
    name: 'daohua-account-signal-obfuscation',
    apply: 'build',
    enforce: 'post',
    transform(code, id) {
      if (!id.replace(/\\/g, '/').includes('/src/private-impl/') || id.endsWith('.tsx')) return null
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
  // 完整版构建：把 src/private/impl.ts（stub 转发）重定向到私有实现入口。
  // preserveSymlinks 保留 junction 挂载视图：私有文件的裸导入（react/zustand）沿挂载位置
  // 向上解析到本仓库 node_modules，而不是 rollup realpath 后的私有仓库物理路径。
  // 字符串 find 为全仓唯一说明符（src/private/index.ts 的 re-export './impl'），
  // replacement 用正斜杠绝对路径以兼容 rollup 在 Windows 下的 id 规范化。
  resolve: privateModulesAvailable
    ? {
        preserveSymlinks: true,
        alias: [{ find: './impl', replacement: privateImplEntry.replace(/\\/g, '/') }],
      }
    : undefined,
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
