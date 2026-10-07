import type { AlchemySubTab, ForgingSubTab, TabId } from '../uiStore'

/**
 * 新手引导 UI 子步骤配置（总纲 §22.4）。
 * 里程碑序号与服务端 settings.tutorialStep 一一对应；每个里程碑内部再拆成
 * 若干 UI 子步骤（说明卡 / 点击真实控件 / 等待服务端推进）。
 */

/** 子步骤推进模式：button=点按钮，target=点击高亮目标，server=等待服务端里程碑翻转，arrive=等待抵达指定节点。 */
export type TutorialAdvance = 'button' | 'target' | 'server' | 'arrive'

/** 预备动作：子步骤激活时先驱动真实 UI，再等待目标元素出现。 */
export type TutorialPrepare =
  | { type: 'tab'; tab: TabId }
  | { type: 'alchemy'; subTab: AlchemySubTab }
  | { type: 'forging'; subTab: ForgingSubTab }

export interface TutorialUiStep {
  /** 高亮目标的 CSS 选择器；缺省时气泡卡居中展示 */
  readonly selector?: string
  /**
   * 展示性聚焦选择器（server/arrive 等待步用）：镂空高亮该区域让玩家观察进度，
   * 但区域上盖透明拦截层——可看不可点，等待期间玩家无法操作详情区控件。
   */
  readonly focusSelector?: string
  /** 标题/正文 i18n 点路径（app.tutorial.*） */
  readonly titleKey: string
  readonly bodyKey: string
  /** 气泡方位偏好；缺省 auto（按剩余空间自动选择） */
  readonly placement?: 'top' | 'bottom' | 'left' | 'right' | 'auto'
  readonly advance: TutorialAdvance
  /** advance='arrive' 时等待到达的目标节点 ID（world.nodeId 变为该值即推进） */
  readonly arriveNodeId?: string
  /**
   * 目标位于业务弹窗内（行动详情模态/启程确认弹窗）：弹窗一旦被 Esc 或意外关闭，
   * 引导自动回退一个子步骤（回到「打开该弹窗」步），避免目标随弹窗消失后全遮罩死等。
   */
  readonly modalBound?: boolean
  readonly prepare?: readonly TutorialPrepare[]
}

export interface TutorialMilestone {
  /** 里程碑序号 = 服务端 tutorialStep 值 */
  readonly step: number
  /** 气泡卡顶部 kicker 词条（「第一环 · 就地采集」） */
  readonly kickerKey: string
  readonly steps: readonly TutorialUiStep[]
  /**
   * 前置条件不满足时的补位步骤：仅当玩家在里程碑开始时不在出生节点
   * （云溪村）时插入到 steps 之前，引导其先旅行回村，再进入正式步骤。
   */
  readonly awayPrefix?: readonly TutorialUiStep[]
  /**
   * 本阶段绑定完成的行动 ID：断线/刷新恢复时若该行动已在队列中，
   * 直接跳到本阶段末位「等待结算」子步，不重走已完成的点击引导。
   */
  readonly actionId?: string
  /** 旅行型阶段的到达节点：队列中已有前往该节点的赶路条目时同样跳至等待步。 */
  readonly travelToNodeId?: string
  /** 完成本阶段后服务端入账的阶段奖励（与服务端 GrantsFor(step+1) 同源，仅展示）。 */
  readonly rewards?: readonly { readonly itemId: string; readonly amount: number }[]
}

/** 出生节点（云溪村）：引导起点与讨伐归宿都绑定该节点。 */
export const HOME_NODE_ID = 'node.yunxi-village'
/** 行动模态「入队/开战」提交钮锚点（App.tsx 行动详情模态）。 */
const ACTION_SUBMIT = '[data-tour="action-submit"]'
/** 地图节点锚点（WorldMapPanel 已带 data-node-id）。 */
const PEAK_NODE = '[data-node-id="node.cloud-peak"]'
const VILLAGE_NODE = `[data-node-id="${HOME_NODE_ID}"]`
/** 地图侧边「规划路线」与启程确认钮锚点。 */
const TRAVEL_PLAN = '[data-tour="travel-plan"]'
const TRAVEL_CONFIRM = '[data-tour="travel-confirm"]'
/** 左侧主导航「地图」入口锚点（Sidebar）。 */
const NAV_MAP = '[data-tour="nav-map"]'
/** 详情页主面板锚点（CombatDetailsPage）：等待步切到「详情」页签并聚焦展示行动实况，可看不可点。 */
const DETAIL_PAGE = '[data-tour="combat-detail"]'
/** 节点地图本体锚点（WorldMapPanel .world-map-stage）：旅行等待步聚焦展示路线与移动实况。 */
const WORLD_MAP = '[data-tour="world-map"]'

const PREP_EXPLORATION: readonly TutorialPrepare[] = [{ type: 'tab', tab: 'exploration' }]
const PREP_ALCHEMY: readonly TutorialPrepare[] = [{ type: 'alchemy', subTab: 'cultivation' }]
const PREP_FORGING: readonly TutorialPrepare[] = [{ type: 'forging', subTab: 'weapon' }]
const PREP_MAP: readonly TutorialPrepare[] = [{ type: 'tab', tab: 'map' }]
/** 等待步切到「详情」页签：行动实况在该页主面板展示，配合 focusSelector 镂空供观看。 */
const PREP_COMBAT: readonly TutorialPrepare[] = [{ type: 'tab', tab: 'combat' }]

/** 「先回云溪村」前置引导链：打开地图 → 点云溪村节点 → 规划路线 → 启程 → 等待到达。 */
const AWAY_PREFIX_HOME: readonly TutorialUiStep[] = [
  { selector: NAV_MAP, titleKey: 'app.tutorial.returnHome.map.title', bodyKey: 'app.tutorial.returnHome.map.body', advance: 'target' },
  { selector: VILLAGE_NODE, titleKey: 'app.tutorial.returnHome.node.title', bodyKey: 'app.tutorial.returnHome.node.body', advance: 'target', prepare: PREP_MAP },
  { selector: TRAVEL_PLAN, titleKey: 'app.tutorial.returnHome.plan.title', bodyKey: 'app.tutorial.returnHome.plan.body', advance: 'target', prepare: PREP_MAP },
  { selector: TRAVEL_CONFIRM, modalBound: true, titleKey: 'app.tutorial.returnHome.confirm.title', bodyKey: 'app.tutorial.returnHome.confirm.body', advance: 'target' },
  { titleKey: 'app.tutorial.returnHome.wait.title', bodyKey: 'app.tutorial.returnHome.wait.body', advance: 'arrive', prepare: PREP_MAP, focusSelector: WORLD_MAP, arriveNodeId: HOME_NODE_ID },
]

/** 按里程碑排布的引导步骤表；与服务端 TutorialSteps 常量同源。 */
export const TUTORIAL_MILESTONES: readonly TutorialMilestone[] = [
  {
    step: 0,
    kickerKey: 'app.tutorial.kicker.gather',
    actionId: 'explore.gather-ningxue-grass',
    // 完成采集后服务端赠送炼丹材料（引气丹单轮输入）
    rewards: [{ itemId: 'item.ningxue-grass', amount: 3 }],
    // 玩家不在云溪村时先补「回村」引导：采集行动仅在该节点可用，缺前置会导致目标永失。
    awayPrefix: AWAY_PREFIX_HOME,
    steps: [
      { titleKey: 'app.tutorial.welcome.title', bodyKey: 'app.tutorial.welcome.body', advance: 'button', placement: 'auto' },
      { selector: '[data-action-id="explore.gather-ningxue-grass"]', titleKey: 'app.tutorial.gather.tap.title', bodyKey: 'app.tutorial.gather.tap.body', advance: 'target', prepare: PREP_EXPLORATION },
      { selector: ACTION_SUBMIT, modalBound: true, titleKey: 'app.tutorial.gather.enqueue.title', bodyKey: 'app.tutorial.gather.enqueue.body', advance: 'target' },
      { titleKey: 'app.tutorial.gather.wait.title', bodyKey: 'app.tutorial.gather.wait.body', advance: 'server', prepare: PREP_COMBAT, focusSelector: DETAIL_PAGE },
    ],
  },
  {
    step: 1,
    kickerKey: 'app.tutorial.kicker.alchemy',
    actionId: 'alchemy.refine-qi-pill',
    // 完成炼丹后服务端赠送炼器材料（青锋剑单轮输入）
    rewards: [{ itemId: 'item.refined-iron', amount: 7 }, { itemId: 'item.beast-bone', amount: 1 }],
    steps: [
      { titleKey: 'app.tutorial.alchemy.intro.title', bodyKey: 'app.tutorial.alchemy.intro.body', advance: 'button', placement: 'auto', prepare: PREP_ALCHEMY },
      { selector: '[data-action-id="alchemy.refine-qi-pill"]', titleKey: 'app.tutorial.alchemy.tap.title', bodyKey: 'app.tutorial.alchemy.tap.body', advance: 'target', prepare: PREP_ALCHEMY },
      { selector: ACTION_SUBMIT, modalBound: true, titleKey: 'app.tutorial.alchemy.enqueue.title', bodyKey: 'app.tutorial.alchemy.enqueue.body', advance: 'target' },
      { titleKey: 'app.tutorial.alchemy.wait.title', bodyKey: 'app.tutorial.alchemy.wait.body', advance: 'server', prepare: PREP_COMBAT, focusSelector: DETAIL_PAGE },
    ],
  },
  {
    step: 2,
    kickerKey: 'app.tutorial.kicker.forge',
    actionId: 'forge.qingfeng-sword',
    steps: [
      { titleKey: 'app.tutorial.forge.intro.title', bodyKey: 'app.tutorial.forge.intro.body', advance: 'button', placement: 'auto', prepare: PREP_FORGING },
      { selector: '[data-action-id="forge.qingfeng-sword"]', titleKey: 'app.tutorial.forge.tap.title', bodyKey: 'app.tutorial.forge.tap.body', advance: 'target', prepare: PREP_FORGING },
      { selector: ACTION_SUBMIT, modalBound: true, titleKey: 'app.tutorial.forge.enqueue.title', bodyKey: 'app.tutorial.forge.enqueue.body', advance: 'target' },
      { titleKey: 'app.tutorial.forge.wait.title', bodyKey: 'app.tutorial.forge.wait.body', advance: 'server', prepare: PREP_COMBAT, focusSelector: DETAIL_PAGE },
    ],
  },
  {
    step: 3,
    kickerKey: 'app.tutorial.kicker.travelPeak',
    travelToNodeId: 'node.cloud-peak',
    steps: [
      { selector: PEAK_NODE, titleKey: 'app.tutorial.travelPeak.tap.title', bodyKey: 'app.tutorial.travelPeak.tap.body', advance: 'target', prepare: PREP_MAP },
      { selector: TRAVEL_PLAN, titleKey: 'app.tutorial.travelPeak.plan.title', bodyKey: 'app.tutorial.travelPeak.plan.body', advance: 'target', prepare: PREP_MAP },
      { selector: TRAVEL_CONFIRM, modalBound: true, titleKey: 'app.tutorial.travelPeak.confirm.title', bodyKey: 'app.tutorial.travelPeak.confirm.body', advance: 'target' },
      { titleKey: 'app.tutorial.travelPeak.wait.title', bodyKey: 'app.tutorial.travelPeak.wait.body', advance: 'server', prepare: PREP_MAP, focusSelector: WORLD_MAP },
    ],
  },
  {
    step: 4,
    kickerKey: 'app.tutorial.kicker.cultivate',
    actionId: 'cultivation.draw-qi',
    steps: [
      { titleKey: 'app.tutorial.cultivate.intro.title', bodyKey: 'app.tutorial.cultivate.intro.body', advance: 'button', placement: 'auto', prepare: PREP_EXPLORATION },
      { selector: '[data-action-id="cultivation.draw-qi"]', titleKey: 'app.tutorial.cultivate.tap.title', bodyKey: 'app.tutorial.cultivate.tap.body', advance: 'target', prepare: PREP_EXPLORATION },
      { selector: ACTION_SUBMIT, modalBound: true, titleKey: 'app.tutorial.cultivate.enqueue.title', bodyKey: 'app.tutorial.cultivate.enqueue.body', advance: 'target' },
      { titleKey: 'app.tutorial.cultivate.wait.title', bodyKey: 'app.tutorial.cultivate.wait.body', advance: 'server', prepare: PREP_COMBAT, focusSelector: DETAIL_PAGE },
    ],
  },
  {
    step: 5,
    kickerKey: 'app.tutorial.kicker.travelVillage',
    travelToNodeId: 'node.yunxi-village',
    steps: [
      { selector: VILLAGE_NODE, titleKey: 'app.tutorial.travelVillage.tap.title', bodyKey: 'app.tutorial.travelVillage.tap.body', advance: 'target', prepare: PREP_MAP },
      { selector: TRAVEL_PLAN, titleKey: 'app.tutorial.travelVillage.plan.title', bodyKey: 'app.tutorial.travelVillage.plan.body', advance: 'target', prepare: PREP_MAP },
      { selector: TRAVEL_CONFIRM, modalBound: true, titleKey: 'app.tutorial.travelVillage.confirm.title', bodyKey: 'app.tutorial.travelVillage.confirm.body', advance: 'target' },
      { titleKey: 'app.tutorial.travelVillage.wait.title', bodyKey: 'app.tutorial.travelVillage.wait.body', advance: 'server', prepare: PREP_MAP, focusSelector: WORLD_MAP },
    ],
  },
  {
    step: 6,
    kickerKey: 'app.tutorial.kicker.combat',
    actionId: 'combat.hunt-qi-beast',
    // 完成讨伐后服务端发放引导终奖（功德×1000 + 灵石×10000）
    rewards: [{ itemId: 'item.gongde', amount: 1000 }, { itemId: 'currency.spirit-stone', amount: 10000 }],
    steps: [
      { titleKey: 'app.tutorial.combat.intro.title', bodyKey: 'app.tutorial.combat.intro.body', advance: 'button', placement: 'auto', prepare: PREP_EXPLORATION },
      { selector: '[data-action-id="combat.hunt-qi-beast"]', titleKey: 'app.tutorial.combat.tap.title', bodyKey: 'app.tutorial.combat.tap.body', advance: 'target', prepare: PREP_EXPLORATION },
      { selector: ACTION_SUBMIT, modalBound: true, titleKey: 'app.tutorial.combat.enqueue.title', bodyKey: 'app.tutorial.combat.enqueue.body', advance: 'target' },
      { titleKey: 'app.tutorial.combat.wait.title', bodyKey: 'app.tutorial.combat.wait.body', advance: 'server', prepare: PREP_COMBAT, focusSelector: DETAIL_PAGE },
    ],
  },
]

/** 引导终态的服务端步骤值（与领域 TutorialSteps.Done 同源）。 */
export const TUTORIAL_DONE = 7
/** 引导里程碑总数（进度展示 n/m 的分母）。 */
export const TUTORIAL_MILESTONE_COUNT = TUTORIAL_MILESTONES.length
