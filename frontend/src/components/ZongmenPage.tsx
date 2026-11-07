import { useLogUnreadStore } from '../logging/logUnreadStore'
import { memo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { useZongmenStore } from '../game/zongmenStore'
import { useT } from '../i18n'
import { useUiStore } from '../uiStore'
import { gameClient } from '../network/gameClient'
import { ConfirmDialog } from './ConfirmDialog'
import { ItemReference } from './ItemDisplay'

/** 同道二级宗门页；表单草稿不随定时读投影更新而重建。 */
export const ZongmenPage = memo(function ZongmenPage(): React.JSX.Element {
  const t = useT()
  const { zongmen: own, directory, invitations, error, pending, loading, refresh, act } = useZongmenStore()
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const [name, setName] = useState('')
  const [prefix, setPrefix] = useState('')
  const [search, setSearch] = useState('')
  const [inviteName, setInviteName] = useState('')
  const [confirm, setConfirm] = useState<{ title: string; description: string; action: () => void } | null>(null)
  const nodes = maps.flatMap(map => map.nodes)
  const currentNodeId = world?.nodeId ?? ''
  const currentNode = nodes.find(node => node.nodeId === currentNodeId)
  const publicLocation = !!currentNode && !world?.edgeId && !currentNodeId.startsWith('node.zongmen.') && !currentNode.hidden
  const locationName = (id: string): string => nodes.find(node => node.nodeId === id)?.name ?? id
  const ask = (title: string, description: string, action: () => void): void => setConfirm({ title, description, action })
  const roleName = (role: number): string => t(`social.zongmen.role${role}`)
  const go = (): void => {
    if (!own) return
    gameClient.sendCommand('world.travel', { targetNodeId: currentNodeId === own.anchorNodeId || currentNodeId === own.nodeId ? own.nodeId : own.anchorNodeId })
    useUiStore.getState().setActiveTab('map')
  }

  return <div className="zongmen-page">
    {error && <div className="zongmen-error" role="alert">{error}<button onClick={() => void refresh()} type="button">{t('social.zongmen.refresh')}</button></div>}
    {own ? <>
      <div className="party-card">
        <div className="zongmen-heading"><h3>[{own.prefix}] {own.name}</h3><span>{roleName(own.myRole)}</span></div>
        <p>{t('social.zongmen.memberCount', { count: own.members.length, max: own.capacity })} · {t('social.zongmen.hallLevel', { level: own.hallLevel })}</p>
        <p>{t('social.zongmen.location', { name: locationName(own.anchorNodeId) })}</p>
        <div className="zongmen-actions">
          <button onClick={go} type="button">{t(currentNodeId === own.anchorNodeId || currentNodeId === own.nodeId ? 'social.zongmen.enter' : 'social.zongmen.goAnchor')}</button>
          <button onClick={() => { useLogUnreadStore.getState().setActiveGroup('sect'); useUiStore.getState().setActiveTab('log'); }} type="button">{t('social.zongmen.chat')}</button>
          {own.myRole === 0 ? <>
            <button disabled={pending || !publicLocation || currentNodeId === own.anchorNodeId} onClick={() => ask(t('social.zongmen.move'), t('social.zongmen.moveConfirm', { name: currentNode?.name ?? '' }), () => act('move', { anchorNodeId: currentNodeId }))} type="button">{t('social.zongmen.move')}</button>
            <button disabled={pending} onClick={() => ask(t('social.zongmen.disband'), t('social.zongmen.disbandConfirm', { name: own.name }), () => act('disband'))} type="button">{t('social.zongmen.disband')}</button>
          </> : <button disabled={pending} onClick={() => ask(t('social.zongmen.leave'), t('social.zongmen.leaveConfirm'), () => act('leave'))} type="button">{t('social.zongmen.leave')}</button>}
        </div>
        {own.myRole === 0 && <p className="party-card-desc"><ItemReference itemId="item.zongmen-lingpai" amount={1} /> {t('social.zongmen.moveCost')}</p>}
      </div>
      <div className="party-card">
        <h3>{t('social.zongmen.members')}</h3>
        <div className="zongmen-members">{own.members.map(member => <div className="zongmen-member" key={member.playerId}>
          <span><i className={member.online ? 'is-online' : ''} />{member.displayName}</span><span>{roleName(member.role)}</span>
          {own.myRole <= 3 && member.role > own.myRole && <div className="zongmen-actions">
            <select aria-label={t('social.zongmen.changeRole')} disabled={pending} value={member.role}
              onChange={event => act('role', { targetPlayerId: member.playerId, role: Number(event.target.value) })}>
              {[1, 2, 3, 4, 5, 6].filter(role => role > own.myRole && (role >= 4 || own.myRole === 0)).map(role => <option key={role} value={role}>{roleName(role)}</option>)}
            </select>
            {own.myRole === 0 && <button disabled={pending} type="button" onClick={() => ask(t('social.zongmen.transfer'), t('social.zongmen.transferConfirm', { name: member.displayName }), () => act('transfer', { targetPlayerId: member.playerId }))}>{t('social.zongmen.transfer')}</button>}
            <button disabled={pending} type="button" onClick={() => ask(t('social.zongmen.kick'), t('social.zongmen.kickConfirm', { name: member.displayName }), () => act('kick', { targetPlayerId: member.playerId }))}>{t('social.zongmen.kick')}</button>
          </div>}
        </div>)}</div>
      </div>
      {own.myRole <= 3 && <div className="party-card">
        <h3>{t('social.zongmen.recruit')}</h3>
        <form className="zongmen-actions" onSubmit={event => { event.preventDefault(); act('invite', { targetName: inviteName }); }}>
          <input aria-label={t('social.zongmen.playerName')} placeholder={t('social.zongmen.playerName')} maxLength={32} value={inviteName} onChange={event => setInviteName(event.target.value)} />
          <button disabled={pending || !inviteName.trim()} type="submit">{t('social.zongmen.invite')}</button>
        </form>
        {own.requests.filter(request => request.kind === 'application').map(request => <div className="zongmen-member" key={request.playerId}>
          <span>{request.displayName}</span><div className="zongmen-actions">
            <button disabled={pending} onClick={() => act('approve', { targetPlayerId: request.playerId })} type="button">{t('social.zongmen.approve')}</button>
            <button disabled={pending} onClick={() => act('reject', { targetPlayerId: request.playerId })} type="button">{t('social.zongmen.reject')}</button>
          </div>
        </div>)}
      </div>}
    </> : <>
      <div className="party-card">
        <h3>{t('social.zongmen.create')}</h3>
        <p className="party-card-desc"><ItemReference itemId="item.zongmen-lingpai" amount={1} /></p>
        <p className="party-card-desc">{t('social.zongmen.recipe')} <ItemReference itemId="currency.spirit-stone" amount={100000} /> <ItemReference itemId="item.refined-iron" amount={10000} /></p>
        <form className="zongmen-form" onSubmit={event => {
          event.preventDefault()
          const anchorNodeId = currentNodeId
          ask(t('social.zongmen.create'), t('social.zongmen.createConfirm', { name, prefix, location: currentNode?.name ?? '' }), () => act('create', { name, prefix, anchorNodeId }))
        }}>
          <label>{t('social.zongmen.name')}<input required minLength={2} maxLength={16} value={name} onChange={event => setName(event.target.value)} /></label>
          <label>{t('social.zongmen.prefix')}<input required maxLength={1} pattern="[\u4e00-\u9fff]" value={prefix} onChange={event => setPrefix(event.target.value)} /></label>
          <span>{publicLocation ? t('social.zongmen.location', { name: currentNode?.name ?? '' }) : t('social.zongmen.needLocation')}</span>
          <button disabled={pending || !publicLocation} type="submit">{t('social.zongmen.create')}</button>
        </form>
      </div>
      {invitations.length > 0 && <div className="party-card"><h3>{t('social.zongmen.invitations')}</h3>{invitations.map(invite => <div className="zongmen-member" key={invite.zongmenId}>
        <span>{invite.zongmenName}</span><div className="zongmen-actions">
          <button disabled={pending} onClick={() => act('accept', { zongmenId: invite.zongmenId })} type="button">{t('social.zongmen.accept')}</button>
          <button disabled={pending} onClick={() => act('decline', { zongmenId: invite.zongmenId })} type="button">{t('social.zongmen.reject')}</button>
        </div></div>)}</div>}
      <div className="party-card"><h3>{t('social.zongmen.directory')}</h3>
        <form className="zongmen-actions" onSubmit={event => { event.preventDefault(); void refresh(search) }}>
          <input aria-label={t('social.zongmen.search')} placeholder={t('social.zongmen.search')} value={search} onChange={event => setSearch(event.target.value)} maxLength={64} />
          <button disabled={loading} type="submit">{t('social.zongmen.search')}</button>
        </form>
        {directory.map(row => <div className="zongmen-member" key={row.zongmenId}><span>[{row.prefix}] {row.name}</span><span>{row.memberCount} / {row.capacity}</span>
          <button disabled={pending || row.memberCount >= row.capacity} onClick={() => act('apply', { zongmenId: row.zongmenId })} type="button">{t('social.zongmen.apply')}</button></div>)}
        {!loading && directory.length === 0 && <p className="party-card-desc">{t('social.zongmen.empty')}</p>}
      </div>
    </>}
    <ConfirmDialog open={confirm !== null} title={confirm?.title ?? ''} description={confirm?.description ?? ''} onConfirm={() => confirm?.action()} onClose={() => setConfirm(null)} />
  </div>
})
