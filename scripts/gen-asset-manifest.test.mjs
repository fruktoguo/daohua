import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import test from 'node:test'

// 两份真实的单色 VP8L WebP，分别为 48×48 和 64×48；测试不需要图像工具。
const webp48 = Buffer.from('UklGRiIAAABXRUJQVlA4TBUAAAAvL8ALAAcQ0f/+B4CE8H+9GNH/1AMA', 'base64')
const webp64 = Buffer.from('UklGRiIAAABXRUJQVlA4TBUAAAAvP8ALAAcQ0f/+B4CE8H+9GNH/1A8A', 'base64')
const src = 'src/assets/items/item.test.webp'
const asset = 'assets/item.test-123.webp'

function fixture(t) {
  const repo = mkdtempSync(path.join(tmpdir(), 'daohua-asset-manifest-'))
  t.after(() => rmSync(repo, { recursive: true, force: true }))
  const script = path.join(repo, 'scripts', 'gen-asset-manifest.mjs')
  const root = path.join(repo, 'frontend')
  const dist = path.join(root, 'dist')
  const cwd = path.join(repo, 'unrelated cwd')
  const viteManifest = path.join(dist, '.vite', 'manifest.json')
  function write(file, content) {
    mkdirSync(path.dirname(file), { recursive: true })
    writeFileSync(file, content)
  }
  write(viteManifest, JSON.stringify({ [src]: { src, file: asset } }))
  write(path.join(dist, asset), webp48)
  write(path.join(cwd, src), webp48)
  write(path.join(root, src), webp48)
  mkdirSync(path.dirname(script), { recursive: true })
  copyFileSync(new URL('./gen-asset-manifest.mjs', import.meta.url), script)
  function run(dir, args = [], workingDir = cwd) {
    const result = spawnSync(process.execPath, [script, dir, ...args], { cwd: workingDir, encoding: 'utf8' })
    assert.ifError(result.error)
    assert.equal(result.signal, null)
    return result
  }
  return { repo, root, dist, cwd, viteManifest, run, write }
}

test('道具 WebP 门禁以脚本对应的 Vite 根定位，从其他 cwd 仍拦截违规', t => {
  const { repo, root, dist, cwd, run, write } = fixture(t)
  const out = path.join(dist, 'asset-manifest.json')
  write(path.join(root, src), webp64)
  const bad = run(dist)
  assert.equal(bad.status, 1, bad.stderr)
  assert.match(bad.stderr, /item\.test\.webp \(64x48\)/)
  assert.equal(existsSync(out), false)

  write(path.join(root, src), webp48)
  write(path.join(cwd, src), webp64)
  for (const [dir, workingDir] of [[dist, cwd], ['frontend/dist', repo], ['dist', root]]) {
    const good = run(dir, [], workingDir)
    assert.equal(good.status, 0, good.stderr)
    assert.deepEqual(JSON.parse(readFileSync(out, 'utf8')).hdAliases, { [`/${asset}`]: '/hd/items/item.test.webp' })
  }
})

test('源图标缺失、尺寸头截断、非 WebP 均使清单生成失败', t => {
  const { root, dist, run, write } = fixture(t)
  const sourceFile = path.join(root, src)
  rmSync(sourceFile)
  for (const [input, error] of [
    [null, /item\.test\.webp.*无法读取源文件/],
    [webp48.subarray(0, 30), /item\.test\.webp.*无法解析 WebP 尺寸头/],
    ['非 WebP 内容', /item\.test\.webp.*无法解析 WebP 尺寸头/],
  ]) {
    if (input !== null) write(sourceFile, input)
    const result = run(dist)
    assert.equal(result.status, 1, result.stderr)
    assert.match(result.stderr, error)
    assert.equal(existsSync(path.join(dist, 'asset-manifest.json')), false)
  }
})

test('APK 使用显式 Vite 根和 manifest；manifest 缺失、坏 JSON 或坏条目不静默降级', t => {
  const { repo, dist, run, write } = fixture(t)
  const www = path.join(repo, 'android-shell', 'www')
  const out = path.join(repo, 'android-shell', 'www-manifest.json')
  const viteRoot = path.join(repo, 'custom-vite-root')
  const viteManifest = path.join(repo, 'vite-output', 'manifest.json')
  const args = ['--out', out, '--vite-root', viteRoot, '--vite-manifest', viteManifest]
  write(path.join(www, 'theme.js'), '{}')
  write(path.join(viteRoot, src), webp48)
  for (const [input, error] of [
    [null, /无法读取或解析 Vite manifest/],
    ['{', /无法读取或解析 Vite manifest/],
    [JSON.stringify({ [src]: {} }), /Vite manifest 条目格式错误/],
  ]) {
    if (input !== null) write(viteManifest, input)
    const result = run(www, args)
    assert.equal(result.status, 1, result.stderr)
    assert.match(result.stderr, error)
    assert.equal(existsSync(out), false)
  }
  write(viteManifest, JSON.stringify({ [src]: { file: asset } }))
  // 构建产物目录无需存在对应源图标：只在显式 Vite 根下查找。
  write(path.join(dist, src), webp64)
  const result = run(www, args)
  assert.equal(result.status, 0, result.stderr)
  const { files, hdAliases } = JSON.parse(readFileSync(out, 'utf8'))
  assert.deepEqual(Object.keys(files), ['/theme.js'])
  assert.deepEqual(hdAliases, { [`/${asset}`]: '/hd/items/item.test.webp' })
})

test('独立 HD 包无 manifest 可显式生成，否则缺失 Vite manifest 必须失败', t => {
  const { repo, run, write } = fixture(t)
  const pack = path.join(repo, 'hd-pack')
  write(path.join(pack, 'items', 'item.test.webp'), webp64)
  const missing = run(pack)
  assert.equal(missing.status, 1, missing.stderr)
  assert.match(missing.stderr, /无法读取或解析 Vite manifest/)
  const hd = run(pack, ['--prefix', '/hd', '--no-vite-manifest'])
  assert.equal(hd.status, 0, hd.stderr)
  const manifest = JSON.parse(readFileSync(path.join(pack, 'asset-manifest.json'), 'utf8'))
  assert.deepEqual(Object.keys(manifest.files), ['/hd/items/item.test.webp'])
  assert.equal(manifest.hdAliases, undefined)
})
