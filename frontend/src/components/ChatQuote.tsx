import { memo } from 'react'
import { useT } from '../i18n'
import type { ChatQuoteDto } from '../network/protocol'
import { ChatMessageBody } from './ChatMessageContent'
import { Icon } from './Icon'

/** 回复草稿与已发送消息使用同一个引用样式；只有草稿允许移除。 */
export const ChatQuote = memo(function ChatQuote({ quote, onRemove }: { quote: ChatQuoteDto; onRemove?: () => void }): React.JSX.Element {
  const t = useT()
  return (
    <div className={`chat-quote${onRemove ? ' chat-quote--draft' : ''}`}>
      <blockquote>
        <strong>{t('combat.chat.quoteAuthor', { name: quote.senderName })}</strong>
        <span className="chat-quote-text"><ChatMessageBody body={quote.body} compact /></span>
      </blockquote>
      {onRemove && (
        <button aria-label={t('combat.chat.messageQuoteRemove')} onClick={onRemove} title={t('combat.chat.messageQuoteRemove')} type="button">
          <Icon name="close" size={14} />
        </button>
      )}
    </div>
  )
})
