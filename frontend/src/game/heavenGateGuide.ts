/**
 * 开天门首次规则说明弹窗的本地持久化辅助模块。
 * 按当前浏览器维度记录是否已查看过独立说明弹窗；存储不可用或受限时优雅降级为会话内记忆，不阻断主流程。
 */
export const HEAVEN_GATE_GUIDE_SEEN_STORAGE_KEY = 'daohua.heavenGate.guideSeen.v1'

const HEAVEN_GATE_GUIDE_SEEN_VALUE = '1'

type StorageReader = Pick<Storage, 'getItem'>
type StorageWriter = Pick<Storage, 'setItem'>
type StorageAdapter = StorageReader & StorageWriter

/** 本地存储受限/抛错时的会话内只读兜底标记。 */
let sessionSeenFallback = false

function defaultStorage(): Storage | null {
  try {
    return typeof window !== 'undefined' ? window.localStorage : null
  } catch {
    return null
  }
}

/** 读取当前浏览器是否已查看过开天门规则说明弹窗。 */
export function hasSeenHeavenGateGuide(storage: StorageReader | null = defaultStorage()): boolean {
  if (sessionSeenFallback) return true
  if (!storage) return false
  try {
    return storage.getItem(HEAVEN_GATE_GUIDE_SEEN_STORAGE_KEY) === HEAVEN_GATE_GUIDE_SEEN_VALUE
  } catch {
    return sessionSeenFallback
  }
}

/** 记录当前浏览器已查看过开天门规则说明弹窗。 */
export function markHeavenGateGuideSeen(storage: StorageWriter | null = defaultStorage()): void {
  if (!storage) {
    sessionSeenFallback = true
    return
  }
  try {
    storage.setItem(HEAVEN_GATE_GUIDE_SEEN_STORAGE_KEY, HEAVEN_GATE_GUIDE_SEEN_VALUE)
  } catch {
    // 无痕模式或存储满额时降级为当前会话内已查看，避免重复自动弹窗。
    sessionSeenFallback = true
  }
}

/**
 * 消费一次「首次打开开天门自动弹出说明」机会：
 * 若尚未看过则立即写入已读标记并返回 `true`，否则返回 `false`。
 */
export function consumeFirstOpenHeavenGateGuide(
  storage: StorageAdapter | null = defaultStorage(),
): boolean {
  if (hasSeenHeavenGateGuide(storage)) return false
  markHeavenGateGuideSeen(storage)
  return true
}

/** 仅用于单元测试重置无存储环境下的会话兜底状态。 */
export function resetHeavenGateGuideSessionFallbackForTest(): void {
  sessionSeenFallback = false
}
