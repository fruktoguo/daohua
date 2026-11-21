import { register } from 'node:module'

if (!globalThis.__daohuaTsHookRegistered) {
  globalThis.__daohuaTsHookRegistered = true
  globalThis.__DAOHUA_TEST_ENV__ ??= {
    DEV: true,
    VITE_APP_VERSION: 'dev',
  }
  register('./test-hook.mjs', import.meta.url)
}
