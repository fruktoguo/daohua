#!/usr/bin/env node
// 构建浏览器 WASM 战斗预演负载并同步到前端静态目录。
// DOTNET 环境变量可显式指定 dotnet 可执行文件（默认 PATH 上的 dotnet）。
import { spawnSync } from 'node:child_process'
import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const dotnet = process.env.DOTNET || 'dotnet'
const project = resolve(root, 'src/Simulator.Browser/Simulator.Browser.csproj')
const configuration = process.env.CONFIGURATION || 'Release'
const publishDir = resolve(root, 'artifacts/simulator-publish')
const target = resolve(root, 'frontend/public/simulator')

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
console.log(`WASM 预演负载已同步：${target}`)
