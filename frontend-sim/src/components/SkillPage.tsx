import { actionsOfType, getItemMeta } from '../game/catalog'
import { ACTION_TYPE_NAMES, ITEM_IDS, type ActionType } from '../game/ids'
import { gameCommands, sim, useSimStore } from '../game/store'

const TYPE_DESCRIPTIONS: Record<ActionType, string> = {
  cultivation: '打坐吐纳，引气入体，日积月蓄终成大道。',
  exploration: '走出洞府，深入山野，寻找修炼所需的灵草。',
  alchemy: '以灵草为材，开炉炼制引气丹，助益修行。'
}

const ENQUEUE_PRESETS = [1, 10, 100, -1] as const

export function SkillPage({ type }: { type: ActionType }) {
  useSimStore(state => state.queue)
  useSimStore(state => state.inventory)
  const state = sim.state
  const slot = state.queue.autoUseSlots[type]
  const actions = actionsOfType(type)
  const pillCount = state.inventory[ITEM_IDS.qiGatheringPill] ?? 0

  return (
    <section className="panel skill-page">
      <header className="panel-header">
        <h2>{ACTION_TYPE_NAMES[type]}</h2>
        <p className="panel-desc">{TYPE_DESCRIPTIONS[type]}</p>
      </header>

      <div className="auto-use">
        <span className="auto-use-label">自动使用物品</span>
        <select
          value={slot.itemId ?? ''}
          onChange={event =>
            gameCommands.setAutoUse(type, event.target.value || null, slot.quantity, slot.enabled)
          }
        >
          <option value="">无</option>
          <option value={ITEM_IDS.qiGatheringPill}>引气丹（拥有 {pillCount}）</option>
        </select>
        <input
          type="number"
          min={1}
          max={99}
          value={slot.quantity}
          onChange={event => {
            const quantity = Number(event.target.value)
            if (Number.isInteger(quantity) && quantity >= 1 && quantity <= 99) {
              gameCommands.setAutoUse(type, slot.itemId, quantity, slot.enabled)
            }
          }}
        />
        <label>
          <input
            type="checkbox"
            checked={slot.enabled}
            disabled={!slot.itemId}
            onChange={event => gameCommands.setAutoUse(type, slot.itemId, slot.quantity, event.target.checked)}
          />
          启用
        </label>
      </div>

      <div className="action-cards">
        {actions.map(action => {
          const inputs = Object.entries(action.inputs)
          const outputs = Object.entries(action.outputs)
          const affordable = inputs.every(([itemId, amount]) => (state.inventory[itemId] ?? 0) >= amount)
          return (
            <article className="action-card" key={action.id}>
              <h3>{action.name}</h3>
              <p>{action.description}</p>
              <dl className="action-io">
                <div>
                  <dt>耗时</dt>
                  <dd>{action.durationSeconds}s</dd>
                </div>
                {inputs.length > 0 && (
                  <div>
                    <dt>消耗</dt>
                    <dd>
                      {inputs.map(([itemId, amount]) => (
                        <span key={itemId} className="io-item">
                          {getItemMeta(itemId).name} ×{amount}
                          <em className={affordable ? '' : 'lack'}>
                            （有 {(state.inventory[itemId] ?? 0).toLocaleString()}）
                          </em>
                        </span>
                      ))}
                    </dd>
                  </div>
                )}
                <div>
                  <dt>产出</dt>
                  <dd>
                    {outputs.map(([itemId, amount]) => (
                      <span key={itemId} className="io-item">
                        {getItemMeta(itemId).name} ×{amount}
                      </span>
                    ))}
                  </dd>
                </div>
              </dl>
              <div className="action-buttons">
                {ENQUEUE_PRESETS.map(preset => (
                  <button
                    key={preset}
                    type="button"
                    onClick={() => gameCommands.enqueue(action.id, preset)}
                  >
                    {preset === -1 ? '∞' : `×${preset}`}
                  </button>
                ))}
              </div>
            </article>
          )
        })}
      </div>
    </section>
  )
}
