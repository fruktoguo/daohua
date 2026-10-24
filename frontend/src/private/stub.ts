/**
 * 开源默认实现：无标签页互斥守卫、无浏览器指纹采集、无模块声明。
 * 仅保留 4010「服务器要求专用客户端」的关闭码提示映射——开源前端连接到
 * 启用了会话准入模块的服务端时据此停止自动重连并展示可理解的原因。
 */
import type { ComponentType } from 'react'
import type { PrivateClientModuleContract, PrivateModuleState } from './contract'

const NullBlockedPage: ComponentType = () => null

const api = {
  ANTI_MULTI_CLOSE_NOTICE_KEYS: {
    4010: 'common.net.dedicatedClientRequired',
  } as Readonly<Record<number, string>>,
  applyAntiMultiOpenConfig(_config: { tabGuardEnabled?: boolean } | null | undefined): void {
    // 开源构建没有标签页互斥守卫可配置。
  },
  installAntiMultiOpen(): void {
    // 开源构建无守卫可安装。
  },
  disposeAntiMultiOpen(): void {
    // 开源构建无守卫可释放。
  },
  isTabBlocked(): boolean {
    return false
  },
  registerBlockedTeardown(_teardown: () => void): void {
    // 开源构建不存在阻断态，无需登记拆除动作。
  },
  getModuleDeclaration(): string | null {
    return null
  },
  getBrowserFingerprint(): Promise<string | null> {
    return Promise.resolve(null)
  },
  useAntiMultiOpenStore<T>(selector: (state: PrivateModuleState) => T): T {
    return selector({ tabBlocked: false, tabGuardEnabled: false })
  },
  MultiTabBlockedPage: NullBlockedPage,
} satisfies PrivateClientModuleContract

export const ANTI_MULTI_CLOSE_NOTICE_KEYS = api.ANTI_MULTI_CLOSE_NOTICE_KEYS
export const applyAntiMultiOpenConfig = api.applyAntiMultiOpenConfig
export const installAntiMultiOpen = api.installAntiMultiOpen
export const disposeAntiMultiOpen = api.disposeAntiMultiOpen
export const isTabBlocked = api.isTabBlocked
export const registerBlockedTeardown = api.registerBlockedTeardown
export const getModuleDeclaration = api.getModuleDeclaration
export const getBrowserFingerprint = api.getBrowserFingerprint
export const useAntiMultiOpenStore = api.useAntiMultiOpenStore
export const MultiTabBlockedPage = api.MultiTabBlockedPage
