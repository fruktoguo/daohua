import { useState } from 'react'
import { getAction } from '../game/catalog'
import type { ActionType } from '../game/ids'
import { gameCommands, sim, useSimStore } from '../game/store'
import { ProgressBar } from './ProgressBar'

const TYPE_GLYPHS: Record<ActionType, { glyph: string; tone: string }> = {
  cultivation: { glyph: '修', tone: 'cyan' },
  exploration: { glyph: '探', tone: 'green' },
  alchemy: { glyph: '丹', tone: 'amber' }
}

// 顶栏中间：当前任务占约 1/3 宽，后续任务占约 1/5 宽并弱化显示；
// 完整队列收进下拉浮层，展开不挤压布局。
export function QueueCenter() {
  useSimStore(state => state.queue)
  const [open, setOpen] = useState(false)
  const queue = sim.state.queue
  const current = queue.entries[0]
  const currentDef = current ? getAction(current.actionId) : null
  const upcoming = queue.entries.slice(1, 3)

  return (
    <div className="queue-center">
      {current && currentDef ? (
        <div className="qc-current">
          <span className={`item-glyph qc-glyph tone-${TYPE_GLYPHS[currentDef.type].tone}`}>
            {TYPE_GLYPHS[currentDef.type].glyph}
          </span>
          <div className="qc-main">
            <div className="qc-title">
              <strong>{currentDef.name}</strong>
              <span className="qc-repeats">
                {current.remainingRepeats < 0 ? '∞' : `×${current.remainingRepeats}`}
              </span>
              {current.status === 'blocked' ? (
                <span className="badge badge-danger">资源不足</span>
              ) : (
                <span className="badge">进行中</span>
              )}
            </div>
            <ProgressBar value={current.progressSeconds} max={current.durationSeconds} />
          </div>
        </div>
      ) : (
        <div className="qc-current qc-idle">队列空闲 — 在下方选择行动加入队列</div>
      )}

      {upcoming.map(entry => {
        const definition = getAction(entry.actionId)
        return (
          <div className="qc-next" key={entry.entryId} title={definition.description}>
            <span className={`item-glyph tone-${TYPE_GLYPHS[definition.type].tone}`}>
              {TYPE_GLYPHS[definition.type].glyph}
            </span>
            <div className="qc-next-text">
              <span className="qc-next-name">{definition.name}</span>
              <span className="qc-next-repeats">
                {entry.remainingRepeats < 0 ? '∞' : `×${entry.remainingRepeats}`}
              </span>
            </div>
          </div>
        )
      })}

      <div className="qc-controls">
        <label className="loop-toggle">
          <input
            type="checkbox"
            checked={queue.loopEnabled}
            onChange={event => gameCommands.setLoop(event.target.checked)}
          />
          循环
        </label>
        <button
          type="button"
          className="qc-expand"
          disabled={queue.entries.length === 0}
          onClick={() => setOpen(value => !value)}
        >
          队列 {queue.entries.length} {open ? '▴' : '▾'}
        </button>
      </div>

      {open && (
        <>
          <div className="queue-backdrop" onClick={() => setOpen(false)} />
          <ol className="queue-dropdown">
            {queue.entries.map((entry, index) => {
              const definition = getAction(entry.actionId)
              return (
                <li
                  key={entry.entryId}
                  className={`queue-entry${index === 0 ? ' active' : ''}${entry.status === 'blocked' ? ' blocked' : ''}`}
                >
                  <span className="queue-order">{index + 1}</span>
                  <span className={`item-glyph tone-${TYPE_GLYPHS[definition.type].tone}`}>
                    {TYPE_GLYPHS[definition.type].glyph}
                  </span>
                  <div className="queue-main">
                    <div className="queue-title">
                      <span className="queue-name">{definition.name}</span>
                      <span className="queue-repeats">
                        {entry.remainingRepeats < 0 ? '∞' : `×${entry.remainingRepeats}`}
                      </span>
                      {entry.status === 'blocked' && <span className="badge badge-danger">资源不足</span>}
                      {index === 0 && entry.status === 'running' && <span className="badge">进行中</span>}
                    </div>
                    <div className="queue-sub">{definition.description}</div>
                  </div>
                  <div className="queue-actions">
                    <button
                      type="button"
                      title="上移"
                      disabled={index === 0}
                      onClick={() => gameCommands.move(entry.entryId, -1)}
                    >
                      ↑
                    </button>
                    <button
                      type="button"
                      title="下移"
                      disabled={index === queue.entries.length - 1}
                      onClick={() => gameCommands.move(entry.entryId, 1)}
                    >
                      ↓
                    </button>
                    <button type="button" title="移除" onClick={() => gameCommands.remove(entry.entryId)}>
                      ✕
                    </button>
                  </div>
                </li>
              )
            })}
          </ol>
        </>
      )}
    </div>
  )
}
