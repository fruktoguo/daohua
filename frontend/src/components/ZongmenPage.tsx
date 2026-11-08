import { useLogUnreadStore } from '../logging/logUnreadStore'
import { memo, useMemo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { useZongmenStore } from '../game/zongmenStore'
import { useT } from '../i18n'
import { useUiStore } from '../uiStore'
import { gameClient } from '../network/gameClient'
import { resolveBuildingIconUrl } from '../game/buildingIcons'
import { ConfirmDialog } from './ConfirmDialog'
import { ItemReference } from './ItemDisplay'
import { Modal } from './Modal'
import type { ZongmenDto } from '../network/protocol'

/** 开宗立派独立弹窗：输入宗门名称与单字字号，提示令牌消耗、炼器配方与挂靠地点。 */
const ZongmenCreateModal = memo(function ZongmenCreateModal({
  open,
  onClose,
  currentNodeName,
  publicLocation,
  onRequestCreate,
}: {
  open: boolean
  onClose: () => void
  currentNodeName: string
  publicLocation: boolean
  onRequestCreate: (name: string, prefix: string) => void
}): React.JSX.Element | null {
  const t = useT()
  const pending = useZongmenStore(state => state.pending)
  const [name, setName] = useState('')
  const [prefix, setPrefix] = useState('')

  if (!open) return null

  const handleSubmit = (event: React.FormEvent): void => {
    event.preventDefault()
    if (!publicLocation || pending || !name.trim() || !prefix.trim()) return
    onRequestCreate(name.trim(), prefix.trim())
  }

  return (
    <Modal labelledBy="zongmen-create-modal-title" onClose={onClose} open={open} size="medium">
      <div className="zongmen-modal-body">
        <h3 id="zongmen-create-modal-title">{t('social.zongmen.createModalTitle')}</h3>
        <div className="zongmen-modal-costs">
          <p className="party-card-desc">
            <span className="zongmen-cost-label">{t('social.zongmen.createTokenCost')}</span>
            <ItemReference amount={1} itemId="item.zongmen-lingpai" />
          </p>
          <p className="party-card-desc">
            <span className="zongmen-cost-label">{t('social.zongmen.recipe')}</span>
            <ItemReference amount={100000} itemId="currency.spirit-stone" />{' '}
            <ItemReference amount={10000} itemId="item.refined-iron" />
          </p>
          <p className={`party-card-desc${publicLocation ? '' : ' is-warning'}`}>
            <span className="zongmen-cost-label">{t('social.zongmen.createLocationLabel')}</span>
            <strong>{publicLocation ? currentNodeName : t('social.zongmen.needLocation')}</strong>
          </p>
        </div>

        <form className="zongmen-form" onSubmit={handleSubmit}>
          <label>
            <span>{t('social.zongmen.name')}</span>
            <input
              maxLength={16}
              minLength={2}
              onChange={event => setName(event.target.value)}
              placeholder={t('social.zongmen.name')}
              required
              value={name}
            />
          </label>
          <label>
            <span>{t('social.zongmen.prefix')}</span>
            <input
              maxLength={1}
              onChange={event => setPrefix(event.target.value)}
              pattern="[\u4e00-\u9fff]"
              placeholder={t('social.zongmen.prefix')}
              required
              value={prefix}
            />
          </label>
          <div className="zongmen-modal-actions">
            <button onClick={onClose} type="button">{t('social.zongmen.cancel')}</button>
            <button
              className="confirm-primary"
              disabled={pending || !publicLocation || !name.trim() || !prefix.trim()}
              type="submit"
            >
              {t('social.zongmen.createBtn')}
            </button>
          </div>
        </form>
      </div>
    </Modal>
  )
})

/** 在宗视图子页面类型 */
type ZongmenSubTab = 'overview' | 'members' | 'applications' | 'buildings'

/** 子页面 1：宗门全貌概览 */
const ZongmenOverviewSection = memo(function ZongmenOverviewSection({
  own,
  currentNodeId,
  currentNodeName,
  publicLocation,
  locationName,
  ask,
}: {
  own: ZongmenDto
  currentNodeId: string
  currentNodeName: string
  publicLocation: boolean
  locationName: (id: string) => string
  ask: (title: string, description: string, action: () => void) => void
}): React.JSX.Element {
  const t = useT()
  const { pending, act } = useZongmenStore()

  const handleTravel = (): void => {
    gameClient.sendCommand('world.travel', {
      targetNodeId: currentNodeId === own.anchorNodeId || currentNodeId === own.nodeId ? own.nodeId : own.anchorNodeId,
    })
    useUiStore.getState().setActiveTab('map')
  }

  const handleOpenChat = (): void => {
    useLogUnreadStore.getState().setActiveGroup('sect')
    useUiStore.getState().setActiveTab('log')
  }

  return (
    <div className="party-card">
      <div className="zongmen-overview-header">
        <div className="zongmen-heading">
          <h3>[{own.prefix}] {own.name}</h3>
          <span className="zongmen-badge-role">{t(`social.zongmen.role${own.myRole}`)}</span>
        </div>
      </div>
      <p className="zongmen-intro">{t('social.zongmen.sectIntro')}</p>

      <div className="zongmen-stats-grid">
        <div className="zongmen-stat-box">
          <span className="zongmen-stat-label">{t('social.zongmen.prefix')}</span>
          <span className="zongmen-stat-value">[{own.prefix}]</span>
        </div>
        <div className="zongmen-stat-box">
          <span className="zongmen-stat-label">{t('social.zongmen.hallBuildingName')}</span>
          <span className="zongmen-stat-value">{t('social.zongmen.hallLevel', { level: own.hallLevel })}</span>
        </div>
        <div className="zongmen-stat-box">
          <span className="zongmen-stat-label">{t('social.zongmen.scale')}</span>
          <span className="zongmen-stat-value">{own.members.length} / {own.capacity} 人</span>
        </div>
        <div className="zongmen-stat-box">
          <span className="zongmen-stat-label">{t('social.zongmen.createLocationLabel')}</span>
          <span className="zongmen-stat-value">{locationName(own.anchorNodeId)}</span>
        </div>
      </div>

      <div className="zongmen-quick-actions">
        <button className="confirm-primary" onClick={handleTravel} type="button">
          {t(currentNodeId === own.anchorNodeId || currentNodeId === own.nodeId ? 'social.zongmen.enter' : 'social.zongmen.goAnchor')}
        </button>
        <button onClick={handleOpenChat} type="button">
          {t('social.zongmen.chat')}
        </button>
      </div>

      <div className="zongmen-manage-box">
        <span className="zongmen-manage-box-title">{t('social.zongmen.overviewHeading')}</span>
        <div className="zongmen-actions">
          {own.myRole === 0 ? (
            <>
              <button
                disabled={pending || !publicLocation || currentNodeId === own.anchorNodeId}
                onClick={() =>
                  ask(
                    t('social.zongmen.move'),
                    t('social.zongmen.moveConfirm', { name: currentNodeName }),
                    () => act('move', { anchorNodeId: currentNodeId }),
                  )
                }
                type="button"
              >
                {t('social.zongmen.move')}
              </button>
              <button
                disabled={pending}
                onClick={() =>
                  ask(
                    t('social.zongmen.disband'),
                    t('social.zongmen.disbandConfirm', { name: own.name }),
                    () => act('disband'),
                  )
                }
                type="button"
              >
                {t('social.zongmen.disband')}
              </button>
            </>
          ) : (
            <button
              disabled={pending}
              onClick={() => ask(t('social.zongmen.leave'), t('social.zongmen.leaveConfirm'), () => act('leave'))}
              type="button"
            >
              {t('social.zongmen.leave')}
            </button>
          )}
        </div>
        {own.myRole === 0 && (
          <p className="party-card-desc">
            <ItemReference amount={1} itemId="item.zongmen-lingpai" /> {t('social.zongmen.moveCost')}
          </p>
        )}
      </div>
    </div>
  )
})

/** 子页面 2：同门弟子花名册与成员管理 */
const ZongmenMembersSection = memo(function ZongmenMembersSection({
  own,
  ask,
}: {
  own: ZongmenDto
  ask: (title: string, description: string, action: () => void) => void
}): React.JSX.Element {
  const t = useT()
  const { pending, act } = useZongmenStore()
  const onlineCount = useMemo(() => own.members.filter(m => m.online).length, [own.members])

  return (
    <div className="party-card">
      <div className="zongmen-heading">
        <h3>{t('social.zongmen.members')}</h3>
        <p className="party-card-desc">
          {t('social.zongmen.memberStats', { count: own.members.length, online: onlineCount, max: own.capacity })}
        </p>
      </div>

      <div className="zongmen-members">
        {own.members.map(member => (
          <div className="zongmen-member" key={member.playerId}>
            <span>
              <i className={member.online ? 'is-online' : ''} />
              {member.displayName}
            </span>
            <span>{t(`social.zongmen.role${member.role}`)}</span>
            {own.myRole <= 3 && member.role > own.myRole && (
              <div className="zongmen-actions">
                <select
                  aria-label={t('social.zongmen.changeRole')}
                  disabled={pending}
                  onChange={event =>
                    act('role', { targetPlayerId: member.playerId, role: Number(event.target.value) })
                  }
                  value={member.role}
                >
                  {[1, 2, 3, 4, 5, 6]
                    .filter(role => role > own.myRole && (role >= 4 || own.myRole === 0))
                    .map(role => (
                      <option key={role} value={role}>{t(`social.zongmen.role${role}`)}</option>
                    ))}
                </select>
                {own.myRole === 0 && (
                  <button
                    disabled={pending}
                    onClick={() =>
                      ask(
                        t('social.zongmen.transfer'),
                        t('social.zongmen.transferConfirm', { name: member.displayName }),
                        () => act('transfer', { targetPlayerId: member.playerId }),
                      )
                    }
                    type="button"
                  >
                    {t('social.zongmen.transfer')}
                  </button>
                )}
                <button
                  disabled={pending}
                  onClick={() =>
                    ask(
                      t('social.zongmen.kick'),
                      t('social.zongmen.kickConfirm', { name: member.displayName }),
                      () => act('kick', { targetPlayerId: member.playerId }),
                    )
                  }
                  type="button"
                >
                  {t('social.zongmen.kick')}
                </button>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
})

/** 子页面 3：拜帖与招募管理（仅管理权限长老及以上可见） */
const ZongmenApplicationsSection = memo(function ZongmenApplicationsSection({
  own,
}: {
  own: ZongmenDto
}): React.JSX.Element {
  const t = useT()
  const { pending, act } = useZongmenStore()
  const [inviteName, setInviteName] = useState('')
  const applications = useMemo(() => own.requests.filter(r => r.kind === 'application'), [own.requests])

  return (
    <>
      <div className="party-card">
        <h3>{t('social.zongmen.recruit')}</h3>
        <form
          className="zongmen-actions"
          onSubmit={event => {
            event.preventDefault()
            act('invite', { targetName: inviteName })
          }}
        >
          <input
            aria-label={t('social.zongmen.playerName')}
            maxLength={32}
            onChange={event => setInviteName(event.target.value)}
            placeholder={t('social.zongmen.playerName')}
            value={inviteName}
          />
          <button disabled={pending || !inviteName.trim()} type="submit">{t('social.zongmen.invite')}</button>
        </form>
      </div>

      <div className="party-card">
        <h3>{t('social.zongmen.tabApplications')}</h3>
        {applications.length === 0 ? (
          <p className="party-card-desc">{t('social.zongmen.noPendingApplications')}</p>
        ) : (
          <div className="zongmen-members">
            {applications.map(request => (
              <div className="zongmen-member" key={request.playerId}>
                <span>{request.displayName}</span>
                <div className="zongmen-actions">
                  <button
                    className="confirm-primary"
                    disabled={pending}
                    onClick={() => act('approve', { targetPlayerId: request.playerId })}
                    type="button"
                  >
                    {t('social.zongmen.approve')}
                  </button>
                  <button
                    disabled={pending}
                    onClick={() => act('reject', { targetPlayerId: request.playerId })}
                    type="button"
                  >
                    {t('social.zongmen.reject')}
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </>
  )
})

/** 子页面 4：宗门驻地建筑与大殿建设（完全对齐地图 NearbyPanel 建筑卡片、效果与详情标准） */
const ZongmenBuildingsSection = memo(function ZongmenBuildingsSection({
  own,
  currentNodeId,
}: {
  own: ZongmenDto
  currentNodeId: string
}): React.JSX.Element {
  const t = useT()
  const world = useGameStore(state => state.world)
  const buildingCatalog = useGameStore(state => state.buildings)
  const [detailOpen, setDetailOpen] = useState(false)
  const atResidence = currentNodeId === own.nodeId

  const hallDef = buildingCatalog.find(d => d.buildingId === 'building.zongmen-dadian')
  const hallBuilding = (world.nodeBuildings ?? []).find(b => b.entityId === 'building.zongmen-dadian')
  const effectiveLevel = hallBuilding?.level ?? own.hallLevel
  const isUpgrading = (hallBuilding?.targetLevel ?? 0) > effectiveLevel
  const targetLevel = isUpgrading ? (hallBuilding?.targetLevel ?? effectiveLevel + 1) : effectiveLevel + 1
  const iconUrl = resolveBuildingIconUrl('building.zongmen-dadian')
  const buildingName = hallDef?.name ?? t('social.zongmen.hallBuildingName')
  const buildingEffect = hallDef?.effect ?? t('social.zongmen.hallBonusDesc')

  const handleTravelToResidence = (): void => {
    gameClient.sendCommand('world.travel', { targetNodeId: own.nodeId })
    useUiStore.getState().setActiveTab('map')
  }

  const handleUpgrade = (): void => {
    gameClient.sendCommand('building.upgrade', { buildingId: 'building.zongmen-dadian' })
    setDetailOpen(false)
  }

  return (
    <>
      <div className="party-card">
        <div className="zongmen-heading">
          <h3>{t('social.zongmen.tabBuildings')}</h3>
          {!atResidence && (
            <button className="confirm-primary" onClick={handleTravelToResidence} type="button">
              {t('social.zongmen.goResidence')}
            </button>
          )}
        </div>

        <div className="nearby-grid" style={{ marginTop: '12px' }}>
          <button
            className="nearby-card is-building is-player-building"
            onClick={() => setDetailOpen(true)}
            type="button"
          >
            {isUpgrading && (
              <span className="building-corner-badge is-upgrading">
                {t('combat.nearby.buildingUpgradingShort')}
              </span>
            )}
            <span className="nearby-mark">
              {iconUrl ? (
                <img
                  alt={buildingName}
                  className="nearby-building-thumb"
                  loading="lazy"
                  src={iconUrl}
                />
              ) : (
                '🏛️'
              )}
            </span>
            <div>
              <div className="nearby-monster-title">
                <strong>{buildingName}</strong>
                <span className="nearby-level-badge">
                  {isUpgrading
                    ? `${t('combat.nearby.buildingLevelValue', { level: effectiveLevel })} → Lv.${targetLevel}`
                    : t('combat.nearby.buildingLevelValue', { level: effectiveLevel })}
                </span>
              </div>
              <small>{buildingEffect}</small>
              <small className="building-usage">
                <span>{t('social.zongmen.memberCount', { count: own.members.length, max: own.capacity })}</span>
                <span className="building-usage-idle">
                  {t('combat.nearby.buildingEfficiencyFull')}
                </span>
              </small>
            </div>
          </button>
        </div>

        <div className="zongmen-facility-box">
          <h4>{t('social.zongmen.residenceFacilityTitle')}</h4>
          <p className="party-card-desc">{t('social.zongmen.residenceFacilityDesc')}</p>
          <div className="zongmen-actions" style={{ marginTop: '8px' }}>
            {atResidence ? (
              <span className="zongmen-tag-here">{t('social.zongmen.currentAtResidence')}</span>
            ) : (
              <button onClick={handleTravelToResidence} type="button">
                {t('social.zongmen.goResidence')}
              </button>
            )}
          </div>
        </div>
      </div>

      {detailOpen && (
        <Modal
          className="building-detail-modal"
          labelledBy="building-detail-title"
          onClose={() => setDetailOpen(false)}
          open
          size="medium"
        >
          <header className="build-picker-header">
            <h2 id="building-detail-title" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              {iconUrl ? (
                <img
                  alt={buildingName}
                  className="nearby-building-thumb"
                  src={iconUrl}
                  style={{ width: 28, height: 28, objectFit: 'contain' }}
                />
              ) : (
                <span>🏛️</span>
              )}
              <span>{buildingName}</span>
              <span className="nearby-level-badge">
                {t('combat.nearby.buildingLevelValue', { level: effectiveLevel })}
              </span>
            </h2>
          </header>

          <div className="building-detail-body">
            <section className="building-section-card">
              <h4 className="building-section-title">{t('combat.nearby.buildingSectionInfo')}</h4>
              <p className="building-desc">{hallDef?.description ?? ''}</p>
              <div className="building-bonuses-current">
                <span className="building-bonus-title">{t('combat.nearby.buildingCurrentBonus')}：</span>
                <span className="building-bonus-pill pill-tone--jade">
                  <span className="capsule-label">{t('social.zongmen.scale')}</span>
                  <span className="underlined-value">{own.capacity} 人</span>
                </span>
              </div>
            </section>

            <section className="building-section-card">
              <div className="building-section-header-row">
                <h4 className="building-section-title">{t('combat.nearby.buildingSectionUpgrade')}</h4>
                {isUpgrading && (
                  <span className="building-tag-upgrading">
                    {t('combat.nearby.buildingUpgrading', { level: targetLevel })}
                  </span>
                )}
              </div>

              <div className="building-upgrade-box">
                <div className="breakthrough-modal-realm building-upgrade-level-jump">
                  <span>Lv.{effectiveLevel}</span>
                  <span aria-hidden="true" className="breakthrough-modal-realm-arrow">→</span>
                  <strong>Lv.{targetLevel}</strong>
                </div>

                <ul className="breakthrough-modal-attrs building-upgrade-attrs">
                  <li className="breakthrough-modal-row pill-tone--jade">
                    <span className="breakthrough-modal-row-label">{t('social.zongmen.scale')}</span>
                    <span className="breakthrough-modal-row-values">
                      <span>{own.capacity} 人</span>
                      <span className="breakthrough-modal-arrow">→</span>
                      <strong>{own.capacity + 5} 人</strong>
                    </span>
                  </li>
                </ul>
              </div>

              <div className="zongmen-upgrade-section">
                <h5>{t('social.zongmen.hallUpgradeReq')}</h5>
                <p className="party-card-desc">{t('social.zongmen.hallUpgradeCost')}</p>
                <p className="party-card-desc">
                  <ItemReference amount={10000} itemId="item.refined-iron" />{' '}
                  <ItemReference amount={10000} itemId="item.refined-copper" />
                </p>
              </div>

              <div className="building-upgrade-action-row" style={{ marginTop: '14px' }}>
                {atResidence ? (
                  <button
                    className="building-action-btn"
                    onClick={handleUpgrade}
                    type="button"
                  >
                    {t('combat.nearby.buildingUpgrade')}
                  </button>
                ) : (
                  <button
                    className="building-action-btn"
                    onClick={handleTravelToResidence}
                    type="button"
                  >
                    {t('social.zongmen.goResidence')}
                  </button>
                )}
              </div>
            </section>
          </div>
        </Modal>
      )}
    </>
  )
})

/** 在宗视图主控制器：根据角色权限动态呈现 3~4 个二级 Tab 导航（靠左通用排布，消除居中与冗余顶栏）。 */
const ZongmenView = memo(function ZongmenView({
  own,
  currentNodeId,
  currentNodeName,
  publicLocation,
  locationName,
  ask,
}: {
  own: ZongmenDto
  currentNodeId: string
  currentNodeName: string
  publicLocation: boolean
  locationName: (id: string) => string
  ask: (title: string, description: string, action: () => void) => void
}): React.JSX.Element {
  const t = useT()
  const canManage = own.myRole <= 3
  const [activeTab, setActiveTab] = useState<ZongmenSubTab>('overview')

  // 根据角色权限动态计算可见 Tab 列表（仅长老及以上 myRole <= 3 展示「拜帖」）
  const visibleTabs = useMemo(() => {
    const tabs: Array<{ id: ZongmenSubTab; labelKey: string }> = [
      { id: 'overview', labelKey: 'social.zongmen.tabOverview' },
      { id: 'members', labelKey: 'social.zongmen.tabMembers' },
    ]
    if (canManage) {
      tabs.push({ id: 'applications', labelKey: 'social.zongmen.tabApplications' })
    }
    tabs.push({ id: 'buildings', labelKey: 'social.zongmen.tabBuildings' })
    return tabs
  }, [canManage])

  // 兜底回退：若当前 Tab 因权限变化不可见，自动回退到概览
  const currentTab = visibleTabs.some(tab => tab.id === activeTab) ? activeTab : 'overview'

  return (
    <>
      <header className="zongmen-header">
        <nav aria-label={t('social.zongmen.tabAria')} className="zongmen-sub-tabs" role="tablist">
          {visibleTabs.map(tab => (
            <button
              aria-selected={currentTab === tab.id}
              className={currentTab === tab.id ? 'active' : ''}
              key={tab.id}
              onClick={() => setActiveTab(tab.id)}
              role="tab"
              type="button"
            >
              {t(tab.labelKey)}
            </button>
          ))}
        </nav>
      </header>

      {currentTab === 'overview' && (
        <ZongmenOverviewSection
          ask={ask}
          currentNodeId={currentNodeId}
          currentNodeName={currentNodeName}
          locationName={locationName}
          own={own}
          publicLocation={publicLocation}
        />
      )}

      {currentTab === 'members' && (
        <ZongmenMembersSection
          ask={ask}
          own={own}
        />
      )}

      {currentTab === 'applications' && canManage && (
        <ZongmenApplicationsSection own={own} />
      )}

      {currentTab === 'buildings' && (
        <ZongmenBuildingsSection
          currentNodeId={currentNodeId}
          own={own}
        />
      )}
    </>
  )
})

/** 散修未入宗视图：顶部提供开宗立派入口，主区呈现当前地点附近仙门名录并支持递交拜帖。 */
const ZongmenlessView = memo(function ZongmenlessView({
  currentNodeId,
  currentNodeName,
  publicLocation,
  locationName,
  ask,
}: {
  currentNodeId: string
  currentNodeName: string
  publicLocation: boolean
  locationName: (id: string) => string
  ask: (title: string, description: string, action: () => void) => void
}): React.JSX.Element {
  const t = useT()
  const { directory, invitations, applications, pending, loading, refresh, act } = useZongmenStore()
  const maps = useGameStore(state => state.maps)
  const [createModalOpen, setCreateModalOpen] = useState(false)
  const [search, setSearch] = useState('')

  // 节点与区域所属映射表，用于附近地点判定
  const nodeToMap = useMemo(() => {
    const map = new Map<string, string>()
    for (const m of maps) {
      for (const n of m.nodes) {
        map.set(n.nodeId, m.mapId)
      }
    }
    return map
  }, [maps])

  const currentMapId = nodeToMap.get(currentNodeId)

  // 附近宗门排序：挂靠当前节点 > 同区域地图 > 其他节点
  const sortedDirectory = useMemo(() => {
    const list = [...directory]
    list.sort((a, b) => {
      const aHere = a.anchorNodeId === currentNodeId ? 2 : currentMapId && nodeToMap.get(a.anchorNodeId) === currentMapId ? 1 : 0
      const bHere = b.anchorNodeId === currentNodeId ? 2 : currentMapId && nodeToMap.get(b.anchorNodeId) === currentMapId ? 1 : 0
      if (aHere !== bHere) return bHere - aHere
      return a.name.localeCompare(b.name, 'zh-CN')
    })
    return list
  }, [directory, currentNodeId, currentMapId, nodeToMap])

  const handleRequestCreate = (name: string, prefix: string): void => {
    setCreateModalOpen(false)
    ask(
      t('social.zongmen.create'),
      t('social.zongmen.createConfirm', { name, prefix, location: currentNodeName }),
      () => act('create', { name, prefix, anchorNodeId: currentNodeId }),
    )
  }

  return (
    <>
      <div className="party-card zongmen-create-banner">
        <div className="zongmen-create-info">
          <h3>{t('social.zongmen.createModalTitle')}</h3>
          <p className="party-card-desc">{t('social.zongmen.createDesc')}</p>
        </div>
        <button
          className="confirm-primary zongmen-create-btn"
          onClick={() => setCreateModalOpen(true)}
          type="button"
        >
          {t('social.zongmen.createBtn')}
        </button>
      </div>

      {invitations.length > 0 && (
        <div className="party-card">
          <h3>{t('social.zongmen.invitations')}</h3>
          {invitations.map(invite => (
            <div className="zongmen-member" key={invite.zongmenId}>
              <span>{invite.zongmenName}</span>
              <div className="zongmen-actions">
                <button disabled={pending} onClick={() => act('accept', { zongmenId: invite.zongmenId })} type="button">
                  {t('social.zongmen.accept')}
                </button>
                <button disabled={pending} onClick={() => act('decline', { zongmenId: invite.zongmenId })} type="button">
                  {t('social.zongmen.reject')}
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      <div className="party-card">
        <div className="zongmen-heading">
          <div>
            <h3>{t('social.zongmen.nearby')}</h3>
            {currentNodeName && (
              <p className="party-card-desc">{t('social.zongmen.location', { name: currentNodeName })}</p>
            )}
          </div>
          <form className="zongmen-actions" onSubmit={event => { event.preventDefault(); void refresh(search) }}>
            <input
              aria-label={t('social.zongmen.search')}
              maxLength={64}
              onChange={event => setSearch(event.target.value)}
              placeholder={t('social.zongmen.search')}
              value={search}
            />
            <button disabled={loading} type="submit">{t('social.zongmen.search')}</button>
          </form>
        </div>

        <div className="zongmen-members">
          {sortedDirectory.map(row => {
            const isHere = row.anchorNodeId === currentNodeId
            const applied = applications.some(app => app.zongmenId === row.zongmenId)
            const full = row.memberCount >= row.capacity

            return (
              <div className="zongmen-member" key={row.zongmenId}>
                <div className="zongmen-member-identity">
                  <span className="zongmen-member-title">[{row.prefix}] {row.name}</span>
                  <span className="zongmen-member-loc">
                    {isHere && <span className="zongmen-tag-here">{t('social.zongmen.currentLocationTag')}</span>}
                    <span className="zongmen-loc-name">{locationName(row.anchorNodeId)}</span>
                  </span>
                </div>
                <span className="zongmen-member-cap">{row.memberCount} / {row.capacity}</span>
                <div className="zongmen-actions">
                  {applied ? (
                    <button className="zongmen-applied-btn" disabled type="button">
                      {t('social.zongmen.letterSubmitted')}
                    </button>
                  ) : full ? (
                    <button disabled type="button">{t('social.zongmen.fullCapacity')}</button>
                  ) : (
                    <button
                      className="confirm-primary"
                      disabled={pending}
                      onClick={() => act('apply', { zongmenId: row.zongmenId })}
                      type="button"
                    >
                      {t('social.zongmen.submitLetter')}
                    </button>
                  )}
                </div>
              </div>
            )
          })}
          {!loading && sortedDirectory.length === 0 && (
            <p className="party-card-desc">{t('social.zongmen.empty')}</p>
          )}
        </div>
      </div>

      <ZongmenCreateModal
        currentNodeName={currentNodeName}
        onClose={() => setCreateModalOpen(false)}
        onRequestCreate={handleRequestCreate}
        open={createModalOpen}
        publicLocation={publicLocation}
      />
    </>
  )
})

/** 同道二级宗门页：根据玩家在宗/散修状态分流展示，支持独立立宗弹窗与附近拜帖递交。 */
export const ZongmenPage = memo(function ZongmenPage(): React.JSX.Element {
  const t = useT()
  const { zongmen: own, error, refresh } = useZongmenStore()
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const [confirm, setConfirm] = useState<{ title: string; description: string; action: () => void } | null>(null)

  const nodes = useMemo(() => maps.flatMap(map => map.nodes), [maps])
  const currentNodeId = world?.nodeId ?? ''
  const currentNode = nodes.find(node => node.nodeId === currentNodeId)
  const publicLocation = !!currentNode && !world?.edgeId && !currentNodeId.startsWith('node.zongmen.') && !currentNode.hidden
  const locationName = (id: string): string => nodes.find(node => node.nodeId === id)?.name ?? id
  const currentNodeName = currentNode?.name ?? locationName(currentNodeId)
  const ask = (title: string, description: string, action: () => void): void => setConfirm({ title, description, action })

  return (
    <div className="zongmen-page">
      {error && (
        <div className="zongmen-error" role="alert">
          {error}
          <button onClick={() => void refresh()} type="button">{t('social.zongmen.refresh')}</button>
        </div>
      )}
      {own ? (
        <ZongmenView
          ask={ask}
          currentNodeId={currentNodeId}
          currentNodeName={currentNodeName}
          locationName={locationName}
          own={own}
          publicLocation={publicLocation}
        />
      ) : (
        <ZongmenlessView
          ask={ask}
          currentNodeId={currentNodeId}
          currentNodeName={currentNodeName}
          locationName={locationName}
          publicLocation={publicLocation}
        />
      )}
      <ConfirmDialog
        description={confirm?.description ?? ''}
        onClose={() => setConfirm(null)}
        onConfirm={() => confirm?.action()}
        open={confirm !== null}
        title={confirm?.title ?? ''}
      />
    </div>
  )
})
