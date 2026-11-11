/**
 * 快照 schema 版本门禁：服务端在 DTO 上声明「仍可解码的最老客户端版本」（读者地板），
 * 客户端接受一切地板不高于自身版本的载荷——additive 升级不再锁定旧客户端；
 * 未声明地板时按保守语义回退为「版本不得高于客户端」。
 * 纯函数无依赖，供 snapshot.ts 门禁与单测直接引用。
 */

/** 当前支持的存档 schema 版本：与服务端 SectionCodec.CurrentSchemaVersion 保持一致。 */
export const SNAPSHOT_SCHEMA_VERSION = 42

/** schemaVersion 为载荷标签，minReaderSchemaVersion 为服务端声明的读者地板；任一缺失按保守语义拒绝。 */
export function snapshotSchemaReadable(
  schemaVersion: number | undefined,
  minReaderSchemaVersion?: number | undefined,
): boolean {
  const floor = minReaderSchemaVersion ?? schemaVersion
  return typeof floor === 'number' && Number.isFinite(floor) && floor <= SNAPSHOT_SCHEMA_VERSION
}
