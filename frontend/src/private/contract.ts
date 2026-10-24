/**
 * 私有客户端模块契约：主仓库网络层与顶层组件只经 `src/private/` 稳定出口交互。
 * 开源 stub 与私有实现都必须满足该导出面；实现侧导出面漂移在完整口径类型检查中报错。
 */
import type { ComponentType } from 'react'

/** 私有客户端模块的本地状态形状（标签页互斥守卫的展示状态）。 */
export interface PrivateModuleState {
  tabBlocked: boolean
  tabGuardEnabled: boolean
}

/** 私有客户端模块契约（导出面即调用面，详见 docs/backend/private-modules.md）。 */
export interface PrivateClientModuleContract {
  /** 关闭码 → i18n 提示词条映射；命中即禁止自动重连 */
  readonly ANTI_MULTI_CLOSE_NOTICE_KEYS: Readonly<Record<number, string>>
  /** 应用服务端经 HelloAck 下发的标签页互斥开关 */
  applyAntiMultiOpenConfig(config: { tabGuardEnabled?: boolean } | null | undefined): void
  /** 应用启动时安装一次守卫；重复调用幂等 */
  installAntiMultiOpen(): void
  /** 释放守卫与低频策略刷新，供应用卸载与重新挂载 */
  disposeAntiMultiOpen(): void
  /** 当前标签页是否已被互斥守卫阻断（阻断时网络层阻止重连） */
  isTabBlocked(): boolean
  /** 由网络层注册：标签页被阻断时需要执行的拆除动作（断开 socket、停止心跳与自动重连） */
  registerBlockedTeardown(teardown: () => void): void
  /** 客户端扩展模块声明：Hello 握手上报，启用会话准入模块的服务端据此校验；开源构建恒为 null */
  getModuleDeclaration(): string | null
  /** 跨浏览器稳定的设备指纹摘要；开源构建恒为 null */
  getBrowserFingerprint(): Promise<string | null>
  /** 私有模块状态订阅（与 zustand useStore 同形的直接选择器调用） */
  useAntiMultiOpenStore<T>(selector: (state: PrivateModuleState) => T): T
  /** 标签页被阻断时的整页展示组件（stub 永不渲染） */
  readonly MultiTabBlockedPage: ComponentType
}
