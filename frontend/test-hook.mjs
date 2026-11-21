import { existsSync, readFileSync, statSync } from 'node:fs'
import { createRequire } from 'node:module'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const require = createRequire(import.meta.url)
const esbuild = require('esbuild')

const ASSET_RE = /\.(css|webp|png|jpe?g|svg)$/i
const EXT_CANDIDATES = ['.ts', '.tsx', '.mts', '.mjs', '.js']

function resolveCandidate(parentURL, specifier) {
  if (!parentURL?.startsWith('file:')) return null
  const parentDir = path.dirname(fileURLToPath(parentURL))
  const base = path.resolve(parentDir, specifier)
  if (existsSync(base) && statSync(base).isFile()) return pathToFileURL(base).href
  for (const ext of EXT_CANDIDATES) {
    const file = `${base}${ext}`
    if (existsSync(file) && statSync(file).isFile()) return pathToFileURL(file).href
  }
  for (const ext of EXT_CANDIDATES) {
    const indexFile = path.join(base, `index${ext}`)
    if (existsSync(indexFile) && statSync(indexFile).isFile()) return pathToFileURL(indexFile).href
  }
  return null
}

export async function resolve(specifier, context, nextResolve) {
  if (specifier.startsWith('./') || specifier.startsWith('../')) {
    const resolved = resolveCandidate(context.parentURL, specifier)
    if (resolved) return { url: resolved, shortCircuit: true }
  }
  return nextResolve(specifier, context)
}

export async function load(url, context, nextLoad) {
  if (url.startsWith('file:') && !url.includes('/node_modules/')) {
    if (ASSET_RE.test(url)) {
      return { format: 'module', shortCircuit: true, source: 'export default ""' }
    }
    if (url.endsWith('.ts') || url.endsWith('.tsx')) {
      const filePath = fileURLToPath(url)
      const raw = readFileSync(filePath, 'utf8')
      const { code } = esbuild.transformSync(raw, {
        loader: url.endsWith('.tsx') ? 'tsx' : 'ts',
        format: 'esm',
        jsx: 'automatic',
        sourcefile: filePath,
        define: {
          'import.meta.env': 'globalThis.__DAOHUA_TEST_ENV__',
        },
      })
      return { format: 'module', shortCircuit: true, source: code }
    }
  }
  return nextLoad(url, context)
}
