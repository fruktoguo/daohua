import { useGameStore } from './gameStore'
import { getT } from '../i18n'
import { chatService } from '../logging/chatService'
import { useLogUnreadStore, type LogGroupId } from '../logging/logUnreadStore'

/**
 * 将指定富文本标记分享至传音（聊天）：
 * 1. 若当前日志面板处于非聊天分组（综合/系统/战斗），自动切至世界频道；
 * 2. 将标记填入输入框并聚焦；
 * 3. 弹出「已插入传音输入框」提示。
 */
export function shareToChat(markup: string): void {
  const currentGroup = useLogUnreadStore.getState().activeGroup
  const chatChannels: LogGroupId[] = ['world', 'nearby', 'friend', 'sect', 'party']
  if (!chatChannels.includes(currentGroup)) {
    useLogUnreadStore.getState().setActiveGroup('world')
  }
  chatService.insertDraft(markup)
  useGameStore.getState().setNotice(getT()('combat.chat.linkInsertedNotice'))
}
