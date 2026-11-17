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
    if (file === 'node_modules' || file === 'dist' || file === 'private-impl') continue
    const full = path.join(dir, file)
    let stat
    try {
      stat = statSync(full)
    } catch {
      continue
    }
    if (stat.isDirectory()) {
      results.push(...findTsxFiles(full))
    } else if (file.endsWith('.tsx') || file.endsWith('.ts')) {
      results.push(full)
    }
  }
  return results
}

const isFunction = (node) => ts.isFunctionDeclaration(node) || ts.isFunctionExpression(node) || ts.isArrowFunction(node)

function hookName(node) {
  if (!ts.isCallExpression(node)) return null
  const callee = node.expression
  const name = ts.isIdentifier(callee) ? callee.text : ts.isPropertyAccessExpression(callee) ? callee.name.text : ''
  return /^use[A-Z]/.test(name) ? name : null
}

function conditionalContext(call) {
  for (let node = call, parent = node.parent; parent && !ts.isFunctionLike(parent); node = parent, parent = node.parent) {
    if (ts.isIterationStatement(parent, false)) return '循环中'
    if ((ts.isIfStatement(parent) && parent.expression !== node)
      || (ts.isConditionalExpression(parent) && parent.condition !== node)) return '条件分支中'
    if (ts.isCaseClause(parent) || ts.isDefaultClause(parent) || ts.isCatchClause(parent)) return '条件分支中'
    if (ts.isBinaryExpression(parent) && parent.right === node && [
      ts.SyntaxKind.AmpersandAmpersandToken, ts.SyntaxKind.BarBarToken, ts.SyntaxKind.QuestionQuestionToken,
      ts.SyntaxKind.AmpersandAmpersandEqualsToken, ts.SyntaxKind.BarBarEqualsToken, ts.SyntaxKind.QuestionQuestionEqualsToken,
    ].includes(parent.operatorToken.kind)) return '短路表达式中'
    if (ts.isCallExpression(parent) && ts.isOptionalChain(parent) && node !== parent.expression) return '可选调用参数中'
  }
  return null
}

function inspectHooks(file, content) {
  const sf = ts.createSourceFile(file, content, ts.ScriptTarget.Latest, true)
  const violations = []
  const line = (node) => sf.getLineAndCharacterOfPosition(node.getStart()).line + 1
  const visit = (node) => {
    const name = hookName(node)
    if (name) {
      const context = conditionalContext(node)
      if (context) violations.push(`${file}:${line(node)} 调用 Hook '${name}' 发生在${context}`)
    }
    // 保留已有前置 return 守卫，不把内层函数自己的 return 算进外层。
    if (isFunction(node) && node.body && ts.isBlock(node.body)) {
      let returnLine = 0
      for (const statement of node.body.statements) {
        const inspectStatement = (child) => {
          if (isFunction(child)) return
          const hook = hookName(child)
          if (hook && returnLine) {
            violations.push(`${file}:${line(child)} 调用 Hook '${hook}' 发生在第 ${returnLine} 行条件返回之后`)
          }
          ts.forEachChild(child, inspectStatement)
        }
        const inspectReturn = (child) => {
          if (isFunction(child)) return
          if (ts.isReturnStatement(child)) returnLine = line(child)
          ts.forEachChild(child, inspectReturn)
        }
        inspectStatement(statement)
        inspectReturn(statement)
      }
    }
    ts.forEachChild(node, visit)
  }
  visit(sf)
  return violations
}

test('React Hooks 静态守卫能区分条件、循环、前置返回和正常重构', () => {
  const inspect = (source) => inspectHooks('fixture.tsx', source)
  assert.match(inspect('function Card() { if (show) useState(0) }').join(), /条件分支/)
  assert.match(inspect('const Card = () => show ? React.useState(0) : null').join(), /条件分支/)
  assert.match(inspect('function Card() { show && useState(0) }').join(), /短路表达式/)
  assert.match(inspect('function Card() { for (const item of items) { useState(item) } }').join(), /循环/)
  assert.match(inspect('function Card() { if (show) return null; const state = useState(0) }').join(), /条件返回/)
  assert.deepEqual(inspect('function Card() { const state = useState(0); if (!show) return null; return <Item state={state} /> }'), [])
  assert.deepEqual(inspect('function Card() { if (React.useId()) return null; }'), [])
})

test('React Rules of Hooks：静态门禁禁止条件、循环和前置返回后的 Hook 调用', () => {
  const srcDir = fileURLToPath(new URL('../', import.meta.url))
  const violations = findTsxFiles(srcDir).flatMap((file) => inspectHooks(path.relative(srcDir, file), readFileSync(file, 'utf8')))
  assert.equal(violations.length, 0, `发现 React Rules of Hooks 违规：\n${violations.join('\n')}`)
})
