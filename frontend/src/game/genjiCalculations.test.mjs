import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

const tempOut = path.join(tmpdir(), `daohua-genji-test-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/genjiCalculations.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
})
const {
  calculateGenjiCap,
  resolveGenjiBonusPoints,
  resolveGenjiRelativeMultiplier,
  resolveGenjiCondenseCost,
  isGenjiUnlocked,
  shouldShowCondenseGenjiForBreakthrough,
} = await import(pathToFileURL(tempOut).href)

test.after(() => {
  try { fs.unlinkSync(tempOut) } catch { /* 忽略清理异常 */ }
})

test('根基上限表与服务端档位覆盖规则逐位对齐', () => {
  assert.equal(calculateGenjiCap(29), 0)
  assert.equal(calculateGenjiCap(30), 30)
  assert.equal(calculateGenjiCap(31), 50)
  assert.equal(calculateGenjiCap(34), 66)
  assert.equal(calculateGenjiCap(40), 98)
  assert.equal(calculateGenjiCap(42), 106)
  assert.equal(calculateGenjiCap(43), 126)
  assert.equal(calculateGenjiCap(126), 638)
  assert.equal(calculateGenjiCap(127), 658)
})

test('根基六维加成向上取整与筑基 99 点门槛引导判定', () => {
  assert.equal(resolveGenjiBonusPoints(90, 0), 0)
  assert.equal(resolveGenjiBonusPoints(90, 10), 9)
  assert.equal(resolveGenjiBonusPoints(90, 11), 10)
  assert.equal(resolveGenjiBonusPoints(96, 33), 32)

  assert.equal(isGenjiUnlocked(29, false, 0, true), false)
  assert.equal(isGenjiUnlocked(30, false, 0, false), false)
  assert.equal(isGenjiUnlocked(30, false, 0, true), true)
  assert.equal(isGenjiUnlocked(30, true, 1, false), true)
  assert.equal(isGenjiUnlocked(31, false, 0, false), true)

  assert.equal(shouldShowCondenseGenjiForBreakthrough(30, 98, true), true)
  assert.equal(shouldShowCondenseGenjiForBreakthrough(30, 99, true), false)
  assert.equal(shouldShowCondenseGenjiForBreakthrough(31, 50, true), false)
})

test('根基消耗曲线前 50% 降费与后 50% 对称守恒一致', () => {
  const cap = 30
  assert.equal(resolveGenjiRelativeMultiplier(0, cap), 0.2)
  assert.equal(resolveGenjiRelativeMultiplier(15, cap), 1.0)

  let sum = 0
  for (let k = 0; k < cap; k++) {
    const m = resolveGenjiRelativeMultiplier(k, cap)
    assert.ok(m > 0 && m < 2.0)
    sum += m
  }
  assert.ok(Math.abs(sum - cap) < 1e-9)

  assert.equal(resolveGenjiCondenseCost(100000, 0, 30), 10000)
  assert.equal(resolveGenjiCondenseCost(100000, 15, 30), 50000)
})
