import test from 'node:test'
import assert from 'node:assert/strict'
import { SNAPSHOT_SCHEMA_VERSION, snapshotSchemaReadable } from './snapshotSchemaGate.ts'

/**
 * 快照 schema 门禁回归：协议按「读者地板 minReaderSchemaVersion」判定可读性——
 * 服务端声明「仍可解码的最老客户端版本」，客户端接受一切地板不高于自身版本的载荷。
 * additive 升级不再锁定旧客户端；破坏性升级抬地板后旧客户端干净拒绝并提示更新；
 * 未声明地板的旧口径服务端按「版本不得高于客户端」保守判定。
 */

test('快照 schema 门禁：同版本与旧服务端载荷放行', () => {
  assert.equal(snapshotSchemaReadable(SNAPSHOT_SCHEMA_VERSION), true)
  // 旧服务端 payload（无 minReader 字段，地板=标签——additive 缺省字段由解码层兜底）
  assert.equal(snapshotSchemaReadable(SNAPSHOT_SCHEMA_VERSION - 1), true)
})

test('快照 schema 门禁：additive 升级凭读者地板放行', () => {
  // 服务端 v43 additive（声明 v41 起可读）：本版本放行
  assert.equal(snapshotSchemaReadable(SNAPSHOT_SCHEMA_VERSION + 1, SNAPSHOT_SCHEMA_VERSION - 1), true)
  // 地板恰等于本版本：边界放行
  assert.equal(snapshotSchemaReadable(SNAPSHOT_SCHEMA_VERSION + 1, SNAPSHOT_SCHEMA_VERSION), true)
})

test('快照 schema 门禁：破坏性与未声明地板的新版本拒绝', () => {
  // 破坏性升级（地板抬过本版本）：必须拒绝
  assert.equal(snapshotSchemaReadable(SNAPSHOT_SCHEMA_VERSION + 2, SNAPSHOT_SCHEMA_VERSION + 2), false)
  // 未声明地板的新版本（旧口径服务端）：保守拒绝
  assert.equal(snapshotSchemaReadable(SNAPSHOT_SCHEMA_VERSION + 1), false)
})

test('快照 schema 门禁：非法输入一律拒绝', () => {
  assert.equal(snapshotSchemaReadable(undefined), false)
  assert.equal(snapshotSchemaReadable(undefined, undefined), false)
  assert.equal(snapshotSchemaReadable(Number.NaN), false)
  assert.equal(snapshotSchemaReadable('42'), false)
})
