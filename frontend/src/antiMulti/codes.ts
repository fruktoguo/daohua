/**
 * 防多开模块自定义 WebSocket 关闭码（4000-4999 私有用途段）。
 * 与服务端 src/Server/Clean/AntiMultiOpen/AntiMultiOpenConstants.cs 严格一一对应，
 * 任何取值变更必须双边同步。
 */
export const ANTI_MULTI_CLOSE_CODES = {
  /** 旧连接被同账号的新会话顶替下线 */
  SessionKicked: 4005,
  /** 设备指纹并发账号配额已满，新登录被拒绝 */
  DeviceQuotaExceeded: 4006,
  /** 设备配额开启但客户端未上报合法指纹 */
  DeviceFingerprintRequired: 4007,
  /** 互踢关闭时同账号第二个连接被拒绝 */
  DuplicateSession: 4008,
} as const

/** 关闭码 → i18n 提示词条映射；命中即禁止自动重连 */
export const ANTI_MULTI_CLOSE_NOTICE_KEYS: Readonly<Record<number, string>> = {
  [ANTI_MULTI_CLOSE_CODES.SessionKicked]: 'common.net.kickedByOther',
  [ANTI_MULTI_CLOSE_CODES.DeviceQuotaExceeded]: 'common.net.deviceQuotaExceeded',
  [ANTI_MULTI_CLOSE_CODES.DeviceFingerprintRequired]: 'common.net.deviceCheckFailed',
  [ANTI_MULTI_CLOSE_CODES.DuplicateSession]: 'common.net.duplicateSession',
}
