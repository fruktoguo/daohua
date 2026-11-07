import { create } from 'zustand'
import { requestJson } from '../network/apiClient'
import { gameClient } from '../network/gameClient'
import { useGameStore } from './gameStore'
import { withZongmenResidence } from './zongmenMap'

import type { ZongmenStateDto } from '../network/protocol'
import { chatService } from '../logging/chatService'

/** 宗门读投影独立于角色快照，命令仍通过现有权威命令通道提交。 */
export const useZongmenStore = create<ZongmenStateDto & {
  loading: boolean; pending: boolean; error: string
  refresh: (search?: string) => Promise<void>
  act: (action: string, payload?: Record<string, unknown>) => void
  reset: () => void
}>((set, get) => ({
  zongmen: null, directory: [], invitations: [], applications: [], loading: false, pending: false, error: '',
  refresh: async (search = '') => {
    if (get().loading) return
    const playerId = useGameStore.getState().playerId
    set({ loading: true })
    try {
      const state = await requestJson<ZongmenStateDto>(`/api/v1/zongmen?search=${encodeURIComponent(search)}`)
      if (playerId !== useGameStore.getState().playerId || useGameStore.getState().connection !== 'online') return
      if (get().zongmen?.zongmenId !== state.zongmen?.zongmenId) chatService.clearChannel('sect')
      set({ ...state, error: '' })
      const game = useGameStore.getState()
      game.setMaps(withZongmenResidence(game.maps, state.zongmen))
    } catch (error) {
      if (playerId === useGameStore.getState().playerId) set({ error: error instanceof Error ? error.message : String(error) })
    } finally { set({ loading: false }) }
  },
  act: (action, payload = {}) => {
    if (get().pending) return
    if (gameClient.sendCommand(`zongmen.${action}`, payload)) set({ pending: true })
  },
  reset: () => {
    set({ zongmen: null, directory: [], invitations: [], applications: [], loading: false, pending: false, error: '' })
    const game = useGameStore.getState()
    game.setMaps(withZongmenResidence(game.maps, null))
  },
}))
