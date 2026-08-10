import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'

// 把 JS/CSS 全部内联进 dist/index.html，产出单个文件：
// 直接双击（file:// 协议）即可游玩，无需启动任何服务器。
// 注意：file:// 下外部 ES module 会被 CORS 拦截，内联 <script type="module"> 则不受限。
function inlineSingleFile(): Plugin {
  return {
    name: 'inline-single-file',
    apply: 'build',
    enforce: 'post',
    generateBundle(_, bundle) {
      const htmlEntry = bundle['index.html']
      if (!htmlEntry || htmlEntry.type !== 'asset') return
      let html = String(htmlEntry.source)

      for (const [fileName, item] of Object.entries(bundle)) {
        if (item.type === 'asset' && fileName.endsWith('.css')) {
          html = html.replace(
            /<link[^>]*rel="stylesheet"[^>]*>/,
            () => `<style>\n${String(item.source)}\n</style>`
          )
          delete bundle[fileName]
        }
        if (item.type === 'chunk' && fileName.endsWith('.js')) {
          // 防止 bundle 内字符串意外闭合 <script>
          const code = item.code.replace(/<\/script/g, '<\\/script')
          html = html.replace(
            /<script[^>]*type="module"[^>]*><\/script>/,
            () => `<script type="module">\n${code}\n</script>`
          )
          delete bundle[fileName]
        }
      }
      htmlEntry.source = html
    }
  }
}

export default defineConfig({
  plugins: [react(), inlineSingleFile()],
  base: './',
  build: {
    cssCodeSplit: false,
    assetsInlineLimit: 100 * 1024 * 1024,
    rollupOptions: {
      output: {
        inlineDynamicImports: true
      }
    }
  },
  server: {
    host: true,
    port: 5174
  }
})
