import { useEffect, useRef, useState } from 'react'
import { sim, useSimStore } from '../game/store'

// 下中底部：日志 + 聊天（聊天待多人模块接入）。
export function LogPanel() {
  const eventsVersion = useSimStore(state => state.events)
  const [tab, setTab] = useState<'log' | 'chat'>('log')
  const listRef = useRef<HTMLOListElement>(null)

  useEffect(() => {
    const list = listRef.current
    if (list) {
      list.scrollTop = list.scrollHeight
    }
  }, [eventsVersion, tab])

  return (
    <section className="panel log-panel">
      <div className="log-tabs">
        <button type="button" className={tab === 'log' ? 'active' : ''} onClick={() => setTab('log')}>
          日志
        </button>
        <button type="button" className={tab === 'chat' ? 'active' : ''} onClick={() => setTab('chat')}>
          聊天
        </button>
      </div>
      {tab === 'log' ? (
        <ol className="event-log" ref={listRef}>
          {sim.state.eventLog.map((message, index) => (
            <li key={`${index}-${message}`}>{message}</li>
          ))}
        </ol>
      ) : (
        <div className="chat-placeholder">聊天频道将在多人模块接入后开放。</div>
      )}
    </section>
  )
}
