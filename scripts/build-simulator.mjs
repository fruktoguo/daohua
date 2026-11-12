#!/usr/bin/env node
// 构建浏览器 WASM 战斗预演负载并同步到前端静态目录。
// DOTNET 环境变量可显式指定 dotnet 可执行文件（默认 PATH 上的 dotnet）；
// FORCE_SIMULATOR_BUILD=1 可跳过指纹缓存强制重新发布。
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const dotnet = process.env.DOTNET || 'dotnet'
const project = resolve(root, 'src/Simulator.Browser/Simulator.Browser.csproj')
const configuration = process.env.CONFIGURATION || 'Release'
const publishDir = resolve(root, 'artifacts/simulator-publish')
const target = resolve(root, 'frontend/public/simulator')
const stampFile = resolve(target, '.simulator-build-stamp.json')

const WATCH_DIRS = [
  'src/Simulator',
  'src/Simulator.Browser',
  'src/Domain',
  'src/Ecs',
  'config/game-data',
]
const WATCH_FILES = [
  'global.json',
  'Directory.Build.props',
  'scripts/build-simulator.mjs',
]

function collectFiles(dir, out) {
  if (!existsSync(dir)) return
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'bin' || entry.name === 'obj' || entry.name === '.git') continue
    const full = resolve(dir, entry.name)
    if (entry.isDirectory()) {
      collectFiles(full, out)
    } else if (entry.isFile()) {
      out.push(full)
    }
  }
}

function computeInputFingerprint() {
  const files = []
  for (const dir of WATCH_DIRS) collectFiles(resolve(root, dir), files)
  for (const file of WATCH_FILES) {
    const full = resolve(root, file)
    if (existsSync(full)) files.push(full)
  }
  files.sort((a, b) => a.localeCompare(b))
  const hash = createHash('sha256')
  hash.update(`config=${configuration}\n`)
  for (const file of files) {
    hash.update(relative(root, file).replace(/\\/g, '/'))
    hash.update('\0')
    hash.update(readFileSync(file))
    hash.update('\0')
  }
  return hash.digest('hex')
}

const fingerprint = computeInputFingerprint()
const hasOutputs = existsSync(resolve(target, '_framework/dotnet.js')) && existsSync(resolve(target, 'main.js'))
if (process.env.FORCE_SIMULATOR_BUILD !== '1' && hasOutputs && existsSync(stampFile)) {
  try {
    const stamp = JSON.parse(readFileSync(stampFile, 'utf8'))
    if (stamp.fingerprint === fingerprint && stamp.configuration === configuration) {
      console.log(`WASM 预演负载未变更（指纹 ${fingerprint.slice(0, 12)}），跳过构建：${target}`)
      process.exit(0)
    }
  } catch {
    // 指纹文件损坏时自动回退完整发布
  }
}

const build = spawnSync(dotnet, ['publish', project, '-c', configuration, '-o', publishDir], {
  cwd: root,
  stdio: 'inherit',
  env: process.env
})
if (build.error) {
  console.error(`无法启动 dotnet（${dotnet}）：${build.error.message}`)
  process.exit(1)
}
if (build.status !== 0) process.exit(build.status ?? 1)

const wwwroot = resolve(publishDir, 'wwwroot')
if (!existsSync(wwwroot)) {
  console.error(`WASM 产物缺失：${wwwroot}`)
  process.exit(1)
}
mkdirSync(target, { recursive: true })
rmSync(resolve(target, '_framework'), { recursive: true, force: true })
cpSync(resolve(wwwroot, '_framework'), resolve(target, '_framework'), { recursive: true })
if (existsSync(resolve(wwwroot, 'main.js'))) cpSync(resolve(wwwroot, 'main.js'), resolve(target, 'main.js'))
writeFileSync(stampFile, JSON.stringify({ fingerprint, configuration, builtAt: new Date().toISOString() }, null, 2) + '\n')
console.log(`WASM 预演负载已同步：${target}`)
