/**
 * 私有客户端模块实现的唯一替换点：开源构建固定为 stub；
 * 存在私有模块挂载（src/private-impl/）时，构建期由 vite 正则 alias 重定向到私有实现入口。
 */
export * from './stub'
