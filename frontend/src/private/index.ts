/**
 * 私有客户端模块稳定出口：主仓库调用点一律经此 import，不直接引用 stub 或私有挂载目录。
 * 拼装机制与兼容矩阵见 docs/backend/private-modules.md。
 */
export * from './impl'
