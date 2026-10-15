import { memo, useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { ItemReference } from '../components/ItemDisplay'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { gameClient } from '../network/gameClient'
import { useUiStore } from '../uiStore'
import { HOME_NODE_ID, TUTORIAL_DONE, TUTORIAL_MILESTONE_COUNT, TUTORIAL_MILESTONES, type TutorialPrepare, type TutorialUiStep } from './steps'

/**
 * 新手引导遮罩层（总纲 §22.4）：里程碑由服务端 settings.tutorialStep 权威下发，
 * 本组件只负责把当前里程碑渲染成 daojie 风格的遮罩 + 高亮 + 气泡卡，
 * 并通过预备动作驱动真实 UI（切页签、展开技艺菜单）使目标元素现身。
 */

/** 高亮框外扩像素：让目标与遮罩间留出呼吸感。 */
const TARGET_PADDING = 8
/** 气泡卡估算尺寸：用于方位选择与视口钳制。 */
const CARD_W = 304
const CARD_H = 200
/** 目标定位轮询间隔：覆盖页签切换、模态开闭与滚动重排。 */
const LOCATE_INTERVAL_MS = 200
/** 气泡与目标的间距。 */
const CARD_GAP = 14
/** 镂空丢失宽限：目标短暂缺失/被遮（滚动擦边、弹窗进出、地图自动适配动画）期间保留上帧镂空不塌陷，超时才收拢全遮罩。 */
const HOLE_GRACE_MS = 700

interface HoleRect { top: number; left: number; width: number; height: number }
type OverlayPhase = 'guiding' | 'finished' | 'hidden'
type Placement = 'top' | 'bottom' | 'left' | 'right' | 'center'

/** 执行预备动作：把真实 UI 切到目标元素所在视图。 */
function runPrepare(prepare: TutorialPrepare): void {
  const ui = useUiStore.getState()
  if (prepare.type === 'tab') {
    ui.setActiveTab(prepare.tab)
  } else {
    ui.setActiveTab('crafting')
    ui.setCraftingMode(prepare.type)
    ui.setCraftingNavExpanded(true)
    if (prepare.type === 'alchemy') ui.setAlchemySubTab(prepare.subTab)
    else ui.setForgingSubTab(prepare.subTab)
  }
}

/** 查询首个「可见」的匹配元素（桌面/移动布局可能同时存在于 DOM）。 */
function findVisibleTarget(selector: string): HTMLElement | null {
  for (const el of document.querySelectorAll<HTMLElement>(selector)) {
    const rect = el.getBoundingClientRect()
    if (rect.width > 0 && rect.height > 0) return el
  }
  return null
}

/**
 * 收拢游离弹窗：业务 Modal/ConfirmDialog 层级低于引导遮罩，残留弹窗既点不到
 * 又会挡住镂空目标形成死锁（如「上一步」回退后 ActionModal 仍开着、跨页签
 * portal 残留）。凡是不包含当前引导目标的 `.modal-backdrop` 一律程序点击其
 * `.modal-close` 关闭；dismissible=false 的弹窗无关闭钮，跳过不处理。
 * 宽限期：刚出现不足 800ms 的弹窗不视为残留——「点行动卡→弹窗挂载→推进到下
 * 一子步」之间存在一拍竞态，旧步的轮询 tick 撞上该窗口会误关刚开的目标弹窗。
 */
const modalSeenAt = new WeakMap<HTMLElement, number>()
const MODAL_GRACE_MS = 800

function dismissForeignModals(target: HTMLElement | null): void {
  for (const backdrop of document.querySelectorAll<HTMLElement>('.modal-backdrop')) {
    const shell = backdrop.querySelector<HTMLElement>('.modal-shell')
    if (!shell || (target && shell.contains(target))) continue
    const seen = modalSeenAt.get(backdrop)
    const now = Date.now()
    if (seen === undefined) { modalSeenAt.set(backdrop, now); continue }
    if (now - seen < MODAL_GRACE_MS) continue
    shell.querySelector<HTMLButtonElement>('.modal-close')?.click()
  }
}

/** 计算气泡卡在指定方位下的矩形：先按方位摆到镂空边，再钳进可视区（band 为可视带在布局视口中的偏移）。 */
function cardRectFor(hole: HoleRect | null, placement: Placement, vw: number, vh: number, cardW: number, cardH: number, bandTop = 0, bandLeft = 0): HoleRect {
  const bottom = bandTop + vh
  const right = bandLeft + vw
  let top = bandTop + (vh - cardH) / 2
  let left = bandLeft + (vw - cardW) / 2
  if (hole && placement !== 'center') {
    if (placement === 'bottom') { top = hole.top + hole.height + CARD_GAP; left = hole.left + hole.width / 2 - cardW / 2 }
    else if (placement === 'top') { top = hole.top - CARD_GAP - cardH; left = hole.left + hole.width / 2 - cardW / 2 }
    else if (placement === 'right') { left = hole.left + hole.width + CARD_GAP; top = hole.top + hole.height / 2 - cardH / 2 }
    else { left = hole.left - CARD_GAP - cardW; top = hole.top + hole.height / 2 - cardH / 2 }
  }
  top = Math.max(bandTop + 12, Math.min(top, bottom - cardH - 12))
  left = Math.max(bandLeft + 12, Math.min(left, right - cardW - 12))
  return { top, left, width: cardW, height: cardH }
}

/** 两矩形交集面积（不相交为 0）。 */
function overlapArea(a: HoleRect, b: HoleRect): number {
  const w = Math.min(a.left + a.width, b.left + b.width) - Math.max(a.left, b.left)
  const h = Math.min(a.top + a.height, b.top + b.height) - Math.max(a.top, b.top)
  return w > 0 && h > 0 ? w * h : 0
}

/** 重叠最小化选位：按偏好序返回首个零遮挡方位；窄屏全遮挡时取与镂空交集最小者，尽力不压住高亮框。 */
function resolvePlacement(hole: HoleRect | null, preferred: TutorialUiStep['placement'], vw: number, vh: number, cardW: number, cardH: number, bandTop = 0, bandLeft = 0): Placement {
  if (!hole) return 'center'
  const order: Placement[] = preferred && preferred !== 'auto' ? [preferred, 'bottom', 'top', 'right', 'left'] : ['bottom', 'top', 'right', 'left']
  let best: Placement = 'center'
  let bestOverlap = Infinity
  for (const placement of order) {
    const overlap = overlapArea(cardRectFor(hole, placement, vw, vh, cardW, cardH, bandTop, bandLeft), hole)
    if (overlap === 0) return placement
    if (overlap < bestOverlap) { best = placement; bestOverlap = overlap }
  }
  return best
}

/** 由方位计算气泡卡坐标（钳制已含在 cardRectFor 内；选位已按重叠最小化完成，此处无需再避让）。 */
function resolveCardPosition(hole: HoleRect | null, placement: Placement, vw: number, vh: number, cardW: number, cardH: number, bandTop = 0, bandLeft = 0): { top: number; left: number } {
  const rect = cardRectFor(hole, placement, vw, vh, cardW, cardH, bandTop, bandLeft)
  return { top: rect.top, left: rect.left }
}

/**
 * 新手引导遮罩层：渲染遮罩四块、高亮框、箭头与气泡卡；
 * 处理 button/target/server 三种推进与跳过命令。
 */
export const TutorialOverlay = memo(function TutorialOverlay(): React.JSX.Element | null {
  const t = useT()
  const tutorialStep = useGameStore(state => state.tutorialStep)
  const hasSnapshot = useGameStore(state => state.hasReceivedSnapshot)
  const worldNodeId = useGameStore(state => state.world.nodeId)
  const [phase, setPhase] = useState<OverlayPhase>('guiding')
  const [substep, setSubstep] = useState(0)
  const [hole, setHole] = useState<HoleRect | null>(null)
  /** 当前子步骤的定位失败计数：持续找不到目标时给 target 卡补「下一步」逃生通道，避免死锁。 */
  const [locateMisses, setLocateMisses] = useState(0)
  /** 本机曾渲染过引导内容：服务端翻转 Done 时据此决定是否弹结算卡。 */
  const activeRef = useRef(false)
  /** 跳过标记：跳过路径不发完成结算卡。 */
  const skippedRef = useRef(false)
  /** 跳过二次确认态：确认卡也在引导层内渲染（业务 Modal z70 够不到引导层）。 */
  const [confirmingSkip, setConfirmingSkip] = useState(false)
  /** 已做过恢复评估的里程碑序号：防止快照推送引起的 effect 重跑把用户当前子步骤重置。 */
  const resumedForRef = useRef(-1)
  /** 上一次见到的服务端步骤：restart 是唯一会让它倒退的路径（正常只增不减）。 */
  const prevStepRef = useRef(-1)
  /** 气泡卡实测尺寸：奖励区/逃生提示等使实际高度偏离估算，定位与避让必须用实测值。 */
  const cardRef = useRef<HTMLDivElement>(null)
  const [cardSize, setCardSize] = useState({ w: CARD_W, h: CARD_H })
  /** 镂空区暂不可交互（目标被裁剪/遮挡或刚丢失）：其上盖透明拦截层吃掉触摸，防误点挡板；指针同时收起。 */
  const [holeBlocked, setHoleBlocked] = useState(false)
  /** 可视视口：移动端地址栏/底部工具栏显隐、键盘开合、横竖屏都会改变它；只读渲染期的 innerWidth/innerHeight 会让遮罩与气泡卡滞留旧几何。 */
  const [viewport, setViewport] = useState(() => ({
    w: window.innerWidth,
    h: window.visualViewport?.height ?? window.innerHeight,
    top: window.visualViewport?.offsetTop ?? 0,
    left: window.visualViewport?.offsetLeft ?? 0,
  }))

  useEffect(() => {
    const vv = window.visualViewport
    const update = () => setViewport(prev => {
      const next = {
        w: vv?.width ?? window.innerWidth,
        h: vv?.height ?? window.innerHeight,
        top: vv?.offsetTop ?? 0,
        left: vv?.offsetLeft ?? 0,
      }
      return prev.w === next.w && prev.h === next.h && prev.top === next.top && prev.left === next.left ? prev : next
    })
    update()
    window.addEventListener('resize', update)
    vv?.addEventListener('resize', update)
    vv?.addEventListener('scroll', update)
    return () => {
      window.removeEventListener('resize', update)
      vv?.removeEventListener('resize', update)
      vv?.removeEventListener('scroll', update)
    }
  }, [])

  useLayoutEffect(() => {
    const el = cardRef.current
    if (!el) return
    const w = el.offsetWidth
    const h = el.offsetHeight
    setCardSize(prev => (prev.w === w && prev.h === h ? prev : { w, h }))
  })

  const milestone = TUTORIAL_MILESTONES.find(m => m.step === tutorialStep) ?? null
  /**
   * 前置补位链：里程碑声明 awayPrefix 且玩家当前不在该里程碑要求的节点时，
   * 把「回村」引导插在欢迎卡之后；抵达后整条前缀自动摘除，回到正式步骤流。
   */
  const atHome = worldNodeId === HOME_NODE_ID
  const steps = milestone
    ? (milestone.awayPrefix && !atHome ? [milestone.steps[0], ...milestone.awayPrefix, ...milestone.steps.slice(1)] : milestone.steps)
    : null
  const uiStep = steps?.[Math.min(substep, steps.length - 1)] ?? null

  const advance = useCallback(() => setSubstep(index => index + 1), [])
  const retreat = useCallback(() => setSubstep(index => Math.max(0, index - 1)), [])
  /** 跳过先弹确认：明确告知剩余奖励直接发放 + 可在设置→引导重学。 */
  const requestSkip = useCallback(() => setConfirmingSkip(true), [])
  const confirmSkip = useCallback(() => {
    skippedRef.current = true
    gameClient.sendCommand('tutorial.skip', {})
    setConfirmingSkip(false)
    setPhase('hidden')
  }, [])

  // 里程碑翻转：重置子步骤；进入终态时按是否跳过决定弹结算卡或直接收起。
  // 恢复推导：本阶段行动/赶路已在队列（刷新/断线重连/阶段中段返回）时，直接落到
  // 末位「等待结算」子步，不重复走已完成的点击引导——子步骤是表现层，权威进度
  // 由服务端 tutorialStep 持久化。评估须等首份快照就位（否则刷新瞬间队列未填会
  // 误判为新开始），且每个里程碑只评估一次，避免快照重推覆盖用户当前子步骤。
  useEffect(() => {
    setHole(null)
    setConfirmingSkip(false)
    // 重学复活：tutorial.restart 让服务端步骤倒退（唯一递减路径）——
    // 收起态回到 guiding 并清掉跳过标记；不能用「milestone 非空且 hidden」判断，
    // 因为确认跳过瞬间 milestone 仍在（patch 未回）会误复活。
    const restarted = prevStepRef.current >= 0 && tutorialStep < prevStepRef.current
    prevStepRef.current = tutorialStep
    if (milestone) {
      activeRef.current = true
      if (restarted) skippedRef.current = false
      if (restarted && phase === 'hidden') setPhase('guiding')
      if (resumedForRef.current === tutorialStep || !hasSnapshot) return
      resumedForRef.current = tutorialStep
      const queue = useGameStore.getState().queue.entries
      const resumed =
        (milestone.actionId !== undefined && queue.some(e => e.actionId === milestone.actionId))
        || (milestone.travelToNodeId !== undefined && queue.some(e => e.actionId === 'world.travel' && e.travelDestinationNodeId === milestone.travelToNodeId))
      setSubstep(resumed && steps ? steps.length - 1 : 0)
    } else if (tutorialStep >= TUTORIAL_DONE && phase === 'guiding') {
      setPhase(activeRef.current && !skippedRef.current ? 'finished' : 'hidden')
      activeRef.current = false
    }
  }, [milestone, tutorialStep, phase, steps, hasSnapshot])

  // 抵达回村：玩家在前缀链推进途中自己先到了目标节点（或 arrive 步完成）时，
  // steps 数组从「带前缀」塌缩为正式步序——前缀段下标平移回正式步首步（下标1）。
  // 例外：行动/赶路已在队列（刷新恢复路径刚被推到末位等待步）时，塌缩应同样
  // 落到等待步而非 tap——否则恢复出的下标会被误判为前缀下标而二次平移。
  useEffect(() => {
    if (!milestone?.awayPrefix || !atHome) return
    const queue = useGameStore.getState().queue.entries
    const alreadyQueued = milestone.actionId !== undefined && queue.some(e => e.actionId === milestone.actionId)
    setSubstep(index => {
      if (index <= 0 || index > milestone.awayPrefix!.length) return index
      return alreadyQueued && steps ? steps.length - 1 : 1
    })
  }, [milestone, atHome, steps])
  // 同步新手引导要求大地图聚焦的目标地点（供 WorldMapPanel 触发平移运镜）
  useEffect(() => {
    const focusNodeId = phase === 'guiding' ? (uiStep?.mapFocusNodeId ?? null) : null
    useUiStore.getState().setTutorialMapFocusNodeId(focusNodeId)
    return () => {
      useUiStore.getState().setTutorialMapFocusNodeId(null)
    }
  }, [phase, uiStep?.mapFocusNodeId])


  // 预备动作 + 目标定位轮询 + 游离弹窗治理：元素变化时先滚动进视野，再按帧更新高亮矩形；
  // 无目标步骤同样持续轮询——残留的确认弹窗在遮罩下点不到，必须由治理收拢。
  // 移动端稳态约束：
  // - scrolledFor 不随 miss/blocked 复位——同一元素每子步至多滚一次，遮挡/丢失恢复后不再
  //   重新 scrollIntoView，避免与玩家触摸滚动往复互抢（滚动擦边曾是「上下闪」主因）；
  // - 目标被遮/丢失时镂空经 HOLE_GRACE_MS 宽限后才塌陷为全遮罩，期间镂空区盖透明拦截层
  //   （blocked 时仍跟随元素实时位置），消除镂空⇄全遮罩高频翻转引起的整屏闪烁。
  useEffect(() => {
    if (phase !== 'guiding' || !uiStep) { setHole(null); setHoleBlocked(false); return }
    setLocateMisses(0)
    setHoleBlocked(false)
    for (const prepare of uiStep.prepare ?? []) runPrepare(prepare)
    // 队列暂停时行动与赶路均不推进：等待步将永远等不到服务端里程碑，
    // 且暂停开关在遮罩下玩家不可操作，由引导代发恢复命令解除死等。
    if ((uiStep.advance === 'server' || uiStep.advance === 'arrive') && useGameStore.getState().queue.paused) {
      gameClient.sendCommand('queue.configure', { paused: false })
    }
    const selector = uiStep.selector ?? uiStep.focusSelector
    /** 展示性聚焦（等待步）：只定位出 hole 供观看，不做可点性检测——该区域本就不可交互。 */
    const focusOnly = uiStep.selector === undefined && uiStep.focusSelector !== undefined
    let scrolledFor: HTMLElement | null = null
    let lostAt = 0
    /** 按元素可视 rect 更新镂空（换算到布局视口坐标）；1px 死区过滤亚像素重排。 */
    const trackHole = (rect: DOMRect) => {
      const vv = window.visualViewport
      const offTop = vv && vv.scale === 1 ? vv.offsetTop : 0
      const offLeft = vv && vv.scale === 1 ? vv.offsetLeft : 0
      const next: HoleRect = { top: rect.top + offTop - TARGET_PADDING, left: rect.left + offLeft - TARGET_PADDING, width: rect.width + TARGET_PADDING * 2, height: rect.height + TARGET_PADDING * 2 }
      setHole(prev => (prev && Math.abs(prev.top - next.top) < 1 && Math.abs(prev.left - next.left) < 1 && Math.abs(prev.width - next.width) < 1 && Math.abs(prev.height - next.height) < 1) ? prev : next)
    }
    const locate = () => {
      const element = selector ? findVisibleTarget(selector) : null
      if (!element) {
        lostAt ||= Date.now()
        setHoleBlocked(true)
        if (Date.now() - lostAt > HOLE_GRACE_MS) setHole(null)
        if (uiStep.modalBound) {
          if (document.querySelector('.modal-backdrop')) {
            // 目标弹窗已开但按钮尚未完成布局：按定位失败计数等待，绝不可误关该弹窗
            setLocateMisses(misses => misses + 1)
          } else {
            // 无弹窗：click 捕获推进早于 React 渲染弹窗一拍，先按 miss 计；
            // 持续无弹窗（Esc 关闭/逃生误入）才回退到「打开弹窗」的上一子步
            setLocateMisses(misses => {
              const next = misses + 1
              if (next >= 8) { setSubstep(index => Math.max(0, index - 1)); return 0 }
              return next
            })
          }
          return
        }
        // 无目标步骤不计 miss（无需逃生通道），只做游离弹窗收拢
        if (selector) setLocateMisses(misses => misses + 1)
        dismissForeignModals(null)
        return
      }
      dismissForeignModals(element)
      const rect = element.getBoundingClientRect()
      if (element !== scrolledFor) { element.scrollIntoView({ block: 'nearest', inline: 'nearest' }); scrolledFor = element }
      if (!focusOnly) {
        // disabled 目标点了无反应、遮挡目标空洞点击落在挡板上：均按定位失败计数，
        // 让逃生「下一步」如期出现；遮挡源在下一 tick 通常已被上一步治理收拢。
        const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2)
        // 引导层自身元素（气泡卡/箭头/拦截层）不算遮挡源：极端小屏卡片可能压到目标中心，
        // 命中它们不代表目标被业务元素挡住。
        const occluded = hit !== null && hit !== element && !element.contains(hit) && hit.closest('.tutorial-layer') === null
        const blocked = element.matches(':disabled, [aria-disabled="true"]') || occluded
        if (blocked) {
          // 遮挡态不再立刻撤掉镂空：元素仍在（多半只是被滚动裁边/短暂盖住），
          // 镂空继续跟住其位置并加拦截层，仅持续超过宽限才退回全遮罩。
          lostAt ||= Date.now()
          setHoleBlocked(true)
          setLocateMisses(misses => misses + 1)
          if (Date.now() - lostAt > HOLE_GRACE_MS) setHole(null)
          else trackHole(rect)
          return
        }
      }
      lostAt = 0
      setHoleBlocked(false)
      setLocateMisses(0)
      trackHole(rect)
    }
    locate()
    const timer = window.setInterval(locate, LOCATE_INTERVAL_MS)
    return () => window.clearInterval(timer)
  }, [phase, uiStep])

  // target 推进：捕获阶段监听真实控件点击，点到高亮目标即进入下一子步骤。
  // 推进延迟到本次事件 dispatch 结束：若在捕获阶段同步 setSubstep，React 会立即
  // commit 到新子步——新步若为无目标的等待步，其首轮定位会触发游离弹窗治理，
  // 把「正在执行 onClick 提交」的业务弹窗当残留收掉；组件卸载后 React 不再派发
  // 该事件的 onClick，入队命令随之丢失（玩家点了入队却没入队）。
  useEffect(() => {
    const selector = uiStep?.selector
    if (phase !== 'guiding' || uiStep?.advance !== 'target' || !selector) return
    const onClick = (event: MouseEvent) => {
      if (!(event.target as Element | null)?.closest?.(selector)) return
      const from = substep
      window.setTimeout(() => setSubstep(index => (index === from ? index + 1 : index)), 0)
    }
    document.addEventListener('click', onClick, true)
    return () => document.removeEventListener('click', onClick, true)
  }, [phase, uiStep, substep])

  // Esc 视为跳过引导（与按钮同路径——先弹确认，确认态下 Esc 取消回引导）；
  // 但业务弹窗开着时 Esc 优先归弹窗消费（Modal 自身监听 Esc 关闭），
  // 避免一次按键同时关弹窗又跳过整个引导。
  useEffect(() => {
    if (phase === 'hidden') return
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || document.querySelector('.modal-backdrop')) return
      setConfirmingSkip(prev => !prev)
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [phase])

  if (!hasSnapshot || phase === 'hidden') return null

  const vw = viewport.w
  const vh = viewport.h
  /** 遮罩覆盖范围取布局视口与可视带并集：捏合缩放（vv.scale>1）时可视带小于布局视口，
      仅用 vv 尺寸会在固定定位的布局视口内露出未遮蔽边缘。 */
  const maskW = Math.max(window.innerWidth, vw)
  const maskH = Math.max(window.innerHeight, vh)
  const isFinished = phase === 'finished'
  /** 跳过确认态：全遮罩 + 居中确认卡，当前高亮暂时收拢。 */
  const showFullMask = confirmingSkip || !hole || isFinished
  /** 展示性聚焦步（等待中）或目标暂不可交互：镂空区盖透明拦截层，详情可看不可点。 */
  const isFocusStep = uiStep !== null && uiStep.selector === undefined && uiStep.focusSelector !== undefined
  const placement = (isFinished || confirmingSkip) ? 'center' as Placement : resolvePlacement(hole, uiStep?.placement, vw, vh, cardSize.w, cardSize.h, viewport.top, viewport.left)
  const cardPos = resolveCardPosition(showFullMask ? null : hole, placement, vw, vh, cardSize.w, cardSize.h, viewport.top, viewport.left)
  const progress = milestone ? (milestone.step + 1) / TUTORIAL_MILESTONE_COUNT : 1

  // portal 至 document.body：与业务 .modal-backdrop（z70）同处根堆叠上下文，
  // z300 直接胜出——若留在 .app 树内，祖先 stacking context 会把 300 囚禁，
  // 弹窗将压到遮罩与气泡卡之上（「上一步/跳过」按钮点不到 = 死锁）。
  return createPortal(
    <div aria-modal="false" className="tutorial-layer" role="dialog">
      {!showFullMask && hole ? (
        <>
          <div className="tutorial-mask" style={{ top: 0, left: 0, width: maskW, height: Math.max(0, hole.top) }} />
          <div className="tutorial-mask" style={{ top: hole.top, left: 0, width: Math.max(0, hole.left), height: hole.height }} />
          <div className="tutorial-mask" style={{ top: hole.top, left: hole.left + hole.width, width: Math.max(0, maskW - hole.left - hole.width), height: hole.height }} />
          <div className="tutorial-mask" style={{ top: hole.top + hole.height, left: 0, width: maskW, height: Math.max(0, maskH - hole.top - hole.height) }} />
          <div className={isFocusStep ? 'tutorial-highlight tutorial-highlight--focus' : 'tutorial-highlight'} style={hole} />
          {/* 透明拦截层：镂空区可见但点击不穿透——等待期只能看进度；目标被遮/丢失宽限期内同样拦截，防点穿到遮挡物 */}
          {(isFocusStep || holeBlocked) && <div className="tutorial-focus-blocker" style={hole} />}
          {uiStep?.advance === 'target' && !holeBlocked && (
            <div className="tutorial-pointer" style={{ top: hole.top, left: hole.left + hole.width / 2 }} />
          )}
        </>
      ) : (
        <div className="tutorial-mask tutorial-mask--full" />
      )}
      <div ref={cardRef} className="tutorial-card" data-placement={placement} style={{ top: cardPos.top, left: cardPos.left, width: CARD_W }}>
        {hole && placement !== 'center' && !isFinished && <div className="tutorial-arrow" />}
        {confirmingSkip ? (
          <>
            <div className="tutorial-kicker">{t('app.tutorial.skip')}</div>
            <h3 className="tutorial-title">{t('app.tutorial.skipConfirm.title')}</h3>
            <p className="tutorial-body">{t('app.tutorial.skipConfirm.body')}</p>
            <div className="tutorial-actions">
              <button className="tutorial-btn tutorial-btn--ghost" onClick={() => setConfirmingSkip(false)} type="button">{t('app.tutorial.skipConfirm.cancel')}</button>
              <span className="tutorial-spacer" />
              <button className="tutorial-btn tutorial-btn--primary" onClick={confirmSkip} type="button">{t('app.tutorial.skipConfirm.confirm')}</button>
            </div>
          </>
        ) : isFinished ? (
          <>
            <div className="tutorial-kicker">{t('app.tutorial.kicker.done')}</div>
            <h3 className="tutorial-title">{t('app.tutorial.done.title')}</h3>
            <p className="tutorial-body">{t('app.tutorial.done.body')}</p>
            <div className="tutorial-rewards">
              <ItemReference amount={1000} itemId="item.gongde" />
              <ItemReference amount={10000} itemId="currency.spirit-stone" />
            </div>
            <div className="tutorial-actions">
              <button className="tutorial-btn tutorial-btn--primary" onClick={() => setPhase('hidden')} type="button">{t('app.tutorial.finish')}</button>
            </div>
          </>
        ) : uiStep && milestone ? (
          <>
            <div className="tutorial-kicker">{t(milestone.kickerKey)}</div>
            <h3 className="tutorial-title">{t(uiStep.titleKey)}</h3>
            <p className="tutorial-body">{t(uiStep.bodyKey)}</p>
            {/* 阶段奖励预览：与服务端 GrantsFor(step+1) 同源声明，展示但不发放 */}
            {milestone.rewards && milestone.rewards.length > 0 && (
              <div className="tutorial-stage-rewards">
                <span className="tutorial-stage-rewards-label">{t('app.tutorial.stageReward')}</span>
                {milestone.rewards.map(r => <ItemReference amount={r.amount} itemId={r.itemId} key={r.itemId} />)}
              </div>
            )}
            <div className="tutorial-progress"><i style={{ width: `${Math.round(progress * 100)}%` }} /></div>
            <div className="tutorial-actions">
              <button className="tutorial-btn tutorial-btn--ghost" onClick={requestSkip} type="button">{t('app.tutorial.skip')}</button>
              <span className="tutorial-spacer" />
              {/* 全步骤允许回退：命令发送失败或误入等待步时可退回重发，防止死等 */}
              {substep > 0 && (
                <button className="tutorial-btn tutorial-btn--ghost" onClick={retreat} type="button">{t('app.tutorial.prev')}</button>
              )}
              {uiStep.advance === 'button' && (
                <button className="tutorial-btn tutorial-btn--primary" onClick={advance} type="button">{t('app.tutorial.next')}</button>
              )}
              {/* 目标持续定位失败时给逃生通道：遮罩全开状态下用户不至于被卡死在当前子步骤 */}
              {uiStep.advance === 'target' && locateMisses > 12 && (
                <button className="tutorial-btn tutorial-btn--ghost" onClick={advance} type="button">{t('app.tutorial.next')}</button>
              )}
              {uiStep.advance === 'target' && locateMisses <= 12 && (
                <span className="tutorial-waiting">{t('app.tutorial.clickTarget')}</span>
              )}
              {(uiStep.advance === 'server' || uiStep.advance === 'arrive') && <span className="tutorial-waiting">{t('app.tutorial.waiting')}</span>}
            </div>
          </>
        ) : null}
      </div>
    </div>,
    document.body,
  )
})
