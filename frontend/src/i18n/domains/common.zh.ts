/**
 * 跨页面通用文本域（zh-CN）：高频按钮、选择器占位与网络提示统一收拢，页面词典禁止重复定义。
 */
export const commonZh = {
  common: {
    confirm: '确认',
    cancel: '取消',
    close: '关闭',
    emptyData: '暂无数据',
    pickerEmpty: '不使用物品',
    selectItem: '选择使用物品',
    selectItemSlot: '选择使用物品 · 槽 {n}',
    repeatsCount: '{n}次',
    quantity: '数量',
    progressComplete: '已圆满 (100%)',
    net: {
      queueOffline: '连接尚未建立，操作将在重连后可用。',
      invalidCommand: '操作格式无效。',
      requestTimeout: '操作等待服务端响应超时，请检查连接后重试。',
      sessionExpired: '登录会话已失效，请重新登录。',
      kickedByOther: '账号已在其他窗口或设备登录，当前连接已被替换。',
      duplicateSession: '该账号已有在线连接，请关闭其他窗口后重试。',
      deviceQuotaExceeded: '当前设备同时在线的账号数量已达上限。',
      deviceCheckFailed: '设备校验信息缺失，请刷新页面后重试。',
      parseUnstable: '协议数据持续异常，已重置连接以恢复同步。',
    },
  },
}
