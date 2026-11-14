/** 更新日志条目分类标识 */
export type ChangelogCategory = 'feature' | 'balance' | 'fix' | 'optimize'

/** 单个分类下的更新条目组 */
export interface ChangelogSection {
  readonly category: ChangelogCategory
  /** 支持 `{{kind:id|label}}` 富文本词条的简练更新条目 */
  readonly items: readonly string[]
}

/** 单期更新日志记录 */
export interface ChangelogEntry {
  /** 更新日期（YYYY-MM-DD，按时间倒序排列） */
  readonly date: string
  /** 对应版本范围或发布版本号（渲染于卡片头部版本徽记，避免在正文与数值胶囊混排） */
  readonly versionLabel: string
  /** 本期更新标题 */
  readonly title: string
  /** 本期简述导语 */
  readonly summary: string
  /**
   * 独立更新公告弹框识别键：
   * 仅当发布带新 `noticeId` 的版本纪要时，才对尚未读过该 `noticeId` 的玩家自动弹出一次更新公告；
   * 平时不发新日志或仅做常规维护更新时，不修改 `noticeId` 即不会重复弹窗打扰玩家。
   */
  readonly noticeId?: string
  readonly sections: readonly ChangelogSection[]
}

export const CHANGELOG_DISMISSED_STORAGE_KEY = 'daohua.changelog.dismissedNoticeIds'

/**
 * 《道化太初》玩家向更新日志清单（最新日期排在最前）
 */
export const CHANGELOG_ENTRIES: readonly ChangelogEntry[] = [
  {
    date: '2026-11-14',
    versionLabel: 'v0.3.3 ~ v0.3.6',
    title: '天榜排行、演武 V2 特效与大地图重塑',
    summary: '新增天榜竞争与配装隐私保护，全面升级战斗演武画廊与大地图节点视效，并实装移形/灼烧新流派体系。',
    noticeId: 'notice-20261114-v0.3.6',
    sections: [
      {
        category: 'feature',
        items: [
          '新增「天榜」系统，整点刷新修为、战力与百艺榜名录；设置中新增配装隐私开关，可隐藏自身装备与功法配置。',
          '战斗演武实装全彩 Canvas V2 特效引擎，新增 Q5 特质 {{trait:trait.jianyuxin|剑与心}}（12 点消耗）、{{trait:trait.huoshen|火神}}（12 点消耗）及功法 {{ability:ability.yueqian|跃迁}}、{{ability:ability.zhanyu|斩玉}}、{{ability:ability.huoyan-fengbao|火焰风暴}}，支持 {{buff:buff.yixing|移形}} 变招（踏云）与 {{buff:buff.ranhun|燃烧}} 叠加灼烧。',
          '大地图节点全面翻新：节点尺寸翻倍并升级古匾样式，地图上直接标出妖王与精英角标；点击文中的 {{location:node.rock-beach|清石水滩}} 等地名即可直达节点。',
        ],
      },
      {
        category: 'balance',
        items: [
          '1~30 级境界与六维根基升级经验整体大幅下调（前 30 级更平顺），同步调平 {{item:equipment.qingfeng-sword|青锋剑}}、{{item:equipment.xuantie-sword|玄铁长剑}}、{{item:equipment.bishui-robe|碧水灵袍}}、{{item:equipment.ningshui-charm|凝水灵符}} 等多件一、二阶装备数值。',
          '对齐 {{item:equipment.duanchen-blade|断尘残刃剑}}、{{item:equipment.pogang-sword|破罡剑}} 等七件特殊装备的穿戴等级与配方，并将宗门驻地建筑建造所需材料全部减半。',
        ],
      },
      {
        category: 'fix',
        items: [
          '修复战斗触发器条件评估、组队收益结算与离线冷挂机掉率边界异常；彻底清理界面残留 Emoji 并同步万象图鉴条目。',
        ],
      },
    ],
  },
  {
    date: '2026-11-13',
    versionLabel: 'v0.3.2',
    title: '模拟器纯前端化与三阶修炼开放',
    summary: '战斗模拟器切为 1:1 前端本地引擎推演，补全高阶吐纳行动并修正敌方施法频率与小游戏结算细节。',
    sections: [
      {
        category: 'feature',
        items: [
          '战斗模拟器重写为纯前端 1:1 确定性引擎，演武测算与收益对比响应更快、无需额外后端开销。',
          '新增 Lv.9 三级修炼行动 {{action:cultivation.ningqi-guiyuan|凝气归元}}（10 秒/轮，基础修为收益并额外恢复 +40 灵力，附带经脉/根骨各 50% 根基经验）。',
        ],
      },
      {
        category: 'balance',
        items: [
          '下调 {{enemy:enemy.wood-spirit|木灵}} 与 {{enemy:enemy.fire-spirit|火灵}} 的灵力上限至单次施法水平，限制连续高频施法。',
        ],
      },
      {
        category: 'fix',
        items: [
          '修复组队模式下消耗品触发条件偶发冻结的问题；放行斗地主三次抢地主后的终局倍率校验。',
        ],
      },
    ],
  },
  {
    date: '2026-11-12',
    versionLabel: 'v0.2.10 ~ v0.3.1',
    title: '特质定格系统、前期法术加强与热更免退',
    summary: '重整特质生效与同道界面结构，加强前期法术伤害并优化协议热更兼容性。',
    sections: [
      {
        category: 'feature',
        items: [
          '战斗特质支持按条件入战定格（{{trait:trait.chujian|初见}}、{{trait:trait.bukandaowo|看不到我}}、{{trait:trait.woyaodashige|我要打十个}}、{{trait:trait.dantiaowang|单挑王}}、{{trait:trait.xuyi|蓄意}}），并在装配视图增加特质栏与装备槽位排序。',
          '同道页签收编进统一导航栏，斗地主新增全屏模式与服务端时钟同步；Android 端启用独立 HD 画质资源热更。',
        ],
      },
      {
        category: 'balance',
        items: [
          '下调前期杂兵等级并移除低阶怪身上的 {{ability:ability.chain-strike|连环}} 技能；小幅上调 {{ability:ability.palm-lightning|掌心雷}}、{{ability:ability.blazing-blast|轰炎}}、{{ability:ability.lightning-attraction|雷光引}}、{{ability:ability.tide|潮汐}} 等技能固伤。',
        ],
      },
      {
        category: 'fix',
        items: [
          '修复秘法感悟经验等级取值偏差、{{item:item.cuiling-powder|淬灵粉}} / {{item:item.guling-sand|固灵砂}} 对非强化行动误扣次数及组队复活读条跨波次残留的问题。',
          '重构协议兼容判定门槛，一般更新不再强制踢出已登录玩家。',
        ],
      },
    ],
  },
  {
    date: '2026-11-11',
    versionLabel: 'v0.2.9',
    title: '斗地主玩法、建筑入模拟与消耗品长效计扣',
    summary: '道友消遣小游戏上线，清石水滩配齐初始阵法，自动消耗品按实战消耗精确计扣。',
    sections: [
      {
        category: 'feature',
        items: [
          '新增同道队伍小游戏「斗地主」；强化与秘法界面的行动完成后支持继续追加队列，无需反复切页。',
          '为 {{location:node.rock-beach|清石水滩}} 配置初始 1 级 {{building:building.rejuvenation-array|回春阵}} 与 5 级 {{building:building.mana-regen-array|回元阵}}，战斗模拟器同步纳入建筑与队友战力。',
        ],
      },
      {
        category: 'fix',
        items: [
          '支持耐久与多槽消耗品按战斗实长精确扣减（优化 {{item:item.juling-formation-disk|聚灵阵盘}}、{{item:item.zhixue-powder|止血散}}、{{item:item.dingshen-incense|定神香}} 自动栏位策略），修复战报中即时回血回蓝偶现缺失的问题。',
        ],
      },
    ],
  },
  {
    date: '2026-11-10',
    versionLabel: 'v0.2.7 ~ v0.2.8',
    title: '传音图床与面板扩展体验升级',
    summary: '完善传音发图看图体验与面板空间调度，补全低阶怪技能与秘籍掉落。',
    sections: [
      {
        category: 'feature',
        items: [
          '传音聊天支持直传图片与点击大图灯箱预览，新增表情包管理删除与过期自动重传机制；传音与日志面板标题栏右上角新增扩展/收起按钮。',
          '为 {{enemy:enemy.demonized-rabbit|魔化野兔}} 与 {{enemy:enemy.armored-bear|铁甲熊}} 补齐 {{ability:ability.heavy-slash|重斩}}、{{ability:ability.chain-strike|连环}} 技能及 {{item:item.tome.heavy-slash|《重斩秘籍》}}、{{item:item.fragment.heavy-slash|《重斩·残卷》}} 等秘籍掉落。',
        ],
      },
    ],
  },
]

type StorageReader = Pick<Storage, 'getItem'>
type StorageWriter = Pick<Storage, 'getItem' | 'setItem'>

function defaultStorage(): Storage | null {
  return typeof window !== 'undefined' ? window.localStorage : null
}

/** 解析当前最新带有更新公告弹窗标识的日志条目；若历史或近期版本未设 noticeId，则返回 null。 */
export function resolveLatestNoticeEntry(entries: readonly ChangelogEntry[] = CHANGELOG_ENTRIES): ChangelogEntry | null {
  for (const entry of entries) {
    if (entry.noticeId && entry.noticeId.trim().length > 0) {
      return entry
    }
  }
  return null
}

/** 读取玩家已关闭过的更新公告 noticeId 集合（仅解析合法字符串数组，容错损坏的本地存储）。 */
export function readDismissedNoticeIds(storage: StorageReader | null = defaultStorage()): Set<string> {
  if (!storage) return new Set()
  try {
    const raw = storage.getItem(CHANGELOG_DISMISSED_STORAGE_KEY)
    if (!raw) return new Set()
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return new Set()
    return new Set(parsed.filter((item): item is string => typeof item === 'string' && item.trim().length > 0))
  } catch {
    return new Set()
  }
}

/** 持久记录玩家已读并关闭的更新公告 noticeId。 */
export function dismissChangelogNotice(
  noticeId: string,
  storage: StorageWriter | null = defaultStorage(),
): Set<string> {
  const next = readDismissedNoticeIds(storage)
  const trimmed = noticeId.trim()
  if (!trimmed) return next
  next.add(trimmed)
  if (!storage) return next
  try {
    storage.setItem(CHANGELOG_DISMISSED_STORAGE_KEY, JSON.stringify([...next]))
  } catch {
    // 无痕模式或存储满额时降级为当前会话内已关闭，不阻断主流程。
  }
  return next
}

/** 判断某期带公告标识的更新日志是否需要向当前玩家首次自动弹窗。 */
export function shouldShowChangelogNotice(
  entry: ChangelogEntry | null | undefined,
  dismissedIds: ReadonlySet<string>,
): boolean {
  if (!entry?.noticeId) return false
  return !dismissedIds.has(entry.noticeId)
}
