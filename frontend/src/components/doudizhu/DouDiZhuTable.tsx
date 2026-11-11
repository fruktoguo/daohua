import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent } from 'react'
import { useT } from '../../i18n'
import { formatNumber } from '../../game/format'
import { resolvePlayerActorId } from '../../game/avatarStore'
import { useSocialStore } from '../../game/socialStore'
import type { DouDiZhuGameDto, PartyDto } from '../../network/protocol'
import { CharacterAvatar } from '../CharacterAvatar'
import { ItemIcon } from '../ItemDisplay'
import { FloatingPopover } from '../FloatingPopover'
import { Modal } from '../Modal'
import { cardFace, cardName, isRed, visualSymbols, type JokerLabels } from './doudizhuArt'
import { beats, evaluate, legalMoves, sortCards, type PatternKind } from './doudizhuRules'
import { DouDiZhuMotion } from './doudizhuMotion'
import { handPosition, measureHand } from './doudizhuLayout'
import './doudizhu.css'

interface TableProps {
  game: DouDiZhuGameDto
  party: PartyDto
  mySeat: number
  isLeader: boolean
  canStart: boolean
  onBid: (action: 'call' | 'rob' | 'pass') => boolean
  onPlay: (cards: number[]) => boolean
  onPass: () => boolean
  onStart: () => boolean
  onEnd: () => void
  onSetHosted: (hosted: boolean) => boolean
}
const PROPS = ['tomato', 'flower', 'tea', 'applause'] as const
const PHRASES = ['praise', 'hurry', 'stay', 'partner'] as const
const clampCount = (count: number): number => Math.max(0, Math.min(17, count))
const patternKey = (kind: PatternKind): string => `social.doudizhu.pattern.${kind}`
const cardClass = (card: number): string => `ddz-card${isRed(card) ? ' ddz-red' : ''}${card >= 52 ? ' ddz-joker' : ''}`
const Card = memo(function Card({ card, jokerLabels, className = '', style }: { card: number; jokerLabels: JokerLabels; className?: string; style?: CSSProperties }): React.JSX.Element {
  return <span className={`${cardClass(card)} ${className}`} aria-label={cardName(card, jokerLabels)} style={style} dangerouslySetInnerHTML={{ __html: cardFace(card, jokerLabels) }} />
})

/** 真正的牌桌仅消费私有投影和公开记录；动画、提示与选牌不修改游戏资产。 */
export const DouDiZhuTable = memo(function DouDiZhuTable({ game, party, mySeat, isLeader, canStart, onBid, onPlay, onPass, onStart, onEnd, onSetHosted }: TableProps): React.JSX.Element {
  const t = useT()
  const jokerLabels = useMemo<JokerLabels>(() => ({
    small: t('social.doudizhu.jokerSmall'),
    big: t('social.doudizhu.jokerBig'),
  }), [t])
  const [now, setNow] = useState(Date.now)
  const [selected, setSelected] = useState<number[]>([])
  const [hovered, setHovered] = useState(-1)
  const [pending, setPending] = useState<number | null>(null)
  const [notice, setNotice] = useState('')
  const [rulesOpen, setRulesOpen] = useState(false)
  const [soundOn, setSoundOn] = useState(false)
  const [fullscreen, setFullscreen] = useState(false)
  const [fallbackFullscreen, setFallbackFullscreen] = useState(false)
  const [clockSync, setClockSync] = useState(() => ({
    gameId: game.gameId, version: game.version,
    serverAt: game.serverNowUnixMilliseconds, clientAt: Date.now(),
  }))
  const [portrait, setPortrait] = useState(() => window.matchMedia('(orientation: portrait)').matches)
  const [layout, setLayout] = useState(() => measureHand(550, 480, portrait))
  const arenaRef = useRef<HTMLDivElement>(null), effectRef = useRef<HTMLDivElement>(null), stackRef = useRef<HTMLDivElement>(null)
  const shellRef = useRef<HTMLElement>(null)
  const handAreaRef = useRef<HTMLDivElement>(null), handRef = useRef<HTMLDivElement>(null)
  const motion = useRef<DouDiZhuMotion | null>(null), hintCursor = useRef(0)
  const drag = useRef<{ start: number; end: number; value: boolean; before: number[] } | null>(null)
  const beforePositions = useRef(new Map<number, { left: number; top: number }>())
  const cardNodes = useRef(new Map<number, HTMLButtonElement>())
  const reflowAnimations = useRef(new Map<number, Animation>())
  const previousGeometry = useRef('')
  const seenInteractions = useRef(new Set<string>())
  const lastInteractionAt = useRef(0)
  const audio = useRef<AudioContext | null>(null)
  const soundEnabled = useRef(soundOn); soundEnabled.current = soundOn
  const interactionEvents = useSocialStore(state => state.douDiZhuInteractions)
  const interact = useSocialStore(state => state.interactDouDiZhu)
  const selfHosted = game.hostedSeats[mySeat]
  const active = game.phase === 'bidding' || game.phase === 'playing'
  const clockMatchesGame = clockSync.gameId === game.gameId && clockSync.version === game.version
  const clockOffset = clockMatchesGame ? clockSync.serverAt - clockSync.clientAt : game.serverNowUnixMilliseconds - now
  const serverNow = now + clockOffset
  const serverNowRef = useRef(serverNow); serverNowRef.current = serverNow
  const dealAge = serverNow - game.dealStartedAtUnixMilliseconds
  const intro = dealAge >= 0 && dealAge < 8250
  const revealAge = game.landlordConfirmedAtUnixMilliseconds === null ? Infinity : serverNow - game.landlordConfirmedAtUnixMilliseconds
  const revealing = revealAge >= 0 && revealAge < 2400
  const ownBase = sortCards(game.hand.filter(card => !game.bottomCards.includes(card)))
  const landedCount = (seat: number): number => clampCount(Math.floor((dealAge - 2790 - seat * 68 - (seat === mySeat ? 570 : 480)) / 204) + 1)
  const introHand = game.dealHand.slice(0, landedCount(mySeat))
  const receivedBottom = game.bottomCards.filter((_, i) => revealAge >= 1170 + i * 190)
  const hand = intro ? dealAge >= 7650 ? sortCards(introHand) : introHand
    : revealing && game.landlordSeat === mySeat ? revealAge >= 1730 ? sortCards(game.hand) : [...ownBase, ...receivedBottom] : sortCards(game.hand)
  const handKey = hand.join(',')
  const authoritativeHandKey = game.hand.join(',')
  const reserved = intro ? 17 : revealing && game.landlordSeat === mySeat ? 20 : hand.length
  const currentSlots = useRef({ layout, reserved }); currentSlots.current = { layout, reserved }
  const canOperate = active && serverNow >= game.actionAvailableAtUnixMilliseconds && !intro && !revealing && pending !== game.version
  const isMyTurn = canOperate && game.currentSeat === mySeat && !selfHosted
  const canSelect = !intro && !revealing && active && !selfHosted
  const lastPattern = useMemo(() => evaluate(game.currentTrickCards), [game.currentTrickCards])
  const pickedPattern = useMemo(() => evaluate(selected), [selected])
  const validSelection = pickedPattern !== null && beats(pickedPattern, lastPattern)
  const moves = useMemo(() => legalMoves(game.hand, lastPattern).sort((a, b) => {
    if (a.cards.length === game.hand.length) return -1
    if (b.cards.length === game.hand.length) return 1
    return (['bomb', 'rocket'].includes(a.pattern.kind) ? 1 : 0) - (['bomb', 'rocket'].includes(b.pattern.kind) ? 1 : 0)
      || (lastPattern ? a.pattern.primary - b.pattern.primary : b.cards.length - a.cards.length || a.pattern.primary - b.pattern.primary)
  }), [game.hand, lastPattern])
  const seconds = Math.max(0, Math.ceil((game.turnDeadlineAtUnixMilliseconds - serverNow) / 1000))
  const isFullscreen = fullscreen || fallbackFullscreen
  const multiplier = game.phase === 'finished' ? game.finalMultiplier : 2 ** (game.robCount + game.bombCount)
  const name = (seat: number): string => party.members.find(member => member.playerId === game.playerIds[seat])!.displayName
  const allPlays = game.history
  let trickStart = 0
  for (let i = 1; i < allPlays.length; i++) if (allPlays[i].isPass && allPlays[i - 1].isPass) trickStart = i + 1
  const plays = allPlays.slice(trickStart)
  const latestPlay = allPlays.at(-1), latestBid = game.bids.at(-1)

  function sound(type: 'deal' | 'play' | 'bomb'): void {
    if (!soundEnabled.current) return
    if (!audio.current) audio.current = new AudioContext()
    void audio.current.resume()
    const oscillator = audio.current.createOscillator(), gain = audio.current.createGain(), start = audio.current.currentTime
    oscillator.type = type === 'bomb' ? 'triangle' : 'sine'
    oscillator.frequency.setValueAtTime(type === 'deal' ? 600 : type === 'bomb' ? 110 : 380, start)
    oscillator.frequency.exponentialRampToValueAtTime(type === 'bomb' ? 40 : 220, start + .08)
    gain.gain.setValueAtTime(.035, start); gain.gain.exponentialRampToValueAtTime(.001, start + .11)
    oscillator.connect(gain); gain.connect(audio.current.destination); oscillator.start(); oscillator.stop(start + .12)
  }
  function handSlot(index: number): { left: number; top: number; width: number; height: number } {
    const r = handRef.current!.getBoundingClientRect(), { layout: slots, reserved: count } = currentSlots.current
    const position = handPosition(index, count, slots), scaleX = r.width / slots.total, scaleY = r.height / slots.extent
    return { left: r.left + position.left * scaleX, top: r.top + position.top * scaleY, width: slots.width * scaleX, height: slots.height * scaleY }
  }

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 50)
    return () => window.clearInterval(timer)
  }, [])
  useLayoutEffect(() => {
    setClockSync({ gameId: game.gameId, version: game.version,
      serverAt: game.serverNowUnixMilliseconds, clientAt: Date.now() })
  }, [game.gameId, game.version, game.serverNowUnixMilliseconds])
  useEffect(() => {
    const onFullscreenChange = (): void => setFullscreen(document.fullscreenElement === shellRef.current)
    document.addEventListener('fullscreenchange', onFullscreenChange)
    return () => document.removeEventListener('fullscreenchange', onFullscreenChange)
  }, [])
  useEffect(() => () => { void audio.current?.close() }, [])
  useEffect(() => {
    const media = window.matchMedia('(orientation: portrait)'), change = (): void => setPortrait(media.matches)
    media.addEventListener('change', change)
    return () => media.removeEventListener('change', change)
  }, [])
  useEffect(() => () => { reflowAnimations.current.forEach(animation => animation.cancel()) }, [])
  useEffect(() => {
    setSelected([]); setHovered(-1); setPending(null); hintCursor.current = 0
    beforePositions.current.clear()
  }, [game.gameId, game.dealNumber])
  useEffect(() => {
    setSelected(current => current.filter(card => game.hand.includes(card)))
    setHovered(-1); drag.current = null; hintCursor.current = 0; setPending(null)
  }, [authoritativeHandKey, game.version])
  useEffect(() => {
    if (pending === null) return
    const timer = window.setTimeout(() => setPending(null), 1600)
    return () => window.clearTimeout(timer)
  }, [pending])
  useEffect(() => {
    if (!notice) return
    const timer = window.setTimeout(() => setNotice(''), 1800)
    return () => window.clearTimeout(timer)
  }, [notice])
  useLayoutEffect(() => {
    const area = handAreaRef.current!
    const measure = (): void => {
      const next = measureHand(area.clientWidth, arenaRef.current!.clientHeight, portrait)
      setLayout(current => current.total === next.total && current.width === next.width && current.portrait === next.portrait ? current : next)
    }
    measure(); const observer = new ResizeObserver(measure); observer.observe(area); observer.observe(arenaRef.current!)
    return () => observer.disconnect()
  }, [portrait])
  useLayoutEffect(() => {
    // 保存未插值的槽位；有新理牌时从当前可见位置接续，不能把上次动画误认成新布局。
    const geometry = `${game.gameId}:${game.dealNumber}:${layout.portrait}:${layout.width}:${layout.total}`
    const positions = new Map<number, { left: number; top: number }>()
    reflowAnimations.current.forEach((animation, card) => {
      if (!hand.includes(card)) { animation.cancel(); reflowAnimations.current.delete(card) }
    })
    hand.forEach((card, index) => {
      const node = cardNodes.current.get(card)!, next = handPosition(index, reserved, layout), before = beforePositions.current.get(card)
      const running = reflowAnimations.current.get(card), translate = running ? getComputedStyle(node).translate : 'none'
      const offset = translate === 'none' ? [0, 0] : translate.split(' ').map(Number.parseFloat)
      running?.cancel(); reflowAnimations.current.delete(card)
      if (before && geometry === previousGeometry.current) {
        const dx = before.left - next.left + offset[0], dy = before.top - next.top + (offset[1] ?? 0)
        if (Math.abs(dx) + Math.abs(dy) > .5) {
          const animation = node.animate([{ translate: `${dx}px ${dy}px` }, { translate: '0px 0px' }], { duration: 230, easing: 'cubic-bezier(.2,.75,.25,1)' })
          reflowAnimations.current.set(card, animation)
          animation.onfinish = () => { if (reflowAnimations.current.get(card) === animation) reflowAnimations.current.delete(card) }
        }
      }
      positions.set(card, next)
    })
    beforePositions.current = positions; previousGeometry.current = geometry
  }, [handKey, reserved, layout, game.gameId, game.dealNumber])
  useEffect(() => {
    const animator = new DouDiZhuMotion(arenaRef.current!, effectRef.current!, () => { setNow(Date.now()); sound('deal') }, jokerLabels)
    motion.current = animator
    const started = game.dealStartedAtUnixMilliseconds - clockOffset
    if (Date.now() - started < 8250) {
      animator.shuffle(stackRef.current!, started)
      if (Date.now() - started >= 2600) stackRef.current!.innerHTML = '<i class="ddz-card-back"></i>'.repeat(4)
      for (let i = 0; i < 51; i++) animator.schedule(started + 2790 + i * 68, elapsed => {
        const seat = i % 3, card = seat === mySeat ? game.dealHand[Math.floor(i / 3)] : null
        const source = stackRef.current!.getBoundingClientRect()
        const destination = seat === mySeat ? handSlot(Math.floor(i / 3)) : arenaRef.current!.querySelector<HTMLElement>(`[data-seat="${seat}"] .ddz-mini-back`)!.getBoundingClientRect()
        animator.fly(seat, card, source, destination, seat === mySeat ? 570 : 480, elapsed)
        const cards = [...stackRef.current!.children] as HTMLElement[]
        cards.forEach((el, index) => { el.style.visibility = index < Math.max(3, Math.ceil((54 - i) / 54 * cards.length)) ? 'visible' : 'hidden' })
      })
      for (let i = 0; i < 3; i++) animator.schedule(started + 6770 + i * 145, elapsed => {
        const destination = arenaRef.current!.querySelectorAll<HTMLElement>('.ddz-bottom-slot')[i].getBoundingClientRect()
        animator.fly(-1, null, stackRef.current!.getBoundingClientRect(), destination, 430, elapsed)
      })
    }
    return () => { animator.dispose(); motion.current = null }
    // 一副牌只预约一次；普通社交推送和回合版本变化不能重播洗牌。
  }, [game.gameId, game.dealNumber, mySeat, jokerLabels])
  useEffect(() => {
    const serverAt = game.landlordConfirmedAtUnixMilliseconds, animator = motion.current
    if (serverAt === null || !animator) return
    const at = serverAt - clockOffset
    if (Date.now() - at >= 2400) return
    for (let i = 0; i < 3; i++) animator.schedule(at + 600 + i * 190, elapsed => {
      const source = arenaRef.current!.querySelectorAll<HTMLElement>('.ddz-bottom-slot')[i].getBoundingClientRect()
      const seat = game.landlordSeat!
      const destination = seat === mySeat ? handSlot(17 + i) : arenaRef.current!.querySelector<HTMLElement>(`[data-seat="${seat}"] .ddz-mini-back`)!.getBoundingClientRect()
      animator.fly(seat, seat === mySeat ? game.bottomCards[i] : null, source, destination, seat === mySeat ? 570 : 480, elapsed, true)
    })
  }, [game.gameId, game.landlordConfirmedAtUnixMilliseconds])
  useEffect(() => {
    const animator = motion.current
    if (!animator) return
    for (const event of interactionEvents) {
      if (event.gameId !== game.gameId || seenInteractions.current.has(event.id)) continue
      seenInteractions.current.add(event.id)
      const elapsed = serverNowRef.current - event.createdAtUnixMilliseconds
      if (elapsed >= 4000) continue
      const sender = game.playerIds.indexOf(event.senderId), target = game.playerIds.indexOf(event.targetId)
      if (sender < 0 || target < 0) continue
      if (PROPS.some(prop => prop === event.kind)) animator.prop(sender, target, event.kind, elapsed)
      else if (PHRASES.some(phrase => phrase === event.kind)) animator.bubble(sender, t(`social.doudizhu.phrase.${event.kind}`))
    }
    if (seenInteractions.current.size > 100) seenInteractions.current = new Set(interactionEvents.map(event => event.id))
  }, [interactionEvents, game.gameId, game.playerIds, t])
  const feedbackKey = `${latestPlay?.version}:${latestBid?.version}:${game.phase}`
  const lastFeedback = useRef(feedbackKey)
  useEffect(() => {
    if (lastFeedback.current === feedbackKey) return
    lastFeedback.current = feedbackKey
    if (game.phase === 'finished') { setNotice(t(game.landlordWon ? 'social.doudizhu.landlordWon' : 'social.doudizhu.farmerWon')); return }
    if (latestPlay && game.phase === 'playing') {
      const pattern = evaluate(latestPlay.cards)
      if (pattern && ['bomb', 'rocket', 'straight', 'pairRun', 'plane', 'planeSingle', 'planePair'].includes(pattern.kind)) {
        setNotice(t(patternKey(pattern.kind))); sound(pattern.kind === 'bomb' || pattern.kind === 'rocket' ? 'bomb' : 'play')
      }
    } else if (latestBid) {
      const calledBefore = game.bids.some(bid => bid.version < latestBid.version && bid.action !== 'pass')
      setNotice(`${name(latestBid.seat)} · ${t(`social.doudizhu.${latestBid.action === 'pass' ? calledBefore ? 'robPass' : 'callPass' : latestBid.action}`)}`)
    }
  }, [feedbackKey])

  function hint(): void {
    if (!isMyTurn || game.phase !== 'playing') return
    if (!moves.length) { setSelected([]); setNotice(t('social.doudizhu.noHint')); return }
    setSelected(moves[hintCursor.current++ % moves.length].cards)
  }
  function submit(send: () => boolean): void {
    if (send()) { setPending(game.version); sound('play') }
    else setNotice(t('social.doudizhu.sendFailed'))
  }
  function sendInteraction(targetId: string, interaction: string, close: () => void): void {
    const localNow = Date.now()
    if (localNow - lastInteractionAt.current < 1100) {
      setNotice(t('social.doudizhu.interactionRateLimited'))
      return
    }
    if (!interact(game, targetId, interaction)) {
      setNotice(t('social.doudizhu.interactionUnavailable'))
      return
    }
    lastInteractionAt.current = localNow
    close()
  }
  async function toggleFullscreen(): Promise<void> {
    const shell = shellRef.current
    if (!shell) return
    if (fallbackFullscreen) { setFallbackFullscreen(false); return }
    if (document.fullscreenElement === shell) {
      try { await document.exitFullscreen() }
      catch { setNotice(t('social.doudizhu.fullscreenUnavailable')) }
      return
    }
    if (!shell.requestFullscreen) { setFallbackFullscreen(true); return }
    try { await shell.requestFullscreen() }
    catch { setFallbackFullscreen(true) }
  }
  function hit(x: number, y: number): number {
    // 以实际可见卡片命中，同时覆盖多排、选中抬起和理牌过程中的位置。
    for (let i = hand.length - 1; i >= 0; i--) {
      const r = cardNodes.current.get(hand[i])!.getBoundingClientRect()
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) return i
    }
    return -1
  }
  function selectAt(index: number, before: number[], value: boolean): void {
    const next = new Set(before); if (value) next.add(hand[index]); else next.delete(hand[index]); setSelected([...next])
  }
  function pointerDown(event: PointerEvent<HTMLDivElement>): void {
    if (!canSelect || event.button !== 0) return
    const index = hit(event.clientX, event.clientY); if (index < 0) return
    event.preventDefault(); event.currentTarget.setPointerCapture(event.pointerId)
    drag.current = { start: index, end: index, value: !selected.includes(hand[index]), before: selected }
    selectAt(index, selected, drag.current.value)
  }
  function pointerMove(event: PointerEvent<HTMLDivElement>): void {
    if (!canSelect) return
    const index = hit(event.clientX, event.clientY); setHovered(event.pointerType === 'mouse' && index >= 0 ? hand[index] : -1)
    const selection = drag.current
    if (!selection || index < 0 || index === selection.end) return
    selection.end = index; const next = new Set(selection.before)
    for (let i = Math.min(index, selection.start); i <= Math.max(index, selection.start); i++) {
      if (selection.value) next.add(hand[i]); else next.delete(hand[i])
    }
    setSelected([...next])
  }
  function pointerUp(event: PointerEvent<HTMLDivElement>): void {
    drag.current = null
    if (event.pointerType !== 'mouse') setHovered(-1)
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
  }
  const handStyle = { '--w': `${layout.width}px`, '--h': `${layout.height}px`, width: layout.total, height: layout.extent } as CSSProperties
  const phaseText = intro ? t(dealAge < 2790 ? 'social.doudizhu.shuffling' : dealAge < 7650 ? 'social.doudizhu.dealing' : 'social.doudizhu.sorting')
    : revealing ? t('social.doudizhu.revealing') : t(`social.doudizhu.phase.${game.phase}`)
  const deckCaption = dealAge < 800 ? 'split' : dealAge < 1740 ? 'riffle' : dealAge < 2380 ? 'bridge' : dealAge < 2790 ? 'square' : dealAge < 6670 ? 'dealing' : dealAge < 7490 ? 'bottomCards' : 'sorting'

  function renderPlay(seat: number): React.JSX.Element | null {
    const play = [...plays].reverse().find(entry => entry.seat === seat)
    if (!play) return null
    const pattern = evaluate(play.cards)
    return <div className="ddz-play" key={play.version} aria-live="polite">
      {play.isPass ? <span className="ddz-pass">{t('social.doudizhu.pass')}</span> : <>
        <div className="ddz-played-cards" style={{ '--count': play.cards.length, width: `calc(var(--w) + ${play.cards.length - 1} * var(--step))` } as CSSProperties}>
          {sortCards(play.cards).map((card, i) => <Card card={card} jokerLabels={jokerLabels} key={card} style={{ '--i': i } as CSSProperties} />)}
        </div>
        <span className={`ddz-play-label${pattern && ['bomb', 'rocket'].includes(pattern.kind) ? ' is-bomb' : ''}`}>{pattern ? t(patternKey(pattern.kind)) : ''}</span>
      </>}
    </div>
  }
  function renderSeat(seat: number): React.JSX.Element {
    const member = party.members.find(entry => entry.playerId === game.playerIds[seat])!
    const own = seat === mySeat, position = own ? 'self' : (seat - mySeat + 3) % 3 === 1 ? 'left' : 'right'
    const lastBid = [...game.bids].reverse().find(bid => bid.seat === seat)
    const earlierCall = lastBid && game.bids.some(bid => bid.version < lastBid.version && bid.action !== 'pass')
    const bidText = lastBid ? t(`social.doudizhu.${lastBid.action === 'pass' ? earlierCall ? 'robPass' : 'callPass' : lastBid.action}`) : ''
    const portrait = <CharacterAvatar actorId={resolvePlayerActorId(member.avatar)} className="ddz-avatar" isFlipped={own || position === 'right'} name={member.displayName} role="player" />
    return <article className={`ddz-seat ddz-seat-${position}${canOperate && game.currentSeat === seat ? ' is-turn' : ''}`} data-seat={seat} key={member.playerId}>
      <div className="ddz-profile">
        <strong className="ddz-player-name">{member.displayName}</strong>
        <span className={`ddz-role${game.landlordSeat === seat ? ' is-landlord' : ''}`}>{game.landlordSeat === null ? '　' : t(game.landlordSeat === seat ? 'social.doudizhu.landlord' : 'social.doudizhu.farmer')}</span>
        <div className="ddz-avatar-row">
          {own ? portrait : <FloatingPopover trigger="click" triggerAs="button" triggerClassName="ddz-avatar-trigger" placement={position === 'left' ? 'right-start' : 'left-start'} size="auto" className="ddz-social-menu" content={({ close }) => <>
            <div className="ddz-interaction-header">{t('social.doudizhu.interactWith', { name: member.displayName })}</div>
            <div className="ddz-prop-grid">{PROPS.map(prop => <button className="ddz-prop-button" key={prop} type="button" onClick={() => sendInteraction(member.playerId, prop, close)}><svg className="ddz-prop-art" viewBox="0 0 64 64"><use href={`#ddz-prop-${prop}`} /></svg><span>{t(`social.doudizhu.prop.${prop}`)}</span></button>)}</div>
            <div className="ddz-quick-phrases">{PHRASES.map(phrase => <button className="ddz-phrase-button" key={phrase} type="button" onClick={() => sendInteraction(member.playerId, phrase, close)}>{t(`social.doudizhu.phrase.${phrase}`)}</button>)}</div>
          </>}>{portrait}<span className="ddz-interact-label">{t('social.doudizhu.interact')}</span></FloatingPopover>}
          {!own && <span className="ddz-hand-counter" aria-label={t('social.doudizhu.cardsLeft', { count: game.handsRemaining[seat] })}><i className="ddz-mini-back" /><b>{intro ? landedCount(seat) : revealing && game.landlordSeat === seat ? 17 + game.bottomCards.filter((_, i) => revealAge >= 1080 + i * 190).length : game.handsRemaining[seat]}</b></span>}
        </div>
        <div className="ddz-wallet" aria-label={`${member.displayName} ${formatNumber(member.spiritStones)}`}><ItemIcon itemId="currency.spirit-stone" /><span>{formatNumber(member.spiritStones)}</span></div>
        <span className="ddz-seat-status">{game.hostedSeats[seat] ? t('social.doudizhu.hosted') : canOperate && game.currentSeat === seat ? t('social.doudizhu.turn') : '　'}</span>
      </div>
      {!own && <div className="ddz-opponent-play">{renderPlay(seat)}</div>}
      {lastBid && (game.phase === 'bidding' || revealing) && <div className="ddz-bid-bubble" key={lastBid.version}>{bidText}{lastBid.action === 'rob' && <small>×2</small>}</div>}
    </article>
  }

  return <section ref={node => { shellRef.current = node }} className={`ddz-shell${portrait ? ' is-portrait' : ''}${fallbackFullscreen ? ' is-fullscreen' : ''}`}>
    <svg className="ddz-symbols" aria-hidden="true"><defs dangerouslySetInnerHTML={{ __html: visualSymbols }} /></svg>
    <header className="ddz-header"><div className="ddz-brand"><strong>{t('social.doudizhu.title')}</strong><span>{phaseText}</span></div>
      <div className="ddz-stats"><span>{t('social.doudizhu.baseStakeValue', { stake: formatNumber(game.baseStake) })}</span><b>×{multiplier}</b>
        <button type="button" onClick={() => setRulesOpen(true)}>{t('social.doudizhu.rules')}</button>
        <button type="button" aria-pressed={soundOn} onClick={() => setSoundOn(value => !value)}>{t(soundOn ? 'social.doudizhu.soundOn' : 'social.doudizhu.soundOff')}</button>
        {active && <button type="button" className={selfHosted ? 'is-on' : ''} onClick={() => onSetHosted(!selfHosted)}>{t(selfHosted ? 'social.doudizhu.cancelHost' : 'social.doudizhu.host')}</button>}
        <button type="button" className="ddz-fullscreen-button" aria-label={t(isFullscreen ? 'social.doudizhu.exitFullscreen' : 'social.doudizhu.fullscreen')} title={t(isFullscreen ? 'social.doudizhu.exitFullscreen' : 'social.doudizhu.fullscreen')} onClick={() => { void toggleFullscreen() }}>
          {isFullscreen ? <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3v5H3M16 3v5h5M8 21v-5H3m13 5v-5h5" /></svg> : <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 8V3h5M21 8V3h-5M3 16v5h5m13-5v5h-5" /></svg>}
        </button>
      </div>
    </header>
    <div className="ddz-table" style={{ '--hand-zone': `${layout.extent}px` } as CSSProperties} ref={arenaRef}>
      <div className="ddz-table-watermark" aria-hidden="true">{t('social.doudizhu.title')}</div>
      <div className="ddz-bottom-cards" aria-label={t('social.doudizhu.bottomCards')}>
        {[0, 1, 2].map(i => <span className={`ddz-bottom-slot${intro && dealAge < 7200 + i * 145 ? ' is-waiting' : ''}`} key={i}>
          {game.bottomCards[i] !== undefined ? <Card card={game.bottomCards[i]} jokerLabels={jokerLabels} className={revealing ? 'ddz-flip-bottom' : ''} /> : <i className="ddz-card-back" />}
        </span>)}
      </div>
      {game.playerIds.map((_, seat) => renderSeat(seat))}
      <div className="ddz-deck-scene" hidden={!intro}>
        <div className="ddz-deck-shadow" /><div className="ddz-deck-stack" ref={stackRef} />
        <div className="ddz-deck-caption">{phaseText}<small>{t(`social.doudizhu.${deckCaption}`)}</small></div>
      </div>
      <div className="ddz-self-play">{renderPlay(mySeat)}</div>
      <div className="ddz-turn-status" aria-live="polite">{!intro && !revealing && active && <>
        <strong>{!canOperate ? t('social.doudizhu.waitAnimation') : isMyTurn ? t(game.phase === 'bidding' ? 'social.doudizhu.bidPrompt' : 'social.doudizhu.playPrompt') : t('social.doudizhu.waitTurn', { name: name(game.currentSeat) })}</strong>
        {canOperate && <span className={seconds <= 5 ? 'is-urgent' : ''}>{game.hostedSeats[game.currentSeat] ? t('social.doudizhu.hostingNow') : t('social.doudizhu.turnCountdown', { seconds })}</span>}
      </>}</div>
      <div className="ddz-hand-area" style={{ height: layout.extent }} ref={handAreaRef}>
        <div className="ddz-controls" style={{ bottom: layout.extent + 8 }}>
          {isMyTurn && game.phase === 'bidding' && <><button className="ddz-primary" type="button" onClick={() => submit(() => onBid(game.landlordSeat === null ? 'call' : 'rob'))}>{t(game.landlordSeat === null ? 'social.doudizhu.call' : 'social.doudizhu.rob')}</button><button type="button" onClick={() => submit(() => onBid('pass'))}>{t(game.landlordSeat === null ? 'social.doudizhu.callPass' : 'social.doudizhu.robPass')}</button></>}
          {isMyTurn && game.phase === 'playing' && <><button className="ddz-primary" type="button" disabled={!validSelection} onClick={() => submit(() => onPlay(selected))}>{t('social.doudizhu.play')}</button><button type="button" onClick={hint}>{t('social.doudizhu.hint')}</button>{lastPattern && <button type="button" onClick={() => submit(onPass)}>{t('social.doudizhu.pass')}</button>}<button type="button" disabled={!selected.length} onClick={() => setSelected([])}>{t('social.doudizhu.clear')}</button></>}
          {selfHosted && active && <button type="button" onClick={() => onSetHosted(false)}>{t('social.doudizhu.cancelHost')}</button>}
        </div>
        <div className={`ddz-selection-note${selected.length && !validSelection ? ' is-error' : ''}`}>{!intro && !revealing && (selected.length ? pickedPattern ? `${t(patternKey(pickedPattern.kind))}${validSelection ? '' : ` · ${t('social.doudizhu.cannotBeat')}`}` : t('social.doudizhu.invalidCards') : canSelect ? t('social.doudizhu.selectHint') : '')}</div>
        <div className="ddz-hand" ref={handRef} style={handStyle} onPointerDown={pointerDown} onPointerMove={pointerMove} onPointerUp={pointerUp} onPointerCancel={pointerUp} onPointerLeave={() => { if (!drag.current) setHovered(-1) }} onKeyDown={event => {
          if (!canSelect) return
          if (event.key.toLowerCase() === 'h') { event.preventDefault(); hint() }
          else if (event.key === 'Escape') setSelected([])
          else if (event.key === 'Enter' && isMyTurn && game.phase === 'playing' && validSelection) { event.preventDefault(); submit(() => onPlay(selected)) }
          else if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
            event.preventDefault(); const cards = [...event.currentTarget.querySelectorAll<HTMLButtonElement>('[data-card]')], index = cards.indexOf(document.activeElement as HTMLButtonElement)
            const columns = layout.portrait ? Math.max(1, Math.ceil(reserved / 3)) : hand.length
            const delta = event.key === 'ArrowLeft' ? -1 : event.key === 'ArrowRight' ? 1 : event.key === 'ArrowUp' ? -columns : columns
            cards[Math.max(0, Math.min(cards.length - 1, index + delta))]?.focus()
          }
        }}>
          {hand.map((card, index) => <button key={card} ref={node => { if (node) cardNodes.current.set(card, node); else cardNodes.current.delete(card) }} data-card={card} type="button" aria-label={cardName(card, jokerLabels)} aria-pressed={selected.includes(card)} disabled={!canSelect} className={`${cardClass(card)} ddz-hand-card${selected.includes(card) ? ' ddz-selected' : ''}${hovered === card ? ' ddz-hovered' : ''}`} style={{ '--i': index, width: layout.width, height: layout.height, ...handPosition(index, reserved, layout) } as CSSProperties} tabIndex={index === 0 ? 0 : -1} onClick={event => { if (event.detail === 0 && canSelect) selectAt(index, selected, !selected.includes(card)) }} dangerouslySetInnerHTML={{ __html: cardFace(card, jokerLabels) }} />)}
        </div>
      </div>
      <div className="ddz-effects" ref={effectRef} aria-hidden="true" />
      {notice && <div className="ddz-announcement" key={notice} aria-live="polite"><strong>{notice}</strong>{game.phase === 'finished' && game.spring && <span>{t(game.landlordWon ? 'social.doudizhu.spring' : 'social.doudizhu.antiSpring')}</span>}</div>}
      {game.phase === 'finished' && <div className="ddz-result-layer"><section className="ddz-result">
        <span>{t(game.landlordWon ? 'social.doudizhu.landlordWon' : 'social.doudizhu.farmerWon')}</span>
        <h2>{t((game.landlordSeat === mySeat) === game.landlordWon ? 'social.doudizhu.youWon' : 'social.doudizhu.youLost')}</h2>
        {game.spring && <strong>{t(game.landlordWon ? 'social.doudizhu.spring' : 'social.doudizhu.antiSpring')}</strong>}
        <div className="ddz-result-summary">{t('social.doudizhu.baseStakeValue', { stake: formatNumber(game.baseStake) })} · ×{game.finalMultiplier}</div>
        {game.playerIds.map((id, seat) => { const member = party.members.find(entry => entry.playerId === id)!; return <div className="ddz-result-row" key={id}><CharacterAvatar actorId={resolvePlayerActorId(member.avatar)} role="player" name={member.displayName} /><span>{member.displayName}<small>{t(seat === game.landlordSeat ? 'social.doudizhu.landlord' : 'social.doudizhu.farmer')}</small></span><b className={game.settlementDeltas[seat] < 0 ? 'is-negative' : ''}>{game.settlementDeltas[seat] >= 0 ? '+' : '−'}{formatNumber(Math.abs(game.settlementDeltas[seat]))}</b></div> })}
        <div className="ddz-result-buttons"><button type="button" onClick={onEnd}>{t('social.doudizhu.end')}</button><button className="ddz-primary" type="button" disabled={!canStart} onClick={onStart}>{t('social.doudizhu.playAgain')}</button></div>
        {!isLeader && <small>{t('social.doudizhu.waitLeader')}</small>}
      </section></div>}
    </div>
    <Modal open={rulesOpen} onClose={() => setRulesOpen(false)} labelledBy="ddz-rules-title" size="small"><h2 id="ddz-rules-title">{t('social.doudizhu.rules')}</h2><p>{t('social.doudizhu.rulesText')}</p></Modal>
  </section>
})
