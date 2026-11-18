import assert from 'node:assert/strict'
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import test from 'node:test'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { build } from 'vite'

const entry = fileURLToPath(new URL('./index.ts', import.meta.url)).replaceAll('\\', '/')

test('原生 Node 可直接导入内置中文词典，不依赖 Vite 转换', async () => {
  const { LOCALES, getT } = await import('./index.ts')
  assert.deepEqual(LOCALES, ['zh-CN'])
  assert.equal(getT()('common.confirm'), '确认')
})

test('Vite 生产编译仅注册实际语言包，首次选择才加载并缓存翻译', async t => {
  // Glob 展开为相对导入；测试工程和源码在 Windows 上也须处于同一盘符。
  const root = await mkdtemp(fileURLToPath(new URL('../../node_modules/.daohua-i18n-', import.meta.url)))
  t.after(() => rm(root, { recursive: true, force: true }))
  const locales = join(root, 'public', 'locales')
  const englishCsv = 'key,zh_source,translation\ncommon.confirm,确认,Confirm\n'
  await mkdir(locales, { recursive: true })
  await Promise.all([
    writeFile(join(locales, 'template.csv'), 'key,zh_source,translation\n'),
    writeFile(join(locales, 'en-US.csv'), englishCsv),
    writeFile(join(root, 'entry.js'), `export * from ${JSON.stringify(entry)}`),
  ])
  await build({
    root,
    configFile: false,
    envFile: false,
    logLevel: 'silent',
    build: { lib: { entry: 'entry.js', formats: ['es'], fileName: () => 'i18n.mjs' } },
  })

  const fetch = t.mock.method(globalThis, 'fetch', async url => {
    assert.equal(url, '/locales/en-US.csv')
    return new Response(englishCsv)
  })
  const { LOCALES, useI18nStore, getT } = await import(pathToFileURL(join(root, 'dist', 'i18n.mjs')).href)
  assert.deepEqual(LOCALES, ['zh-CN', 'en-US'])
  const { setLocale } = useI18nStore.getState()
  await setLocale('template')
  await setLocale('fr-FR')
  assert.equal(useI18nStore.getState().locale, 'zh-CN')
  assert.equal(fetch.mock.callCount(), 0)

  const loading = setLocale('en-US')
  assert.equal(getT()('common.confirm'), '确认', '加载期间应保留中文兜底')
  await loading
  assert.equal(useI18nStore.getState().loadedLocale, 'en-US')
  assert.equal(getT()('common.confirm'), 'Confirm')
  assert.equal(getT()('common.cancel'), '取消', '外语缺键仍回退中文')
  await setLocale('zh-CN')
  assert.equal(getT()('common.confirm'), '确认')
  await setLocale('en-US')
  assert.equal(fetch.mock.callCount(), 1)
})
