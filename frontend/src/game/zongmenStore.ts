import { create } from 'zustand'
import { requestJson } from '../network/apiClient'
import { gameClient } from '../network/gameClient'
import { useGameStore } from './gameStore'
import { withZongmenResidence } from './zongmenMap'

import type { ZongmenStateDto } from '../network/protocol'
import { chatService } from '../logging/chatService'

let refreshSeq = 0

/** 宗门读投影独立于角色快照，命令仍通过现有权威命令通道提交。 */
export const useZongmenStore = create<ZongmenStateDto & {
  loading: boolean; pending: boolean; error: string; searchQuery: string
  refresh: (search?: string) => Promise<void>
  act: (action: string, payload?: Record<string, unknown>) => void
  reset: () => void
}>((set, get) => ({
  zongmen: null, directory: [], invitations: [], applications: [], loading: false, pending: false, error: '', searchQuery: '',
  refresh: async (search) => {
    const playerId = useGameStore.getState().playerId
    const query = search ?? get().searchQuery
    const seq = ++refreshSeq
    set({ loading: true, ...(search !== undefined ? { searchQuery: query } : {}) })
    try {
      const state = await requestJson<ZongmenStateDto>(`/api/v1/zongmen?search=${encodeURIComponent(query)}`)
      if (seq !== refreshSeq) return
      if (playerId !== useGameStore.getState().playerId || useGameStore.getState().connection !== 'online') return
      if (get().zongmen?.zongmenId !== state.zongmen?.zongmenId) chatService.clearChannel('sect')
      set({ ...state, error: '' })
      const game = useGameStore.getState()
      game.setMaps(withZongmenResidence(game.maps, state.zongmen))
    } catch (error) {
      if (seq === refreshSeq && playerId === useGameStore.getState().playerId) set({ error: error instanceof Error ? error.message : String(error) })
    } finally { if (seq === refreshSeq) set({ loading: false }) }
  },
  act: (action, payload = {}) => {
    if (get().pending) return
    if (gameClient.sendCommand(`zongmen.${action}`, payload)) set({ pending: true })
  },
  reset: () => {
    refreshSeq += 1
    set({ zongmen: null, directory: [], invitations: [], applications: [], loading: false, pending: false, error: '', searchQuery: '' })
    const game = useGameStore.getState()
    game.setMaps(withZongmenResidence(game.maps, null))
  },
}))

if (typeof window !== 'undefined') {
  window.addEventListener('daohua:zongmen-result', () => {
    const state = useZongmenStore.getState()
    if (state.pending) useZongmenStore.setState({ pending: false })
    if (useGameStore.getState().connection === 'online') void state.refresh()
  })
}
