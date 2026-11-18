import { memo, useRef, useState, type ReactNode } from 'react'
import { quoteInChat, resolveMessageDestination } from '../game/chatShare'
import { chatStickerService } from '../game/chatStickerService'
import { useGameStore } from '../game/gameStore'
import { isQqEmoji } from '../game/qqEmojis'
import { useT } from '../i18n'
import type { ChatMessage } from '../logging/chatService'
import { gameClient } from '../network/gameClient'
import { copyTextToClipboard } from '../utils/clipboard'
import { FloatingPopover } from './FloatingPopover'

export function repeatChatMessage(message: ChatMessage, t: ReturnType<typeof useT>): void {
  const destination = resolveMessageDestination(message)
  if (!destination) {
    useGameStore.getState().setNotice(t('combat.chat.messageUnavailable'))
    return
  }
  gameClient.sendChat(destination.channel, message.body, destination.targetPlayerId, undefined, sent => {
    if (!sent) return
    const text = message.body.trim()
    const emoji = /^\[([^\[\]\s]+)\]$/.exec(text)?.[1]
    if (emoji && isQqEmoji(emoji)) chatStickerService.recordEmojiUsage(emoji)
    else if (chatStickerService.isStickerSaved(text)) chatStickerService.recordStickerUsage(text)
    useGameStore.getState().setNotice(t('combat.chat.repeatSuccessNotice'))
  })
}

const INTERACTIVE_CONTENT = 'button, a, input, [role="button"], [role="link"], .daohua-floating-trigger'

/** 富文本自身保持可用；正文正常点击打开菜单，拖选与触屏原生选词不拦截。 */
export const ChatMessageActions = memo(function ChatMessageActions({ message, children }: { message: ChatMessage; children: ReactNode }): React.JSX.Element {
  const t = useT()
  const [open, setOpen] = useState(false)
  const bubbleRef = useRef<HTMLDivElement>(null)
  const buttonRef = useRef<HTMLButtonElement>(null)
  const setLike = (): void => {
    const destination = resolveMessageDestination(message)
    if (!destination) {
      useGameStore.getState().setNotice(t('combat.chat.messageUnavailable'))
      return
    }
    gameClient.setChatLike({ channel: destination.channel, targetPlayerId: destination.targetPlayerId, messageId: message.id, liked: !message.likedByMe })
  }

  const run = (action: () => void): void => {
    setOpen(false)
    action()
  }

  return (
    <>
      <div
        className={`chat-message-bubble${open ? ' is-open' : ''}`}
        onClick={event => {
          if (!(event.target instanceof Element) || event.target.closest(INTERACTIVE_CONTENT)) return
          const selection = window.getSelection()
          if (selection && !selection.isCollapsed && selection.containsNode(event.currentTarget, true)) return
          setOpen(previous => !previous)
        }}
        ref={bubbleRef}
      >
        <div className="chat-message-text">{children}</div>
        <button
          aria-expanded={open}
          aria-haspopup="dialog"
          aria-label={t('combat.chat.messageActions')}
          className="chat-message-actions-trigger"
          onClick={() => setOpen(previous => !previous)}
          ref={buttonRef}
          title={t('combat.chat.messageActions')}
          type="button"
        >⋯</button>
      </div>
      {open && (
        <FloatingPopover
          anchorEl={bubbleRef.current}
          className="chat-message-action-popover"
          content={
            <div aria-label={t('combat.chat.messageActions')} className="chat-message-action-list" role="group">
              <button autoFocus onClick={() => run(() => {
                buttonRef.current?.focus({ preventScroll: true })
                void copyTextToClipboard(message.body).then(copied => useGameStore.getState().setNotice(t(copied ? 'combat.chat.messageCopied' : 'combat.chat.messageCopyFailed')))
              })} type="button">{t('combat.chat.messageCopy')}</button>
              <button onClick={() => run(() => repeatChatMessage(message, t))} type="button">{t('combat.chat.messageRepeat')}</button>
              <button onClick={() => run(() => quoteInChat(message))} type="button">{t('combat.chat.messageQuote')}</button>
              <button aria-pressed={message.likedByMe} onClick={() => run(setLike)} type="button">{t(message.likedByMe ? 'combat.chat.messageUnlike' : 'combat.chat.messageLike')}</button>
            </div>
          }
          interactive
          onOpenChange={setOpen}
          open
          placement="top-start"
          trigger="manual"
          triggerClassName="chat-message-action-anchor"
          width={244}
        >{null}</FloatingPopover>
      )}
      {message.likeCount > 0 && (
        <button
          aria-label={`${t(message.likedByMe ? 'combat.chat.messageUnlike' : 'combat.chat.messageLike')} · ${t('combat.chat.messageLikes', { count: message.likeCount })}`}
          aria-pressed={message.likedByMe}
          className="chat-message-like"
          onClick={setLike}
          type="button"
        ><span aria-hidden="true">{message.likedByMe ? '♥' : '♡'}</span> {message.likeCount}</button>
      )}
    </>
  )
})
