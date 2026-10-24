#!/usr/bin/env node
// 日志富文本标记转义门禁。
//
// 服务端日志/传音模板用双花括号引用标记 {{kind:id|label}}（见 SimulationLogMapper 与
// frontend/src/logging/gameLogService.ts）。在 C# 插值字符串里 {{ / }} 是转义语法，
// 一旦直接写 {{mechanic:dodge|闪避}}，编译后只剩单花括号，前端正则不匹配，
// 玩家会在战斗日志里看到原文 `{mechanic:dodge|闪避}`。这类错误编译器不报、测试难覆盖，
// 故在此做源码级扫描：插值字符串内出现单层标记一律判定为缺陷。
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = fileURLToPath(new URL('..', import.meta.url)).replace(/[\\/]$/, '')
const KINDS = 'item|character|mechanic|action|location|ability|encounter|enemy'
/** 只匹配「恰好两个左花括号」：{{kind:（排除已正确转义的 {{{{kind:） */
const BAD_MARKER = new RegExp(`(?<!\\{)\\{\\{(?!\\{)(${KINDS}):`, 'g')
const SCAN_DIRS = ['src', 'tests', 'tools']

function walk(dir, out = []) {
  let entries
  try {
    entries = readdirSync(dir)
  } catch {
    return out
  }
  for (const name of entries) {
    if (name === 'bin' || name === 'obj' || name === 'node_modules' || name === '.git') continue
    const path = join(dir, name)
    const stat = statSync(path)
    if (stat.isDirectory()) walk(path, out)
    else if (path.endsWith('.cs')) out.push(path)
  }
  return out
}

const failures = []
for (const dir of SCAN_DIRS) {
  for (const file of walk(join(ROOT, dir))) {
    readFileSync(file, 'utf8').split('\n').forEach((line, index) => {
      // 插值字符串起点：$" 或 $@"（逐字插值）。普通字符串里的 {{kind: 是合法写法。
      const verbatim = line.indexOf('$@"')
      const plain = line.indexOf('$"')
      const start = verbatim >= 0 ? verbatim : plain
      if (start < 0) return
      BAD_MARKER.lastIndex = 0
      let match
      while ((match = BAD_MARKER.exec(line))) {
        if (match.index <= start) continue
        failures.push(
          `${file.slice(ROOT.length + 1).replace(/\\/g, '/')}:${index + 1} 插值字符串内的 ${match[0]} 会退化为单花括号，前端无法解析`,
        )
      }
    })
  }
}

if (failures.length === 0) {
  console.log('日志富文本标记转义校验通过：未发现插值字符串里的单层引用标记。')
  process.exit(0)
}
console.error(`日志富文本标记转义校验失败：共 ${failures.length} 处。正确写法是在插值字符串中使用 {{{{kind:id|label}}}}。`)
for (const failure of failures) console.error(`  - ${failure}`)
process.exit(1)
