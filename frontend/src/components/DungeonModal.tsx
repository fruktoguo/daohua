import { memo, useEffect, useMemo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { useSocialStore } from '../game/socialStore'
import {
  extractDungeonLevelAnchors,
  computeDungeonEnergy,
  computeDungeonLootPreview,
  getDefaultDifficultyForMode,
  getDungeonDifficultiesByMode,
  getDungeonDifficultyMeta,
  getDungeonEnergyCost,
  getDungeonMode,
  isRealityDifficulty,
  resolveDungeonClearRewardsPreview,
  resolveDungeonMinMonsterLevel,
  resolveDungeonMonsterForDisplay,
  resolveDungeonTargetBaseLevel,
  type DungeonMode,
} from '../game/dungeonRules'
import {
  findEquipmentInCatalog,
  findItemInCatalog,
  resolveItemCategoryLabel,
  resolveItemGrade,
  resolveItemMetadata,
  resolveItemSubCategory,
} from '../game/ids'
import { formatInteger, formatNumber } from '../game/format'
import type { DungeonDefinition } from '../game/types'
import { PartyActivityKind } from '../network/protocol'
import { gameClient } from '../network/gameClient'
import { useUiStore } from '../uiStore'
import { useT } from '../i18n'
import { CharacterAvatar } from './CharacterAvatar'
import { Icon } from './Icon'
import { ItemIcon, ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { Modal } from './Modal'
import { MonsterTooltipTarget } from './MonsterDisplay'
import { QuantitySelector } from './QuantitySelector'

interface DungeonModalProps {
  dungeon: DungeonDefinition | null
  open: boolean
  onClose: () => void
}

/** 格式化稀有掉落综合概率百分比 */
function formatLootChance(chance: number): string {
  const pct = chance * 100
  if (pct >= 10) return `${pct.toFixed(1)}%`
  return `${pct.toFixed(2)}%`
}

/**
 * 追忆副本备战弹窗 (DungeonModal)
 *
 * 承载「虚境（3 档）/ 现世（8 阶）」联动双下拉难度选择、
 * 波次下拉框 + 右侧出战妖兽瓦片（Hover 直接查看叠加现世动态等级与难度阶位后的具体属性）、
 * 通关独占战利品与挑战次数配置。
 */
export const DungeonModal = memo(function DungeonModal({
  dungeon,
  open,
  onClose,
}: DungeonModalProps): React.JSX.Element | null {
  const t = useT()
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const dungeons = useGameStore(state => state.dungeons)
  const items = useGameStore(state => state.items)
  const equipment = useGameStore(state => state.equipment)
  const queueEntries = useGameStore(state => state.queue.entries)
  const playerId = useGameStore(state => state.playerId)
  const realmLevel = useGameStore(state => state.stats.realmLevel) ?? 1
  const tiliValue = useGameStore(state => state.stats.tiliValue)
  const tiliUpdatedAt = useGameStore(state => state.stats.tiliUpdatedAtUnixSeconds)
  const tiliFallback = useGameStore(state => state.stats.tiliCurrent)
  const serverTimeMs = useGameStore(state => state.serverTimeUnixMilliseconds)
  const lastSyncPerf = useGameStore(state => state.lastSyncPerformanceTime)
  const party = useSocialStore(state => state.party)
  const setActiveTab = useUiStore(state => state.setActiveTab)
  const setSocialSubTab = useUiStore(state => state.setSocialSubTab)

  const [difficulty, setDifficulty] = useState<number>(0)
  const [selectedWaveNumber, setSelectedWaveNumber] = useState<number>(1)
  const [repeats, setRepeats] = useState<number>(1)
  const [nowSec, setNowSec] = useState<number>(() =>
    Math.floor((serverTimeMs + Math.max(0, performance.now() - lastSyncPerf)) / 1000),
  )

  useEffect(() => {
    setSelectedWaveNumber(1)
  }, [dungeon?.dungeonId])

  useEffect(() => {
    if (!open) return undefined
    const refresh = () => {
      const s = useGameStore.getState()
      const estimatedMs = s.serverTimeUnixMilliseconds + Math.max(0, performance.now() - s.lastSyncPerformanceTime)
      setNowSec(Math.floor(estimatedMs / 1000))
    }
    refresh()
    const timer = window.setInterval(refresh, 1000)
    return () => window.clearInterval(timer)
  }, [open])

  const energy = useMemo(
    () => computeDungeonEnergy(tiliValue, tiliUpdatedAt, nowSec, tiliFallback),
    [tiliValue, tiliUpdatedAt, nowSec, tiliFallback],
  )

  const levelAnchors = useMemo(() => extractDungeonLevelAnchors(dungeons), [dungeons])

  const nodeName = useMemo(() => {
    if (!dungeon) return ''
    for (const m of maps) {
      const found = m.nodes.find(n => n.nodeId === dungeon.nodeId)
      if (found) return found.name
    }
    return dungeon.nodeId
  }, [dungeon, maps])

  const currentMode = getDungeonMode(difficulty)
  const modeDifficulties = useMemo(() => getDungeonDifficultiesByMode(currentMode), [currentMode])

  // 单人或队伍最高玩家等级：现世模式下用于动态抬升副本怪物等级
  const maxPartyRealmLevel = realmLevel

  const activeWave = useMemo(() => {
    if (!dungeon || dungeon.waves.length === 0) return null
    return dungeon.waves.find(w => w.waveNumber === selectedWaveNumber) ?? dungeon.waves[0]
  }, [dungeon, selectedWaveNumber])

  const scaledWaveEnemies = useMemo(() => {
    if (!dungeon || !activeWave) return []
    return activeWave.enemies.map(enemy =>
      resolveDungeonMonsterForDisplay(enemy, dungeon, difficulty, maxPartyRealmLevel, levelAnchors),
    )
  }, [dungeon, activeWave, difficulty, maxPartyRealmLevel, levelAnchors])

  const clearRewards = useMemo(() => {
    if (!dungeon) return { experience: 0, spiritStones: 0 }
    return resolveDungeonClearRewardsPreview(dungeon, difficulty, maxPartyRealmLevel, levelAnchors)
  }, [dungeon, difficulty, maxPartyRealmLevel, levelAnchors])

  if (!open || !dungeon) return null

  const diffMeta = getDungeonDifficultyMeta(difficulty)
  const energyCost = getDungeonEnergyCost(difficulty)
  const recommendedVal = dungeon.recommendedValuation[difficulty] ?? dungeon.recommendedValuation[0] ?? 0
  const atDungeonNode = world.nodeId === dungeon.nodeId
  const hasEnoughTili = energy.current >= energyCost
  const isPartyLeader = party !== null && party.leaderId === playerId
  const queueLength = queueEntries.length
  const minMonsterLevel = resolveDungeonMinMonsterLevel(dungeon)
  const realityTargetLevel = resolveDungeonTargetBaseLevel(minMonsterLevel, maxPartyRealmLevel, difficulty)

  const handleModeChange = (nextMode: DungeonMode) => {
    if (nextMode === currentMode) return
    setDifficulty(getDefaultDifficultyForMode(nextMode))
  }

  const handleEnqueueSolo = (priority: boolean) => {
    if (!atDungeonNode || !hasEnoughTili) return
    if (gameClient.sendCommand('action.enqueue', {
      actionId: dungeon.dungeonId,
      repeatCount: repeats,
      pvpEnabled: false,
      difficulty,
      priority,
    })) {
      onClose()
      setActiveTab('combat')
    }
  }

  const handleGoParty = () => {
    if (isPartyLeader) {
      gameClient.sendSocialAction('party.update', undefined, undefined, {
        party: {
          activityKind: PartyActivityKind.Dungeon,
          nodeId: dungeon.nodeId,
          dungeonId: dungeon.dungeonId,
          difficulty,
          dungeonRepeatCount: repeats,
        },
      })
    }
    onClose()
    setSocialSubTab('party')
    setActiveTab('social')
  }

  return (
    <Modal
      labelledBy="dungeon-modal-title"
      onClose={onClose}
      open={open}
      size="large"
    >
      <div className="dungeon-modal">
        <header className="dungeon-modal-header">
          <div className="dungeon-modal-title-wrap">
            <h2 id="dungeon-modal-title">
              <Icon name="gate" size={18} />
              <span>{t('combat.dungeon.modalTitle', { name: dungeon.name })}</span>
            </h2>
            <p className="dungeon-modal-desc">{dungeon.description}</p>
            <div className="dungeon-modal-subbar">
              <span className="dungeon-modal-meta-chip">
                <Icon name="compass" size={13} />
                {t('combat.dungeon.nodeLabel', { name: nodeName })}
              </span>
              <span className={`dungeon-modal-meta-chip${hasEnoughTili ? ' is-ok' : ' is-warn'}`}>
                <Icon name="bolt" size={13} />
                {t('combat.dungeon.energyBadge', { current: energy.current, max: energy.max })}
              </span>
              {recommendedVal > 0 && (
                <span className="dungeon-modal-meta-chip">
                  {t('combat.dungeon.recommendedValBadge', { value: formatNumber(recommendedVal) })}
                </span>
              )}
              {isRealityDifficulty(difficulty) && (
                <span className="dungeon-modal-meta-chip is-reality">
                  {t('combat.dungeon.realityLevelBadge', { level: realityTargetLevel })}
                </span>
              )}
            </div>
          </div>
        </header>

        {/* 联动双下拉难度选择：左选虚境/现世，右选对应档位与耗体 */}
        <section className="dungeon-section">
          <div className="dungeon-diff-select-bar">
            <label className="dungeon-select-field">
              <span className="dungeon-select-label">{t('combat.dungeon.modeSelectLabel')}</span>
              <select
                aria-label={t('combat.dungeon.modeSelectLabel')}
                className="difficulty-select dungeon-native-select"
                onChange={event => handleModeChange(event.target.value as DungeonMode)}
                value={currentMode}
              >
                <option value="virtual">{t('combat.dungeon.mode.virtual')}</option>
                <option value="reality">{t('combat.dungeon.mode.reality')}</option>
              </select>
            </label>

            <label className="dungeon-select-field">
              <span className="dungeon-select-label">{t('combat.dungeon.tierSelectLabel')}</span>
              <select
                aria-label={t('combat.dungeon.tierSelectLabel')}
                className={`difficulty-select dungeon-native-select dungeon-diff-tone--${diffMeta.styleTone}`}
                onChange={event => setDifficulty(Number(event.target.value))}
                value={difficulty}
              >
                {modeDifficulties.map(item => (
                  <option key={item.level} value={item.level}>
                    {t('combat.dungeon.tierOptionWithCost', {
                      name: t(`combat.dungeon.difficulty.${item.key}`),
                      cost: item.energyCost,
                    })}
                  </option>
                ))}
              </select>
            </label>

            <span className={`dungeon-diff-pill dungeon-diff-tone--${diffMeta.styleTone}`}>
              {t('combat.dungeon.fullDifficultyLabel', {
                mode: t(`combat.dungeon.mode.${diffMeta.mode}`),
                tier: t(`combat.dungeon.difficulty.${diffMeta.key}`),
              })}
            </span>
          </div>
          {isRealityDifficulty(difficulty) && (
            <p className="dungeon-reality-hint">
              {t('combat.dungeon.realityRuleHint', { level: realityTargetLevel })}
            </p>
          )}
        </section>

        <div className="dungeon-two-col">
          {/* 波次下拉框 + 右侧出战怪物图标瓦片（Hover 直接查看当前难度与现世等级下的具体属性） */}
          <section className="dungeon-section dungeon-waves-section">
            <h3 className="dungeon-section-title">{t('combat.dungeon.wavesHeading')}</h3>
            <div className="dungeon-wave-browser">
              <div className="dungeon-wave-selector-col">
                <label className="dungeon-select-field is-vertical">
                  <span className="dungeon-select-label">{t('combat.dungeon.waveSelectLabel')}</span>
                  <select
                    aria-label={t('combat.dungeon.waveSelectLabel')}
                    className="difficulty-select dungeon-native-select"
                    onChange={event => setSelectedWaveNumber(Number(event.target.value))}
                    value={activeWave?.waveNumber ?? 1}
                  >
                    {dungeon.waves.map(wave => (
                      <option key={wave.waveNumber} value={wave.waveNumber}>
                        {t('combat.dungeon.waveOptionLabel', {
                          n: wave.waveNumber,
                          kind: t(`combat.dungeon.waveKind.${wave.kind}`),
                        })}
                      </option>
                    ))}
                  </select>
                </label>
                {activeWave && (
                  <span className={`dungeon-wave-kind is-${activeWave.kind}`}>
                    {t(`combat.dungeon.waveKind.${activeWave.kind}`)}
                  </span>
                )}
              </div>

              <div className="dungeon-wave-monsters-panel">
                <div className="ecology-monster-strip dungeon-wave-monster-strip">
                  {scaledWaveEnemies.map((enemy, idx) => {
                    const rawRank = (enemy.rank ?? 'normal').toLowerCase()
                    const rankClass = rawRank === 'boss' ? 'boss' : rawRank === 'elite' ? 'elite' : 'normal'
                    return (
                      <MonsterTooltipTarget
                        displayContents
                        isBoss={rankClass === 'boss'}
                        isElite={rankClass === 'elite'}
                        key={`${enemy.enemyId}-${idx}`}
                        monster={enemy}
                        placement="top"
                        preScaled
                      >
                        <button
                          aria-label={t('combat.ecology.inspectAria', { name: enemy.name })}
                          className={`ecology-monster-tile is-${rankClass}`}
                          type="button"
                        >
                          <CharacterAvatar
                            actorId={enemy.enemyId}
                            className="ecology-monster-avatar"
                            fallback={enemy.name.slice(0, 1)}
                            isFlipped
                            name={enemy.name}
                            role="enemy"
                            visual={enemy.visual}
                          />
                          {rankClass !== 'normal' && (
                            <span aria-hidden="true" className={`ecology-monster-rank is-${rankClass}`}>
                              {rankClass === 'boss' ? t('combat.nearby.markBoss') : t('combat.nearby.markElite')}
                            </span>
                          )}
                          <span className="ecology-monster-level">Lv.{enemy.level ?? 1}</span>
                        </button>
                      </MonsterTooltipTarget>
                    )
                  })}
                </div>
                <span className="dungeon-wave-hover-hint">{t('combat.dungeon.waveHoverHint')}</span>
              </div>
            </div>
          </section>

          {/* 通关独占奖励与战利品预览 */}
          <section className="dungeon-section dungeon-loot-section">
            <h3 className="dungeon-section-title">{t('combat.dungeon.lootHeading')}</h3>
            <div className="dungeon-clear-currencies">
              <div className="dungeon-currency-pill">
                <span>{t('combat.dungeon.clearExpLabel')}</span>
                <strong className="text-gold">+{formatInteger(clearRewards.experience)}</strong>
              </div>
              <div className="dungeon-currency-pill">
                <ItemIcon className="badge-item-icon" itemId="currency.spirit-stone" />
                <span>{t('combat.dungeon.clearStoneLabel')}</span>
                <strong className="text-jade">+{formatInteger(clearRewards.spiritStones)}</strong>
              </div>
            </div>
            <div className="dungeon-loot-list">
              {dungeon.clearLootTable.map((entry, idx) => {
                const preview = computeDungeonLootPreview(entry, difficulty)
                const itemDef = findItemInCatalog(items, entry.itemId)
                const eqDef = findEquipmentInCatalog(equipment, entry.itemId)
                const meta = resolveItemMetadata(entry.itemId, t)
                const tier = eqDef?.tier ?? itemDef?.tier ?? meta?.tier ?? 1
                const grade = resolveItemGrade(tier)
                const label = itemDef?.name ?? eqDef?.name ?? meta?.label ?? entry.itemId
                const category = resolveItemCategoryLabel(
                  resolveItemSubCategory(entry.itemId, itemDef, eqDef, meta?.category),
                  t,
                )
                const minDiffMeta = getDungeonDifficultyMeta(entry.minDifficulty)
                const minDiffLabel = t('combat.dungeon.fullDifficultyLabel', {
                  mode: t(`combat.dungeon.mode.${minDiffMeta.mode}`),
                  tier: t(`combat.dungeon.difficulty.${minDiffMeta.key}`),
                })
                const qtyText = preview.minQuantity === preview.maxQuantity
                  ? `×${preview.minQuantity}`
                  : `×${preview.minQuantity}~${preview.maxQuantity}`

                return (
                  <div
                    className={`dungeon-loot-row${preview.unlocked ? '' : ' is-locked'}`}
                    key={`${entry.itemId}-${idx}`}
                  >
                    <ItemTooltipTarget
                      definition={itemDef}
                      equipmentDefinition={eqDef}
                      itemId={entry.itemId}
                      metadata={meta}
                    >
                      <ItemTile
                        bottomRightLabel={qtyText}
                        category={category}
                        className="dungeon-loot-tile"
                        itemId={entry.itemId}
                        level={eqDef?.level ?? itemDef?.level ?? meta?.level}
                        name={label}
                        rarity={grade.rarity}
                      />
                    </ItemTooltipTarget>
                    <div className="dungeon-loot-info">
                      <div className="dungeon-loot-head">
                        <strong className="dungeon-loot-name">{label}</strong>
                        <span className={`dungeon-loot-tag is-${entry.kind}`}>
                          {entry.kind === 'guaranteed'
                            ? t('combat.dungeon.lootGuaranteedTag')
                            : t('combat.dungeon.lootRareTag')}
                        </span>
                      </div>
                      <div className="dungeon-loot-meta">
                        {!preview.unlocked ? (
                          <span className="dungeon-loot-locked-hint">
                            {t('combat.dungeon.lootLockedTag', { difficulty: minDiffLabel })}
                          </span>
                        ) : entry.kind === 'guaranteed' ? (
                          <span>{qtyText}</span>
                        ) : (
                          <span>
                            {t('combat.dungeon.lootChanceMeta', {
                              rate: formatLootChance(preview.effectiveChance),
                            })}
                          </span>
                        )}
                      </div>
                    </div>
                  </div>
                )
              })}
            </div>
            <p className="dungeon-rule-note">{t('combat.dungeon.ruleNote')}</p>
          </section>
        </div>

        {/* 底部操作区：挑战次数（次） + 单人立即开始/加入队列 + 组队入口 */}
        <footer className="dungeon-modal-footer">
          <div className="dungeon-repeat-row">
            <span className="dungeon-repeat-label">{t('combat.dungeon.repeatLabel')}</span>
            <QuantitySelector
              allowInfinity
              ariaLabel={t('combat.dungeon.repeatAria')}
              compact
              max={99}
              min={1}
              onChange={setRepeats}
              presets={[1, 3, 5]}
              value={repeats}
            />
            {repeats === -1 && (
              <span className="dungeon-repeat-hint">{t('combat.dungeon.repeatInfiniteHint')}</span>
            )}
          </div>

          <div className="dungeon-footer-actions">
            <button
              className="ghost-button dungeon-party-btn"
              onClick={handleGoParty}
              type="button"
            >
              <Icon name="social" size={14} />
              <span>{isPartyLeader ? t('combat.dungeon.syncPartyBtn') : t('combat.dungeon.goPartyBtn')}</span>
            </button>

            {queueLength > 0 && hasEnoughTili && atDungeonNode && (
              <button
                className="ghost-button dungeon-enter-btn"
                onClick={() => handleEnqueueSolo(false)}
                type="button"
              >
                <span>{t('app.action.enqueueAction', { n: queueLength + 1 })}</span>
              </button>
            )}

            <button
              className="confirm-primary dungeon-enter-btn"
              disabled={!hasEnoughTili || !atDungeonNode}
              onClick={() => handleEnqueueSolo(true)}
              type="button"
            >
              <Icon name="combat" size={14} />
              <span>
                {hasEnoughTili
                  ? t('combat.dungeon.startSoloBtn', { cost: energyCost })
                  : t('combat.dungeon.insufficientTiliBtn', { cost: energyCost, current: energy.current })}
              </span>
            </button>
          </div>
        </footer>
      </div>
    </Modal>
  )
})
