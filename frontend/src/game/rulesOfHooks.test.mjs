import test from 'node:test'
import assert from 'node:assert/strict'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { createRequire } from 'node:module'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url)
const tsPath = fileURLToPath(new URL('../../node_modules/typescript/lib/typescript.js', import.meta.url))
const ts = require(tsPath)

function findTsxFiles(dir) {
  const results = []
  for (const file of readdirSync(dir)) {
    const full = path.join(dir, file)
    const stat = statSync(full)
    if (stat.isDirectory()) {
      results.push(...findTsxFiles(full))
    } else if (file.endsWith('.tsx') || file.endsWith('.ts')) {
      results.push(full)
    }
  }
  return results
}

test('React Rules of Hooks：静态代码门禁，严禁在条件返回之后调用任何 Hook（防止切换物品等交互触发 Minified React error #300）', () => {
  const srcDir = fileURLToPath(new URL('../', import.meta.url))
  const files = findTsxFiles(srcDir)
  const violations = []

  for (const file of files) {
    const content = readFileSync(file, 'utf8')
    const sf = ts.createSourceFile(file, content, ts.ScriptTarget.Latest, true)

    function inspectNode(node) {
      if (
        ts.isFunctionDeclaration(node) ||
        ts.isFunctionExpression(node) ||
        ts.isArrowFunction(node)
      ) {
        if (node.body && ts.isBlock(node.body)) {
          let returnSeen = false
          let returnLine = 0
          for (const stmt of node.body.statements) {
            let hasHook = false
            let hookName = ''
            let hookLine = 0

            function findHook(n) {
              if (
                n !== stmt &&
                (ts.isFunctionDeclaration(n) ||
                  ts.isFunctionExpression(n) ||
                  ts.isArrowFunction(n))
              ) {
                return
              }
              if (ts.isCallExpression(n)) {
                let name = ''
                if (ts.isIdentifier(n.expression)) {
                  name = n.expression.text
                } else if (ts.isPropertyAccessExpression(n.expression) && ts.isIdentifier(n.expression.name)) {
                  name = n.expression.name.text
                }
                if (/^use[A-Z]/.test(name)) {
                  hasHook = true
                  hookName = name
                  hookLine = sf.getLineAndCharacterOfPosition(n.getStart()).line + 1
                }
              }
              ts.forEachChild(n, findHook)
            }
            findHook(stmt)

            if (returnSeen && hasHook) {
              violations.push(`${path.relative(srcDir, file)}:${hookLine} 调用 Hook '${hookName}' 发生在第 ${returnLine} 行条件返回之后，违反 React Rules of Hooks`)
            }

            function findReturn(n) {
              if (
                n !== stmt &&
                (ts.isFunctionDeclaration(n) ||
                  ts.isFunctionExpression(n) ||
                  ts.isArrowFunction(n))
              ) {
                return
              }
              if (ts.isReturnStatement(n)) {
                returnSeen = true
                returnLine = sf.getLineAndCharacterOfPosition(n.getStart()).line + 1
              }
              ts.forEachChild(n, findReturn)
            }
            findReturn(stmt)
          }
        }
      }
      ts.forEachChild(node, inspectNode)
    }

    inspectNode(sf)
  }

  assert.equal(
    violations.length,
    0,
    `发现 React Rules of Hooks 违规（条件返回后调用 Hook）：\n${violations.join('\n')}`
  )
})
