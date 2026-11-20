import { useState } from 'react'
import { selectPartyLoadoutLocked, useSocialStore } from '../game/socialStore'
import { useT } from '../i18n'
import { ConfirmDialog } from './ConfirmDialog'

/** 同一组队会话内，各配装入口共用解锁状态。 */
export function PartyLoadoutLock(): React.JSX.Element | null {
  const t = useT()
  const sessionId = useSocialStore(state => state.party?.activeSession?.sessionId ?? null)
  const locked = useSocialStore(selectPartyLoadoutLocked)
  const unlock = useSocialStore(state => state.unlockPartyLoadout)
  const [pendingSessionId, setPendingSessionId] = useState<string | null>(null)

  if (sessionId === null) return null

  return (
    <div className="party-loadout-lock">
      <span role="status">{locked ? t('app.partyLoadout.locked') : t('app.partyLoadout.nextSession')}</span>
      {locked && (
        <button className="ghost-button" onClick={() => setPendingSessionId(sessionId)} type="button">
          {t('app.partyLoadout.unlock')}
        </button>
      )}
      <ConfirmDialog
        confirmLabel={t('app.partyLoadout.unlock')}
        description={t('app.partyLoadout.confirmDescription')}
        onClose={() => setPendingSessionId(null)}
        onConfirm={() => unlock(sessionId)}
        open={locked && pendingSessionId === sessionId}
        title={t('app.partyLoadout.confirmTitle')}
      />
    </div>
  )
}
