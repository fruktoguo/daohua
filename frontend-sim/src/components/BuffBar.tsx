import { sim, useSimStore } from '../game/store'

export function BuffBar() {
  useSimStore(state => state.buffs)
  const clock = useSimStore(state => state.clock)
  const buffs = sim.state.buffs
  if (buffs.length === 0) {
    return null
  }

  return (
    <div className="buff-bar">
      {buffs.map(buff => {
        const remaining = Math.max(0, buff.expiresAt - clock)
        return (
          <span className="buff-chip" key={buff.buffId} title={`灵气收益 ×${buff.qiGainMultiplier}`}>
            {buff.name} ×{buff.stacks}
            <em>+{Math.round((buff.qiGainMultiplier - 1) * 100)}%</em>
            <span className="buff-timer">{remaining}s</span>
          </span>
        )
      })}
    </div>
  )
}
