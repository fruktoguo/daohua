import { create } from 'zustand'
import type { ChatQuoteDto } from '../network/protocol'

export const MAX_CHAT_BODY_LENGTH = 200
export const EMPTY_CHAT_DRAFT = { body: '', quote: null } as const
export interface ChatDraft {
  body: string
  quote: ChatQuoteDto | null
}

interface ChatDraftState {
  playerId: string | null
  drafts: ReadonlyMap<string, ChatDraft>
  focusKey: string | null
  activate: (playerId: string | null) => void
  edit: (key: string, update: (draft: ChatDraft) => ChatDraft) => void
  append: (key: string, text: string) => boolean
  requestFocus: (key: string) => void
  consumeFocus: (key: string) => void
}

export const useChatDraftStore = create<ChatDraftState>((set, get) => ({
  playerId: null,
  drafts: new Map(),
  focusKey: null,
  activate: playerId => {
    if (get().playerId !== playerId) set({ playerId, drafts: new Map(), focusKey: null })
  },
  edit: (key, update) => {
    set(state => {
      const previous = state.drafts.get(key) ?? EMPTY_CHAT_DRAFT
      const next = update(previous)
      if (next.body === previous.body && next.quote === previous.quote) return state
      const drafts = new Map(state.drafts)
      if (next.body || next.quote) drafts.set(key, next)
      else drafts.delete(key)
      return { drafts }
    })
  },
  append: (key, text) => {
    const current = get().drafts.get(key)?.body.trim() ?? ''
    const body = current ? `${current} ${text}` : text
    if (body.length > MAX_CHAT_BODY_LENGTH) return false
    get().edit(key, draft => ({ ...draft, body }))
    return true
  },
  requestFocus: focusKey => set({ focusKey }),
  consumeFocus: key => { if (get().focusKey === key) set({ focusKey: null }) },
}))
