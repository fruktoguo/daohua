import { memo, useCallback, useState } from 'react'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { marketStatsKey } from '../../network/apiClient'
import type { SimulatorMemberResultDto, SimulatorRunResultDto } from '../../game/simulator/wasmSimulatorTypes'
import { findCombatantInCatalog } from '../../game/ids'
import { ItemIcon, ItemReference } from '../ItemDisplay'
import { LogTemplateText } from '../LogFeed'

interface Props {
  results: SimulatorRunResultDto[]
  nodeName: (id: string) => string
  runSeed: string
  startedAtUnixSeconds: number
}

const LINGSHI = 'item.lingshi'
const ASSET_SEP = ''

function variantLevel(variant: string): number | null {
  if (!variant) return 0
  const raw = variant.startsWith('+') ? variant.slice(1) : variant
  if (!/^\d+$/.test(raw)) return null
  return Number(raw)
}

const ROOT_KEYS: readonly (keyof SimulatorMemberResultDto['rootExp'])[] =
  ['stamina', 'meridians', 'attack', 'bone', 'agility', 'consciousness']
const ROOT_LABEL_KEYS: Record<string, string> = {
  stamina: 'combat.simulator.core.stamina', meridians: 'combat.simulator.core.meridians',
  attack: 'combat.simulator.core.attack', bone: 'combat.simulator.core.bone',
  agility: 'combat.simulator.core.agility', consciousness: 'combat.simulator.core.consciousness'
}

function Valuation({ member }: { member: SimulatorMemberResultDto }) {
  const t = useT()
  const marketStats = useGameStore(state => state.marketStats)
  let known = 0
  let hasUnknown = false
  const unknownIds: string[] = []
  const totals = new Map<string, number>()
  for (const delta of member.assetDeltas) {
    const key = `${delta.itemId}${ASSET_SEP}${delta.variant}`
    totals.set(key, (totals.get(key) ?? 0) + delta.quantity)
  }
  for (const [key, quantity] of totals) {
    if (quantity === 0) continue
    const [itemId, variant] = key.split(ASSET_SEP)
    if (itemId === LINGSHI) { known += quantity; continue }
    const level = variantLevel(variant)
    const ref = level === null ? undefined
      : marketStats?.[marketStatsKey(itemId, level)]?.referencePrice
    if (ref != null && Number.isFinite(ref) && ref > 0) { known += ref * quantity; continue }
    hasUnknown = true
    if (!unknownIds.includes(itemId)) unknownIds.push(itemId)
  }
  return (
    <div className="wasm-sim-valuation">
      <span>{hasUnknown ? t('combat.simulator.partialValue') : t('combat.simulator.netValue')}: {known.toFixed(1)}
        <em className="wasm-sim-valuation-note">（{t('combat.simulator.currentMarket')}）</em>
        {unknownIds.length > 0 && (
          <em className="wasm-sim-valuation-note">（{t('combat.simulator.unpriced')}：{unknownIds.map(id =>
            <ItemReference key={id} itemId={id} className="wasm-sim-asset-inline" />)}）</em>
        )}
      </span>
    </div>
  )
}

export const WasmSimulatorResults = memo(function WasmSimulatorResults({ results, nodeName, runSeed, startedAtUnixSeconds }: Props) {
  const t = useT()
  const encounters = useGameStore(state => state.encounters)
  const maps = useGameStore(state => state.maps)
  const abilities = useGameStore(state => state.abilities)
  const [selected, setSelected] = useState(0)

  const enemyName = useCallback((id: string) =>
    findCombatantInCatalog(encounters, maps, id)?.name ?? id, [encounters, maps])

  const abilityName = useCallback((id: string | null) =>
    id ? (abilities.find(a => a.abilityId === id)?.name ?? id) : '—', [abilities])

  if (results.length === 0) return null
  const current = results[Math.min(selected, results.length - 1)]

  return (
    <div className="wasm-sim-result-list">
      <table className="wasm-sim-table wasm-sim-compare">
        <thead>
          <tr>
            <th>{t('combat.simulator.compare.node')}</th>
            <th>{t('combat.simulator.difficultyLabel')}</th>
            <th>{t('combat.simulator.compare.duration')}</th>
            <th>{t('combat.simulator.result.killsPerHour')}</th>
            <th>{t('combat.simulator.result.expPerHour')}</th>
            <th>{t('combat.simulator.result.deaths')}</th>
          </tr>
        </thead>
        <tbody>
          {results.map((result, ri) => {
            const hours = result.simulatedSeconds / 3600
            const first = result.members[0]
            const kills = (first?.normalKills ?? 0) + (first?.eliteKills ?? 0) + (first?.bossKills ?? 0)
            return (
              <tr key={ri} className={ri === selected ? 'is-active' : ''}
                onClick={() => setSelected(ri)}>
                <td>{nodeName(result.nodeId)}</td>
                <td>{result.difficulty}</td>
                <td>{result.simulatedSeconds}{t('combat.simulator.secondsUnit')}</td>
                <td>{hours > 0 ? (kills / hours).toFixed(1) : '—'}</td>
                <td>{hours > 0 ? ((first?.experience ?? 0) / hours).toFixed(1) : '—'}</td>
                <td>{first?.deaths ?? 0}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
      {current && (
        <section className="wasm-sim-result">
          <h4>{nodeName(current.nodeId)} · {t('combat.simulator.difficultyLabel')}{current.difficulty} · {t('combat.simulator.seedTitle')} {current.seed ?? runSeed} · {current.simulatedSeconds}{t('combat.simulator.secondsUnit')}</h4>
          {current.members.map((m, mi) => (
            <details key={mi} className="wasm-sim-result-member" open={mi === 0}>
              <summary>{m.displayName} — {t('combat.simulator.result.summary', {
                hp: m.hitpoints.toFixed(1), mp: m.manapoints.toFixed(1), wave: m.waveNumber
              })}</summary>
              <div className="wasm-sim-result-stats">
                <span>{t('combat.simulator.result.encounters')}: {m.encounters}</span>
                <span>{t('combat.simulator.result.cycles')}: {m.cycles}</span>
                <span>{t('combat.simulator.result.kills')}: {m.normalKills}+{m.eliteKills}+{m.bossKills}</span>
                <span>{t('combat.simulator.result.deaths')}: {m.deaths}</span>
                <span>{t('combat.simulator.result.exp')}: {m.experience.toFixed(1)}</span>
                <span>{t('combat.simulator.result.combatExp')}: {m.combatExperience.toFixed(1)}</span>
                <span>{t('combat.simulator.result.foundation')}: {m.foundation.toFixed(1)}</span>
                <span>{t('combat.simulator.result.repeats')}: {m.completedRepeats}</span>
              </div>
              <div className="wasm-sim-result-stats">
                <span>{t('combat.simulator.result.rootExp')}: {ROOT_KEYS.map(k =>
                  `${t(ROOT_LABEL_KEYS[k])} ${m.rootExp[k].toFixed(1)}`).join(' · ')}</span>
              </div>
              <Valuation member={m} />
              {m.items.length > 0 && <div className="wasm-sim-result-items">
                <span>{t('combat.simulator.result.gained')}:</span>
                {m.items.map((item, i) => <ItemReference key={i} itemId={item.itemId}
                  enhancementLevel={variantLevel(item.variant) ?? 0} amount={item.quantity} />)}
              </div>}
              {m.itemsLost.length > 0 && <div className="wasm-sim-result-items">
                <span>{t('combat.simulator.result.lost')}:</span>
                {m.itemsLost.map((item, i) => <ItemReference key={i} itemId={item.itemId}
                  enhancementLevel={variantLevel(item.variant) ?? 0} amount={item.quantity} />)}
              </div>}
              {m.remainingConsumableBalances.length > 0 && <div className="wasm-sim-result-items">
                <span>{t('combat.simulator.result.remaining')}:</span>
                {m.remainingConsumableBalances.map((asset, i) => (
                  <ItemReference key={i} itemId={asset.itemId} amount={asset.quantity}
                    enhancementLevel={variantLevel(asset.variant) ?? 0} className="wasm-sim-asset-inline" />
                ))}
              </div>}
              {m.combatStats.length > 0 && (
                <table className="wasm-sim-table">
                  <thead><tr><th>{t('combat.simulator.result.unit')}</th><th>{t('combat.simulator.result.dealt')}</th><th>{t('combat.simulator.result.taken')}</th><th>{t('combat.simulator.result.heal')}</th></tr></thead>
                  <tbody>{m.combatStats.map((u, ui) => (
                    <tr key={ui}><td>{u.displayName || enemyName(u.unitKey)}</td>
                      <td>{(u.damageDealtPhysical + u.damageDealtSpell + u.damageDealtTrue).toFixed(0)}</td>
                      <td>{(u.damageTakenPhysical + u.damageTakenSpell + u.damageTakenTrue).toFixed(0)}</td>
                      <td>{(u.healingRegen + u.healingOther).toFixed(0)}</td></tr>
                  ))}</tbody>
                </table>
              )}
              {m.enemies.length > 0 && <div className="wasm-sim-result-items">
                <span>{t('combat.simulator.result.enemies')}:</span>
                {m.enemies.map((e, i) => <span key={i} className="wasm-sim-tag">
                  <LogTemplateText template={`{{enemy:${e.enemyId}|${enemyName(e.enemyId)}}}`} />
                  {' '}{t('combat.simulator.hp')}{e.hitpoints.toFixed(0)} {t('combat.simulator.mp')}{e.manapoints.toFixed(0)}
                </span>)}
              </div>}
              {m.combatBuffCarry.length > 0 && <div className="wasm-sim-result-items">
                <span>{t('combat.simulator.result.buffs')}:</span>
                {m.combatBuffCarry.map((b, i) => <span key={i} className="wasm-sim-tag"><ItemIcon itemId={b.buffId} />{b.name || b.buffId}×{b.stacks} {b.remainingSeconds.toFixed(1)}s</span>)}
              </div>}
              {(m.combatSkills?.cooldowns ?? []).length > 0 && <div className="wasm-sim-result-items">
                <span>{t('combat.simulator.result.cooldowns')}:</span>
                {(m.combatSkills?.cooldowns ?? []).map((c, i) => <span key={i} className="wasm-sim-tag"><ItemIcon itemId={c.abilityId} />{abilityName(c.abilityId)} {c.remainingSeconds.toFixed(1)}s</span>)}
              </div>}
              {m.combatAutoUseSlots.length > 0 && <div className="wasm-sim-result-items">
                <span>{t('combat.simulator.result.slots')}:</span>
                {m.combatAutoUseSlots.map((slot, i) => (
                  <span key={i} className="wasm-sim-tag">
                    {slot.itemId
                      ? <ItemReference itemId={slot.itemId} amount={slot.quantity} className="wasm-sim-asset-inline" />
                      : `#${slot.slotIndex} —`}
                    {slot.enabled ? '' : `（${t('combat.simulator.slotDisabled')}）`}
                  </span>
                ))}
              </div>}
              {m.events.length > 0 && <div className="wasm-sim-result-events">
                <span>{t('combat.simulator.result.events')}（{m.events.length}{m.droppedEventCount > 0 ? ` +${m.droppedEventCount}` : ''}）:</span>
                <ul>{m.events.slice(-50).map((ev, i) => <li key={i}>{(ev.atUnixSeconds - startedAtUnixSeconds).toFixed(1)}s <LogTemplateText template={ev.message} /></li>)}</ul>
              </div>}
            </details>
          ))}
        </section>
      )}
    </div>
  )
})
