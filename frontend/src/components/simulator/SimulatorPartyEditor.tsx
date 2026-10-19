import { memo, useCallback, useState } from 'react'
import { useGameStore } from '../../game/gameStore'
import { useSocialStore } from '../../game/socialStore'
import { useT } from '../../i18n'
import type { CombatAbilityDefinition, CombatAttributes } from '../../game/types'
import type { SimulatorCombatantConfig } from '../../game/simulator/simulatorTypes'
import { StatGlyph } from '../StatGlyph'

interface SimulatorPartyEditorProps {
  allies: SimulatorCombatantConfig[]
  onChangeAllies: (allies: SimulatorCombatantConfig[]) => void
  abilities: readonly CombatAbilityDefinition[]
}

const DEFAULT_ATTRIBUTES: CombatAttributes = {
  maxHitpoints: 300,
  maxManapoints: 100,
  physicalAttack: 30,
  spellAttack: 20,
  armor: 15,
  magicResistance: 10,
  accuracy: 20,
  evasion: 10,
  critical: 10,
  criticalResistance: 10,
  criticalDamagePoints: 50,
  breakPower: 10,
  parryResolve: 10,
  hpRegenPerFiveSeconds: 5,
  mpRegenPerFiveSeconds: 3,
  attackIntervalMilliseconds: 1000,
  physicalDamageBonusPoints: 0,
  spellDamageBonusPoints: 0,
  metalDamageBonusPoints: 0,
  woodDamageBonusPoints: 0,
  waterDamageBonusPoints: 0,
  fireDamageBonusPoints: 0,
  earthDamageBonusPoints: 0,
  totalDamageBonusPoints: 0,
  basicAttackBonusPoints: 0,
  skillBonusPoints: 0,
  physicalDamageReductionPoints: 0,
  spellDamageReductionPoints: 0,
  metalDamageReductionPoints: 0,
  woodDamageReductionPoints: 0,
  waterDamageReductionPoints: 0,
  fireDamageReductionPoints: 0,
  earthDamageReductionPoints: 0,
  damageAbsorptionPoints: 0,
  fixedDamageBonus: 0,
  fixedDamageReduction: 0,
  fixedPhysicalDamageBonus: 0,
  fixedPhysicalDamageReduction: 0,
  fixedSpellDamageBonus: 0,
  fixedSpellDamageReduction: 0,
  attackSpeedPoints: 0,
  actionSpeedPoints: 0,
  cooldownReductionPoints: 0,
  threatBonusPoints: 0,
  hpDrainPoints: 0,
  mpDrainPoints: 0,
  damageReflectPoints: 0,
  healingBonusPoints: 0,
}

export const SimulatorPartyEditor = memo(function SimulatorPartyEditor({
  allies,
  onChangeAllies,
  abilities,
}: SimulatorPartyEditorProps): React.JSX.Element {
  const t = useT()
  const [selectedIdx, setSelectedIdx] = useState(0)
  const currentAlly = allies[selectedIdx] || allies[0]

  const party = useSocialStore(state => state.party)
  const myName = useGameStore(state => state.name)
  const myAttributes = useGameStore(state => state.combatAttributes)
  const myCombat = useGameStore(state => state.combat)

  // 1. 导入自身属性
  const handleImportSelf = useCallback(() => {
    if (!currentAlly) return
    const newAlly: SimulatorCombatantConfig = {
      ...currentAlly,
      name: myName || t('combat.simulator.selfName'),
      isSelf: true,
      attributes: myAttributes ? { ...myAttributes } : { ...DEFAULT_ATTRIBUTES },
      abilitySlots: myCombat?.abilitySlots ? [...myCombat.abilitySlots] : [],
      abilityTriggers: myCombat?.abilityTriggers ? { ...myCombat.abilityTriggers } : {},
    }
    const nextList = [...allies]
    nextList[selectedIdx] = newAlly
    onChangeAllies(nextList)
  }, [allies, currentAlly, myAttributes, myCombat, myName, onChangeAllies, selectedIdx, t])

  // 2. 导入队友
  const handleImportTeammate = useCallback((memberId: string, memberName: string) => {
    if (!currentAlly) return
    const newAlly: SimulatorCombatantConfig = {
      ...currentAlly,
      id: memberId,
      name: memberName,
      isSelf: false,
      attributes: myAttributes ? { ...myAttributes } : { ...DEFAULT_ATTRIBUTES },
      abilitySlots: myCombat?.abilitySlots ? [...myCombat.abilitySlots] : [],
    }
    const nextList = [...allies]
    nextList[selectedIdx] = newAlly
    onChangeAllies(nextList)
  }, [allies, currentAlly, myAttributes, myCombat?.abilitySlots, onChangeAllies, selectedIdx])

  // 3. 修改属性值
  const handleUpdateStat = useCallback((key: keyof CombatAttributes, val: number) => {
    if (!currentAlly) return
    const updated: SimulatorCombatantConfig = {
      ...currentAlly,
      attributes: {
        ...currentAlly.attributes,
        [key]: Math.max(0, val),
      },
    }
    const nextList = [...allies]
    nextList[selectedIdx] = updated
    onChangeAllies(nextList)
  }, [allies, currentAlly, onChangeAllies, selectedIdx])

  // 4. 添加新队员
  const handleAddMember = useCallback(() => {
    if (allies.length >= 5) return
    const newMember: SimulatorCombatantConfig = {
      id: `member-${Date.now()}`,
      name: `${t('combat.simulator.allyLabel')} ${allies.length + 1}`,
      isSelf: false,
      formationIndex: allies.length,
      attributes: { ...DEFAULT_ATTRIBUTES },
      abilitySlots: [
        { abilityId: 'internal-basic', level: 1 },
        { abilityId: abilities[0]?.abilityId || 'spell-basic', level: 1 },
      ],
    }
    onChangeAllies([...allies, newMember])
    setSelectedIdx(allies.length)
  }, [abilities, allies, onChangeAllies, t])

  // 5. 移除当前队员
  const handleRemoveMember = useCallback((index: number) => {
    if (allies.length <= 1) return
    const nextList = allies.filter((_, i) => i !== index)
    onChangeAllies(nextList)
    setSelectedIdx(Math.max(0, index - 1))
  }, [allies, onChangeAllies])

  if (!currentAlly) return <div className="sim-empty-party">{t('combat.simulator.emptyParty')}</div>

  return (
    <div className="sim-party-editor">
      {/* 顶部成员标签页选择 */}
      <div className="sim-party-tabs">
        {allies.map((ally, idx) => (
          <button
            key={ally.id}
            type="button"
            className={`sim-party-tab-btn ${idx === selectedIdx ? 'is-active' : ''}`}
            onClick={() => setSelectedIdx(idx)}
          >
            <span>{ally.name}</span>
            {ally.isSelf && <span className="sim-tag-self">{t('combat.simulator.tagSelf')}</span>}
            {allies.length > 1 && (
              <span
                role="button"
                tabIndex={0}
                className="sim-tab-close"
                onClick={e => {
                  e.stopPropagation()
                  handleRemoveMember(idx)
                }}
                onKeyDown={e => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.stopPropagation()
                    handleRemoveMember(idx)
                  }
                }}
              >
                ×
              </span>
            )}
          </button>
        ))}
        {allies.length < 5 && (
          <button
            type="button"
            className="sim-party-tab-add"
            onClick={handleAddMember}
            title={t('combat.simulator.addMember')}
          >
            + {t('combat.simulator.addMember')}
          </button>
        )}
      </div>

      {/* 快捷导入操作栏 */}
      <div className="sim-editor-toolbar">
        <button
          type="button"
          className="sim-btn sim-btn--primary"
          onClick={handleImportSelf}
        >
          📥 {t('combat.simulator.importSelf')}
        </button>

        {party && party.members && party.members.length > 0 && (
          <div className="sim-teammate-import-select">
            <span className="sim-label">👥 {t('combat.simulator.importTeammate')}:</span>
            <select
              className="sim-select"
              onChange={e => {
                const member = party.members.find(m => m.playerId === e.target.value)
                if (member) handleImportTeammate(member.playerId, member.displayName)
              }}
              defaultValue=""
            >
              <option value="" disabled>{t('combat.simulator.selectTeammate')}</option>
              {party.members.map(m => (
                <option key={m.playerId} value={m.playerId}>
                  {m.displayName}
                </option>
              ))}
            </select>
          </div>
        )}
      </div>

      {/* 属性微调输入网格 */}
      <div className="sim-stats-grid-container">
        <div className="sim-stats-subheading">
          <span>⚔️ {t('combat.simulator.combatStats')}</span>
        </div>
        <div className="sim-stats-grid">
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="hp" /> {t('combat.simulator.statHp')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.maxHitpoints}
              onChange={e => handleUpdateStat('maxHitpoints', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="mp" /> {t('combat.simulator.statMp')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.maxManapoints}
              onChange={e => handleUpdateStat('maxManapoints', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="physAtk" /> {t('combat.simulator.statPhysAtk')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.physicalAttack}
              onChange={e => handleUpdateStat('physicalAttack', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="spellAtk" /> {t('combat.simulator.statSpellAtk')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.spellAttack}
              onChange={e => handleUpdateStat('spellAttack', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="armor" /> {t('combat.simulator.statArmor')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.armor}
              onChange={e => handleUpdateStat('armor', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="magicRes" /> {t('combat.simulator.statMagicRes')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.magicResistance}
              onChange={e => handleUpdateStat('magicResistance', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="accuracy" /> {t('combat.simulator.statAccuracy')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.accuracy}
              onChange={e => handleUpdateStat('accuracy', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="evasion" /> {t('combat.simulator.statEvasion')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.evasion}
              onChange={e => handleUpdateStat('evasion', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="critical" /> {t('combat.simulator.statCrit')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.critical}
              onChange={e => handleUpdateStat('critical', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="criticalResistance" /> {t('combat.simulator.statCritRes')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.criticalResistance}
              onChange={e => handleUpdateStat('criticalResistance', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="breakPower" /> {t('combat.simulator.statBreak')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.breakPower}
              onChange={e => handleUpdateStat('breakPower', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name"><StatGlyph glyph="parryResolve" /> {t('combat.simulator.statParry')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.parryResolve}
              onChange={e => handleUpdateStat('parryResolve', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name">{t('combat.simulator.statMpRegen')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.mpRegenPerFiveSeconds}
              onChange={e => handleUpdateStat('mpRegenPerFiveSeconds', Number(e.target.value))}
            />
          </label>
          <label className="sim-stat-field">
            <span className="sim-stat-name">{t('combat.simulator.statHpRegen')}</span>
            <input
              type="number"
              className="sim-input"
              value={currentAlly.attributes.hpRegenPerFiveSeconds}
              onChange={e => handleUpdateStat('hpRegenPerFiveSeconds', Number(e.target.value))}
            />
          </label>
        </div>
      </div>
    </div>
  )
})
