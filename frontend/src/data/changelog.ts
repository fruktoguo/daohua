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
    date: '2026-11-26',
    versionLabel: 'v0.3.16 ~ v0.3.23',
    title: '追忆副本与模拟器推演、符阵技艺伤害、大地图移速与攻防强化平衡',
    summary: '上线追忆副本十波闯关及模拟器追忆副本推演、符箓与阵法技艺每级伤害增幅、境界大地图移速加成、强化高阶成功率收敛与互斥消耗品、紧凑斗法记录、礼包兑换码（附公开码 dhtc，更多福利见官方 QQ 群 940886387）、道友攻略与灵石打赏、配装付费查看及剑杖攻防平衡。',
    noticeId: 'notice-20261126-v0.3.22',
    sections: [
      {
        category: 'feature',
        items: [
          '战斗模拟器新增「追忆副本」推演模式：与野外讨伐共享 1~5 人配置，支持多选追忆副本及虚境三档、现世八阶共 11 档难度，按挑战次数推演并统计通关率、止步波次、等效耗体及每点体力与每小时修为、净产值对比。',
          '新增「历练 → 追忆副本」十波闯关玩法：驻留青云古涧、古道断界、星陨古墟、雾梦剑冢所在地点即可单人或组队挑战（支持助战模式，不耗体力不计收益）；开放虚境三档与现世八阶共 11 档难度（现世难度怪物等级随队伍最高境界动态跃升），通关必掉 {{item:item.qingyun-secret-crystal|青云秘晶}}、{{item:item.boundary-spirit-shard|断界灵魄晶}}、{{item:item.starfall-astral-core|星陨玄核}}、{{item:item.wumeng-sword-soul|剑冢凝魄石}}，用于炼器锻造 {{item:equipment.changhu-pendant|伥虎镇魂佩}}、{{item:equipment.golden-ape-cloak|金背撼岳甲}}、{{item:equipment.starfall-crown|星陨镇灵冠}}、{{item:equipment.wuyin-spirit-sword|雾隐青冥剑}}（现世各阶亦有概率直接掉落成品）。',
          '新增「战斗统计 → 斗法记录」明细回看：完整记录当前行动内最近 20 场同道斗法的逐次出手（单场上限 1000 次行动），涵盖普攻、功法、丹药符箓、阵盘反击、先天特质触发与气血灵力周期回复，清晰标注物理、术法、真实伤害与五行属性及暴击、闪避、破招、化解状态。',
          '新增「礼包兑换」功能（「设置 → 账号 → 礼包兑换」）：输入兑换口令即可直接领取对应道途好礼。本次附赠公开兑换码 dhtc（不区分大小写），其余活动与隐藏兑换码请道友前往官方 QQ 群 940886387（也可于顶栏或「设置 → 关于」快捷复制群号）获取！',
          '新增「道友 → 攻略」板块与打赏体系：任一个人排行榜位列前 50 名的修士每日可撰写发布 1 篇万字图文攻略（支持排版、内置表情、颜文字、插图及物品/地点悬浮图鉴引用）；支持按热度与打赏双排序，作者可设免费或 1~100,000 {{item:currency.spirit-stone|灵石}} 解锁门槛，道友亦可向心仪攻略自由打赏（解锁与打赏扣除 10% 手续费后 90% 实时入账作者）。',
          '新增「设置 → 隐私」付费查看装配：角色名片配装可见性升级为「公开 / 付费查看 / 完全隐藏」三态；选择付费查看后，非同队修士查看名片时可支付设定灵石解锁当日查看权限（维持至北京时间当日零点）。',
          '新增筑基期专属「根基」常驻养成：30 级半步筑基满修为后可消耗修为凝练根基，每点根基提供 +1% 全六维加成（筑基破境需任意有效六维达 99），上限从 30 点起随小、中、大境界突破阶梯拓展。',
          '新增完整英文语言包与多语言切换支持；宗门「同门」列表升级为「在线 / 挂机中 / 已离线」三态展示，超时彻底离线的成员将显示简化离线时长。',
        ],
      },
      {
        category: 'balance',
        items: [
          '符箓与阵法技艺新增对应伤害成长：每级符箓技艺提供 +1% 符箓伤害增幅（作用于 {{item:item.leiji-talisman|雷击符}} 等伤害型符箓），每级阵法技艺提供 +1% 阵法伤害增幅（作用于 {{item:item.tianlei-formation-disk|天雷阵盘}}、{{item:item.xuesha-formation-disk|血煞阵盘}} 等伤害型阵盘反击，不影响非伤害效果与维系代价），于内视技艺与战斗面板同步展示。',
          '收敛装备高阶强化成功率并启用辅助消耗品互斥：秘法技艺等级高于强化目标等级时，仅对超出部分每级乘算提升 0.5% 成功率，且 +11 及以上单次强化成功率封顶 45%；同类强化成功率辅助消耗品（如 {{item:item.cuiling-powder|淬灵粉}} 与 {{item:item.guling-sand|固灵砂}}）改为互斥不可叠加，配置新物品将自动顶替旧槽位，已同时装载的自动保留高阶物品。',
          '校准剑修与杖修输出平衡：上调 {{ability:ability.heavy-slash|重斩}}、{{ability:ability.chain-strike|连环}}、{{ability:ability.heavy-clouds|重云}}、{{ability:ability.star-piercer|贯星}}、{{ability:ability.zhanyu|斩玉}}、{{ability:ability.shunshan|瞬闪}}、{{ability:ability.pojun|破军}} 七门物理伤害功法的倍率与基础伤害；同时将全部法系武器、防具与副手附带的技能增幅点数调整为同装备法术增伤点数的一半，使同境界剑修与杖修清怪效率回归均衡。',
          '全服攻防与特质平衡：玩家境界基础护甲、法抗及体魄、经脉提供的固定双抗翻倍，装备生命等防御属性强化（装备固定回血按穿戴者中境界分段阶梯成长）；同步重调特质品阶阶梯与双刃剑负面补偿，修复高品阶倒挂并将含双攻词条的特质一级数值减半。',
          '调整输出装备百分比主攻与固定主攻权衡，将部分主攻预算挪入既有防御词条；逐级妖兽共享属性基准同步装备攻防配平，整体历练攻防节奏更稳健。',
          '开天门高灵根约束：当总灵根点数超过 300 时，须先将全部可用灵根点数分配完毕方可开天门（300 点及以下保持原规则）。',
        ],
      },
      {
        category: 'optimize',
        items: [
          '新增大地图境界移速加成：每晋升一次中境界大地图移动速度提升 +5%，每突破一次大境界提升 +30%（大境界入口不重复计入中境界，线性叠加），大地图面板与启程确认弹窗同步展示当前移速倍率与折算耗时。',
          '传音社交体验升级：支持消息引用回复与便捷分享，改为点击气泡本体展开操作弹层并将复读与点赞胶囊行内并排，浏览更紧凑顺手。',
          '组队讨伐默认锁定功法与装备切换防止战斗中误操作；坊市挂单缺少档位时默认填入参考价，战斗详情页窄屏效率条独立成行，更新日志页新增固定页头并支持列表内滚动。',
        ],
      },
      {
        category: 'fix',
        items: [
          '修复并发点击未读传书时红点偶发滞后、宗门驻地与历史战报多语言文本漏译及英文界面属性提示高亮问题。',
          '实装斗地主开局底注托管担保，加固坊市盘口报价刻度校验与过期卖单挂单放行；修复装配栏文字裁切、战斗面板上限收缩卡滞、断线重连日志恢复与登录错误提示多语言显示等稳定性问题。',
        ],
      },
    ],
  },
  {
    date: '2026-11-18',
    versionLabel: 'v0.3.15',
    title: '秘法化灵等级挂钩与战斗重连稳定性优化',
    summary: '调整秘法化灵的单轮耗时与成功率规则以匹配物品等级，修复断线重连时阵盘与技能冷却恢复偏差及切换账号状态隔离问题。',
    noticeId: 'notice-20261118-v0.3.15',
    sections: [
      {
        category: 'balance',
        items: [
          '秘法「化灵」单轮耗时与成功率改为按物品基础等级（不含装备强化加值）计算：基础耗时调整为 5 + 0.3 ×（物品等级 - 1）秒（1 级物品仍为 5 秒）；基础成功率 100%，当秘法技艺等级低于物品等级时，每低 1 级成功率降低 5%（最低 20%）。化灵失败时物品焚毁且不产出 {{item:currency.spirit-stone|灵石}}，当轮秘法技艺经验与神识经验照常获得，秘法界面同步展示实时耗时与成功率。',
        ],
      },
      {
        category: 'fix',
        items: [
          '修复断线重连或离线恢复战斗时 {{item:item.juling-formation-disk|聚灵阵盘}} 等阵盘触发时机与技能冷却偶发偏差的问题，并修正多步自动赶路恢复与功法残卷优先入队的边界校验。',
          '优化切换账号时的传书、签到与传音状态隔离，修复连续查看多名同道角色名片时偶发信息串显的问题。',
        ],
      },
    ],
  },
  {
    date: '2026-11-17',
    versionLabel: 'v0.3.11 ~ v0.3.14',
    title: '字符挂机终端、斗法消耗品实装与体验优化',
    summary: '新增纯字符命令行挂机视图与战斗特效开关，斗法全面支持战斗消耗品与完整受击表现，升级表情包跨设备同步并修正强化装备回收折算。',
    sections: [
      {
        category: 'feature',
        items: [
          '「设置 → 显示」新增纯字符命令行挂机视图：以无图像的简洁文字面板实时展示境界修为、气血灵力、行动进度、字符血条、掉落记录与补给余量，右上角支持一键切换中英文指标显示。',
          '同道斗法全面接入战斗自动消耗品（丹药、符箓及 {{item:item.tianlei-formation-disk|天雷阵盘}}、{{item:item.xuesha-formation-disk|血煞阵盘}} 等阵盘），按实际触发消耗库存，并禁止在初入搜寻与重伤复活期间发起或应战斗法。',
          '传音表情包上限提升至 100 个，支持跨设备同步与批量勾选管理；新增服务器更新维护提示页，发版期间可自动检测恢复并刷新进入游戏。',
        ],
      },
      {
        category: 'optimize',
        items: [
          '战斗详情面板左上角新增「特效」开关（关闭后仅保留伤害数字）；切出浏览器标签页或后台返回时自动清理积压动画，避免画面瞬时卡顿。',
          '优化每日签到气运抽取曲线，降低连续抽到低档签文的概率，连签收益更平稳。',
        ],
      },
      {
        category: 'balance',
        items: [
          '修正高阶强化装备的系统回收价折算规则：强化折旧改为按该装备可用保护材料的最低单价计算，修复 {{item:equipment.duanchen-blade|断尘残刃剑}} 等高基础价值装备强化后回收价异常虚高的问题。',
        ],
      },
      {
        category: 'fix',
        items: [
          '补全斗法结算回放中的伤害、暴击、破招、招架、治疗及尾帧受击表现；修复多项极端情况下的战斗状态清理与坊市挂单结算稳定性问题。',
        ],
      },
    ],
  },
  {
    date: '2026-11-16',
    versionLabel: 'v0.3.8 ~ v0.3.10',
    title: '雾梦泽秘境开放、五门新功法与每日签到上线',
    summary: '开放第四张历练地图「雾梦泽」与五门二阶功法，上线每日签到领功德系统，补齐全部缺失图标与心法护体特效并放开讨伐行动境界限制。',
    sections: [
      {
        category: 'feature',
        items: [
          '开放新地图「雾梦泽」（24~30 级，由 {{location:node.scorched-pines|畸木焦林}} 向北前往 {{location:node.mist-edge|泽畔荒径}}、{{location:node.venom-fen|毒瘴浅沼}}、{{location:node.mist-heart|迷雾泽心}}、{{location:node.sword-abyss|剑冢沉潭}}），开启 {{action:combat.sweep-wumeng-marsh|讨伐 雾梦泽}}，迎战精英 {{enemy:enemy.abyss-black-croc|寒潭玄鳄}}、{{enemy:enemy.nine-head-guiche|九首鬼车}} 与镇渊妖王 {{enemy:enemy.mist-sword-wraith|雾隐剑魄}}，可掉落 {{item:item.black-croc-scale|玄鳄逆鳞}}、{{item:item.guiche-crimson-plume|鬼车赤翎}}、{{item:item.dragon-pearl|龙珠}} 等奇珍材料。',
          '新增五门二阶主动功法及对应秘籍残卷：{{ability:ability.tianlei-po|天雷破}}（命中附加 {{buff:buff.mabi|麻痹}}，降低闪避、招架与行动速度）、{{ability:ability.zhuoxin|灼心}}（叠加大额 {{buff:buff.ranhun|燃烧}}）、{{ability:ability.shunshan|瞬闪}}（联动 {{buff:buff.yixing|移形}} 并在消耗移形击杀时重置冷却）、{{ability:ability.shangui-yao|山鬼谣}}（群体施加 {{buff:buff.ganlu|甘露}} 持续回血）、{{ability:ability.pojun|破军}}（横扫群敌并获得移形与减伤状态 {{buff:buff.shibukedang|势不可挡}}）。',
          '新增「每日签到」系统（位于「其他 / 更多 → 签到」，每日首次上线或跨零点自动弹出）：每日可抽取 10~999 {{item:item.gongde-bound|功德·绑定}}，连续签到可稳步提升期望收益并查看签运。',
        ],
      },
      {
        category: 'optimize',
        items: [
          '补齐新地图妖兽立绘、奇珍、功法秘籍与宗门大殿等 35 项美术图标；补全十门内功心法的周天护体光效及 {{trait:trait.xuyi|蓄意}} 特效，并重做五门新功法的战斗演出。',
          '放开各地图讨伐行动的境界解锁门槛（原等级改为推荐参考等级，道友可自由越级挑战），并将全部秘籍与残卷的物品等级、品阶与最低掉落妖兽对齐。',
        ],
      },
      {
        category: 'balance',
        items: [
          '上调 {{item:item.storage-bag.broken|破损的储物袋}} 的功德产出（20% 概率获得 10~20 {{item:item.gongde|功德}}，1% 概率获得 100~300 功德），并简化物品卡片上的掉落概率展示。',
          '18 级以上的妖兽按超出等级获得全属性成长加成（普通与精英每级 +2%，妖王每级 +3%），使中高阶历练强度与道友特质、灵根成长相匹配。',
        ],
      },
    ],
  },
  {
    date: '2026-11-15',
    versionLabel: 'v0.3.7',
    title: '仙门传书、修行帮助指南与百艺减负调优',
    summary: '上线顶栏仙门传书邮件与五章修行帮助手册，百艺生活技艺经验全面翻倍并同步产出底蕴，补齐剑系武器物理增伤与多项界面体验细节。',
    sections: [
      {
        category: 'feature',
        items: [
          '新增「仙门传书」邮件系统（顶栏右上角常驻传书入口与未读红点），支持阅读传书、一键领取灵石功德等附件奖励，重要传书支持长期保留。',
          '「其他 / 更多」分组新增「帮助」修行指南弹窗，收录五章十四节图文教程，新手引导完成或跳过后可随时查阅。',
        ],
      },
      {
        category: 'optimize',
        items: [
          '优化「开天门」竖屏双页签布局（天门 / 加成切换）并新增首次规则说明弹窗；大地图跨区域关口节点新增醒目的青玉色「交界」角标，竖屏战斗详情支持一键全屏专注浏览。',
          '顶部行动栏新增剩余总耗时与当前材料可支撑次数估算；更新日志与战报中的功法、特质、建筑、状态词条全面支持悬浮查看图鉴详情。',
        ],
      },
      {
        category: 'balance',
        items: [
          '生活百艺减负提速：采集、炼丹、锻造、制符、阵法等技艺经验获取全面翻倍，消耗下级装备的进阶锻造再享双倍经验，且每次获得技艺经验均等额增加底蕴。',
          '为 {{item:equipment.qingfeng-sword|青锋剑}}、{{item:equipment.xuantie-sword|玄铁长剑}}、{{item:equipment.pogang-sword|破罡剑}}、{{item:equipment.duanchen-blade|断尘残刃剑}} 补齐物理伤害加成词条，与同阶法器强度对齐；精英与妖王击杀经验倍率分别提升至 10 倍与 40 倍。',
        ],
      },
    ],
  },
  {
    date: '2026-11-14',
    versionLabel: 'v0.3.3 ~ v0.3.6',
    title: '天榜排行、演武特效升级与大地图重塑',
    summary: '新增天榜竞争与配装隐私保护，全面升级战斗技能演出与大地图节点视效，并实装移形/灼烧新流派体系。',
    noticeId: 'notice-20261114-v0.3.6',
    sections: [
      {
        category: 'feature',
        items: [
          '新增「天榜」系统，整点刷新修为、战力与百艺榜名录；设置中新增配装隐私开关，可隐藏自身装备与功法配置。',
          '战斗技能与普攻连段特效全面重制，新增天品特质 {{trait:trait.jianyuxin|剑与心}}（12 点消耗）、{{trait:trait.huoshen|火神}}（12 点消耗）及功法 {{ability:ability.yueqian|跃迁}}、{{ability:ability.zhanyu|斩玉}}、{{ability:ability.huoyan-fengbao|火焰风暴}}，支持 {{buff:buff.yixing|移形}} 变招（踏云）与 {{buff:buff.ranhun|燃烧}} 叠加灼烧。',
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
          '修复战斗触发条件判定、组队收益结算与离线挂机掉率边界异常；统一全界面国风矢量与手绘图标，并同步万象图鉴条目。',
        ],
      },
    ],
  },
  {
    date: '2026-11-13',
    versionLabel: 'v0.3.2',
    title: '战斗模拟器提速与三阶修炼开放',
    summary: '大幅提升战斗模拟器推演响应速度，补全高阶吐纳行动并修正敌方施法频率与小游戏结算细节。',
    sections: [
      {
        category: 'feature',
        items: [
          '重构内置战斗模拟器：无需等待加载即可秒级完成多地点演武测算与收益对比，结果与实战完全一致。',
          '新增 9 级三阶修炼行动 {{action:cultivation.ningqi-guiyuan|凝气归元}}（10 秒/轮，获得基础修为并额外恢复 +40 灵力，附带经脉/根骨各 50% 根基经验）。',
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
    summary: '重整特质生效与同道界面结构，加强前期法术伤害并优化日常更新在线体验。',
    sections: [
      {
        category: 'feature',
        items: [
          '战斗特质支持按条件入战生效（{{trait:trait.chujian|初见}}、{{trait:trait.bukandaowo|看不到我}}、{{trait:trait.woyaodashige|我要打十个}}、{{trait:trait.dantiaowang|单挑王}}、{{trait:trait.xuyi|蓄意}}），并在装配视图增加特质栏与装备槽位排序。',
          '同道页签收拢进统一导航栏，斗地主新增全屏模式与倒计时校准；移动端支持高清美术资源按需更新。',
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
          '修复秘法感悟经验等级计算偏差、{{item:item.cuiling-powder|淬灵粉}} / {{item:item.guling-sand|固灵砂}} 对非强化行动误扣次数及组队复活读条跨波次残留的问题。',
          '优化日常版本更新兼容机制，常规更新不再强制中断在线道友的登录状态。',
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
