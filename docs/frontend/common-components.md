# 前端公共组件与统一模板速查手册

> **开发规范要求**：
> 1. 进行任何前端开发前，**必须先查阅本手册**，核对是否存在适用的公共组件或统一模板，避免重复造轮子或各写一套样式。
> 2. 凡是将某类 UI/交互收拢为统一模板，**必须同步登记至本手册**。
> 3. 本手册**不记录具体 CSS 样式实现代码**，仅作为**快速索引与场景查找表**；具体用法、参数和样式直接查阅所列路径。

---

## 1. 悬浮提示、信息卡片与浮层菜单 (Hover / Click Floating Popovers)

- **适用场景**：
  - 极简单行/短文文字悬浮提示，移走即消，点击穿透底层（0 阻碍） -> `FloatingTooltip`
  - 标准法宝、装备、物品、功法、属性说明卡片（含品质色、境界徽章、属性键值网格、词条、副卡片组合） -> `FloatingInfoCard` / `FloatingPopover`
  - **装备/道具属性加成修仙色系统一收拢**：装备与道具悬浮卡片中的属性加成条目统一收拢为 `PillBonusLabel`，通过 `STAT_TONE_MAP` 字典与内视属性面板（`InsightPage`）中的修仙色彩（`PillTone`）完全对齐（赤红物攻、靛紫法攻、翡翠绿气血、灵气青灵力、青铜护甲、紫罗兰抗性、天蓝命中、水绿闪避、道金暴击、玄岩灰免暴韧性、琥珀橙破招、银白化解等），避免原先因 `+` 加号统归为粉红色产生的歧义与误导。
  - **攻击间隔规则收拢**：修仙规则中装备不具备“攻击间隔”属性，服务端配表映射与前端悬浮展示已彻底移除。
  - **通用独立数量副气泡联动**：传入 `quantity={amount}` 属性，自动在主卡片旁附加独立的数量副气泡（标题为「数量」，内容为中文四位分段「具体数值」）。
  - **通用属性数值加成独立副气泡窗口 (Bonus Aside Cards)**：支持通过 `createBonusAsideCard`（入参 `id`, `mark`, `title`, `subtitle`, `tone`, `items: BonusAsideItem[]`）与 `BonusAsideList` 列表渲染组件，将装备属性、丹药加成（修炼、采集、炼丹、药效时长）以及实时 Buff/Debuff 统一转化为工整标准的加成列表。每一行严格对齐装备属性规范：**[极简二值小图标 StatGlyph] + [属性名胶囊 capsule-label] + [加成数值 underlined-value（固定数值或百分比均等宽粗体高亮右对齐）]**，装备属性加成逻辑抽象为 `extractEquipmentBonusItems` / `createEquipmentBonusAsideCard`，并在战场 Buff 小方块（`ArenaBuffSquare`）与内视属性面板（`CombatAttributePanel`）全量实装。
  - **功法书与招式残卷参悟副气泡联动**：针对 `item.tome.*` 与 `item.fragment.*`（或 `manual`/`manualFragment` 类别），自动检索全局战技配置，通过 `describeAbility(ability, 1)` 生成 `[秘籍参悟]` / `[残卷参悟]` 副卡片（色调 `qi`），挂载于悬浮主卡片旁联动展示（`resolveManualAsideCard`）。功法悬浮详情统一结构：主描述 = 配表 `description` 招式描述 + 效果句（目标/伤害构成/五行伤害类别，如「对单个目标造成 60%术法攻击 +10.5 木属性术法伤害」，构成单元经 `[[ ]]` 标记由 `ValueText` 渲染为整组胶囊），词条行 = 等级、冷却、吟唱、灵力消耗与次级效果，不输出默认触发条件文本。
  - **内功心法六维资质雷达图统一收拢 (`RootAspectRadarChart`)**：内功心法的六维（体魄/经脉/力道/根骨/身法/神识）资质加成与经验重分配彻底告别生硬文本，统一收拢为纯 SVG 矢量雷达图（`RootAspectRadarChart`），展示金虚线 100% 基准环、灵光青动态多边形、中心点与环周百分比数值（随心法等级动态放大，超常特化青蓝高亮、削减暗淡、绝弃危红），并在气泡、Aside 副卡与图鉴中无缝挂载。
  - **通用技能与技艺升级进度附属副气泡窗口 (Progress Aside Card)**：支持通过 `createProgressAsideCard` 与 `createAbilityProgressAsideCard`，在功法技能瓦片（斗法槽位、已学功法目录、选择器网格、内视面板）及左侧技艺栏二级菜单（炼丹/炼器/符箓/阵法/强化）hover 时，并排挂载独立的「升级进度」副气泡窗口。主卡片保持精简的效果叙述与当前加成属性（彻底剥离主悬浮信息内杂糅的经验数字），副卡片统一展示徽记（`[参悟]` / `[技艺]`）、当前等级与百分比副标、格式化经验差额（`当前进度：current / max`，满级显`已达最高等级`）以及带有前缘高光光斑与修仙五行色彩的渐变进度条（`.daohua-floating-aside-progress`）。
  - **通用描述/说明独立副气泡窗口 (Description Aside Card)**：支持通过 `createDescriptionAsideCard`（入参 `id`, `mark`, `title`, `subtitle`, `description`, `lines`, `tone`），在主卡片旁并排挂载独立的机制/规则说明信息窗。样式类为 `.daohua-floating-aside-desc`，文本支持纯文本与 `ValueText` 自动高亮，并维持紧凑优雅的微光卡片排版。首发场景落地于**内功心法六维资质倾向说明**（`createInternalRootExpAsideCard` / `resolveAbilityAsideCards`）：在功法神通面板装配槽与已学列表、功法网格待选列表、主界面战斗面板槽位、战斗属性面板、万象图鉴及储物袋/坊市秘籍残卷等全链路，挂载徽记为「倾向」、标题为「六维倾向说明」的灵气青修仙副气泡，清晰提示战斗时获取的六维经验会按照内功标注的六维百分比去增幅入账。
  - **可交易物品与装备「市场价格」坊市行情副气泡联动**：针对所有可交易标的（`item.isTradable` 物品或装备类标的），自动通过 `resolveMarketStatsAsideCard` 命中 `gameStore.marketStats` 缓存（按 `itemId|强化等级` 索引），渲染「市场价格」副卡（金色调 `gold`、右上角标「坊市」）。内部全面采用修仙微光胶囊徽章与数值高亮（`.daohua-market-aside-*`）：展示最低在售价（暖赤橙色调胶囊 `ask` 与单价/总量）、最高收购价（翡翠碧青色调胶囊 `bid` 与单价/总量）、系统回收价格（道金琥珀色调胶囊 `recycle` 与官价保底，装备不可回收清晰反馈）以及流通成交量（灵气青色调胶囊 `volume`，紧凑对比今日/本周数据），价格缺位以 `-` 优雅占位。数据源由 `gameClient` 在线期间以 15s 周期后台静默拉取 `GET /api/v1/market/stats`（小表全量读），悬浮渲染零即时网络开销，页面切后台跳过 tick。
  - **懒求值与密集长列表滚动性能保障**：`FloatingPopover` 具备 `isOpen` 短路懒计算机制，未展开时完全不执行 `content` 与 `mergedAsideCards` 的解析与 JSX 构建；在密集网格长列表（如坊市 `MarketBrowseGrid`）中，支持滚动抑制（`is-scrolling` 配合 `pointer-events: none`，命令式切换不经过 React 渲染）与视口渲染裁剪（`content-visibility: auto`）。非交互 hover 气泡（tooltip 语义）在**包含锚点的滚动容器**滚动时立即收起（等同原生 `title`）；与锚点无关的面板自滚（如底部日志 append 后自动滚底、侧栏滚动）不会触碰已打开的气泡。click/interactive 浮层滚动跟随则走 rAF 节流重定位，彻底消除滚动期间的逐事件 `getBoundingClientRect` 测量、整卡重渲染、`backdrop-filter` 重绘与高频 Hover 风暴。
  - **生态生灵悬浮详情统一收拢 (`MonsterTooltipTarget` / `MonsterInfoCard`)**：大地图地点生灵（`MapMonsterPill`）、附近面板生灵（`NearbyPanel.MonsterCard`）、讨伐生态面板（`CombatEcologyPanel.EcologyMonsterTile`）、万象图鉴妖兽收录（`CodexPage.CodexMonsterEntry`）与日志敌怪引用（`LogFeed.EnemyReference`）全面统一收拢走 `frontend/src/components/MonsterDisplay.tsx`。共享最高规格统一浮层——生命层次品质徽记（普通金/精英紫/领主危红）、境界等阶副标、修仙伤害流派与五行属性描述、出没周期、击杀修为收益、施展功法瓦片行（`MonsterAbilityTile`，与玩家技能格子同构，具备元素/类别丝带、Lv 等级角标、WebP 图标/名称与独立参悟详情浮层）、掉落物图元复合胶囊（`ItemDropReference`，含概率与掉落区间且支持二次悬停查物品）、六维基础战斗属性网格（气血/灵力/双攻/双防，已剔除多余攻击间隔）；彻底消灭生态面板双层 Popover 的体验割裂与多处面板同怪不同展的断层。
  - **角色名片弹窗全局收拢 (`PlayerCardModal`)**：同道队伍成员与地图人物附近修士点击事件统一唤起 `PlayerCardModal`，服务端按需投影白名单（形象、道号、境界牌匾、身家、六维根骨等级与进度、战斗六维基础属性、生活技艺等级以及全身装配），弹窗于顶层唯一挂载。
  - 点击/右键呼出的悬浮操作菜单、处置选项列表 -> `FloatingPopover`（菜单容器）、`FloatingMenuItem`、`FloatingMenuGroup`、`FloatingMenuDivider`
  - **右下角全局浮动气泡（自动 2.6s 消失）**：命令回执 `message` 是唯一服务端下发管道——成功文案按命令差异化（`EcsPlayerRuntimeAdapter.SuccessNotice`），高频或界面已自证的命令回空串静默；发送即知结果的操作（坊市挂单/撤单）在调用点就地 `setNotice` 即时提示。同文案重复提示经 `noticeSeq` 重置计时并重播入场动画 -> `Notice`（App.tsx）+ `.notice`（styles.css）
- **组件源码**：[`frontend/src/components/FloatingPopover.tsx`](../../frontend/src/components/FloatingPopover.tsx)
- **样式文件**：[`frontend/src/styles/floating-popover.css`](../../frontend/src/styles/floating-popover.css)
- **专项设计规范**：[`floating-popover.md`](./floating-popover.md)

---

## 2. 复合修仙标签与胶囊 (Pill Stat & Bonus Labels)

- **适用场景**：
  - **复合数值展示**（暗墨卡片底、图标、胶囊包裹名称、专属下划线高亮数值、可选内联经验进度文本与底边进度条、可选集成 Hover Tooltip） -> `PillStatLabel`
  - **属性加成/词缀标签**（无外边框轻量流式、胶囊名、专属色调下划线值、可选集成 Tooltip） -> `PillBonusLabel`
  - 支持修仙色调 (`PillTone`)：`gold`, `jade`, `qi`, `purple`, `amber`, `crimson`, `emerald`, `cyan`, `violet`, `indigo`, `teal`, `bronze`, `silver`, `slate`, `sky`, `muted`, `default`
- **组件源码**：[`frontend/src/components/PillLabel.tsx`](../../frontend/src/components/PillLabel.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.compound-stat-label`, `.compound-bonus-label`, `.pill-tone--*`)

---

## 3. 境界徽章与境界阶段牌匾 (Realm Badge)

- **适用场景**：
  - 角色/敌方境界标准牌匾展示（包含大境界牌匾框、境界阶段棱形徽章框【武/玄/天/初/中/后/极】、小境界灵气胶囊框，自带 Hover 弹出大境界与属性详情浮层） -> `RealmBadge`
- **组件源码**：[`frontend/src/components/RealmBadge.tsx`](../../frontend/src/components/RealmBadge.tsx)
  - **队伍角色卡惩罚角标 (`.party-malus-badge`)**：同道队伍成员卡片（`party-slot-card`）右上角展示触发队伍等级惩罚（MWI Level Malus）的醒目危红标签（如 `罚 -90%`），集成 `FloatingTooltip`（`tone="danger"`）展示具体说明；有队长踢人按钮时自动向左避让 24px。
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.insight-realm-*`)

---

## 4. 数值文本高亮与词条解析 (Value Highlighting & Diff)

- **适用场景**：
  - 战报日志、效果描述文本中自动识别正负数、百分比、数值单位，并自动包裹为修仙五行色彩的高亮小胶囊 -> `ValueText`
- **组件源码**：[`frontend/src/components/ValueText.tsx`](../../frontend/src/components/ValueText.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.value-capsule`, `.value-capsule--*`)

---

## 5. 面板二级导航与子选项卡 (Panel Sub-Tabs Navigation)

- **适用场景**：
  - 面板主分类下的二级切换栏（如内视根基下的「六维」与「基础」切换，采用紧凑暗墨胶囊按键与金色微光高亮）。
  - **坊市多级分类子导航与功法多维切换 (`MarketBrowseGrid`)**：在一级主 Tab（全部/材料/装备/消耗品/功法）下方，材料/装备/消耗品复用 `.insight-sub-tabs` 与 `.insight-sub-tab-btn` 展示二级子分类；功法主分类下采用专用的多维筛选器（`.market-manual-filters`），包含最左侧「全部」开关与三组左右二选一切换 Toggle（`.market-toggle-group` / `MarketSegmentedToggle`：术法/内功、物理/法术、全本/残卷）。当「全部」勾选激活时三组 Toggle 整体置灰但不改变已有勾选状态，取消勾选时三组 Toggle 恢复可用并执行 AND 复合精准过滤；在置灰态点击任意 Toggle 选项可自动解除「全部」激活状态并无缝应用该项筛选。
  - **万象图鉴多级分类导航 (`CodexPage`)**：在一级主分类（材料/技艺/建造/功法/妖兽）中材料居首；材料分类下复用 `.codex-sub-tabs` 与 `.codex-sub-tab-btn` 展示二级子分类（药材/矿石/异材），技艺分类下展示（炼丹/炼器/阵法/符箓），子类划分统一走 `isMaterialSubTabItem` 纯函数。
- **样式类名**：`.insight-sub-tabs`, `.insight-sub-tab-btn`, `.market-sub-tabs`, `.market-manual-filters`, `.market-toggle-group`, `.market-toggle-btn`, `.codex-sub-tabs`, `.codex-sub-tab-btn`
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.insight-sub-tabs`)

---

## 6. 物品方块与图元展示 (Item Tile & Display)

- **强制规则**：物品、装备、材料、消耗品、功法技能的一切展示位必须使用图标组件（整块瓦片 `ItemTile`、行内 `ItemIcon`、文本行/复合胶囊 `ItemReference`），禁止仅用纯文本名称罗列。
- **适用场景**：
  - 行囊、交易、装备槽位中的单个物品方块（包含品质光晕色调与边框、数量角标、内置 Hover 悬浮信息） -> `ItemTile`, `ItemTooltipTarget`
  - **左上品类丝带统一走 `resolveItemSubCategory`**：装备取穿戴槽位（武器/头部/衣服/下装/鞋履/饰品），其余取配表二级 `subCategory`（药材/矿石/丹药/秘籍/残卷/补给耗材等），功法书按 `item.tome./item.fragment.` 前缀兜底区分全本与残卷；所有展示位（背包、装备栏、选择器、坊市、悬浮详情）共享同一解析口径，装备栏不再叠加独立槽位角标。
  - **仅持有 itemId + 数量的行内场景**（账本物品得失、斗法暂存区、日志引用等）：统一渲染复合胶囊（行内小图标 + 名称胶囊 + `×数量` 下划线值 + 悬浮详情卡片） -> `ItemReference`（`amount` 提供时渲染 `item-reference-compound` 复合胶囊；`valueContent` 可覆写值区内容）
  - **消耗/产出条目（持有·需求对照）**：行动详情与强化预览的「物品 ×需求 / 持有/需求」行内条目统一用 `ActionDetailItem`（ItemDisplay 导出）；`current` 提供时显示 `持有 / 需求` 并在不足时以 `is-insufficient` 警示，缺省仅显示 `×需求`。
  - **怪物掉落条目（概率 + 数量区间）**：怪物悬浮/详情卡的掉落明细——同款复合胶囊，`×数量` 值区展示掉落区间（min=max 显示单值否则 `min-max`）+ 胶囊尾部 `（概率%）` dim 小字，对应「先按 dropChance 判定再抽数量区间」语义 -> `ItemDropReference`（薄封装 `ItemReference`，概率样式见 `expedition.css` `.world-monster-drop-chance`）
  - **图标（有图用图、无图回退文本）**：传入资源 ID 即可。该资源在
    `frontend/src/assets/items/<资源ID>.{webp,png}` 有图时，`ItemTile` 用图片整块替换中间的文本名称；
    没有图时自动保持原文本渲染。行内文本/列表行等不适合放整块方块的场合，改用
    `ItemIcon`（无图时不渲染任何节点）。新增图标只需往该目录丢文件，无需维护清单 -> `ItemIcon`, `resolveItemIconUrl`
    - 资源 ID 即配表 `id`，覆盖 **物品/材料（`item.*`、`currency.*`）、装备（`equipment.*`）、功法战技（`ability.*`）** 三类。
    - **功法战技是例外，必须显式接线**：物品与装备的 ID 会作为 `itemId` 流经既有展示位而自动生效，
      而战技面板走的是 `ability.name`，需调用方自己传 `ability.abilityId`
      （接入点：`AbilitiesPanel` 装配槽瓦片 + 已学功法列表，`AbilityGridPicker` 网格瓦片，`App.tsx` 战斗面板槽位）。
    - `.webp` 为运行时资源，**全项目只保留这一档：64×64**（由 `art/` 切图管线产出，脚本默认参数即 64）；
      `.png` 为早期历史图片，现已全部转出为 WebP 并移出该目录。注册表仍同时认两种后缀，同名时 `.webp` 优先。
    - **资产分层约定**：非运行时位置只留**原始分辨率** —— AI 原图在 `art/source/`，
      另有 26 张早期图标的**唯一高清副本**在 `art/legacy_128/`（其原图在本机已不存在，不可删）。
      切图中间档（256 工程母版）**不常驻**，需要时由 `art/sheets.json` 一条命令重建。
      详见 [`TASK-20260913-06.md`](../tasks/archive/archive-2026-09.md)。
    - **编码档位**：**降色 256 + 无损 WebP**（`--web-palette 256 --web-lossless`）。
      90 张合计 **168.1 KB**：同口径下比原先的有损 q90（267.2 KB）**小 37%**，可见像素 RGB 还高 4.13 dB。
      有损 WebP 会把**边缘半透明像素的 RGB** 带偏（表现为轮廓发白、整体褪色发灰），降色 + 无损没有这个毛病。
      换编码档位前先读 [`TASK-20260913-07.md`](../tasks/archive/archive-2026-09.md) 的实测对照表。
  - **图标渲染统一关闭线性插值（`image-rendering: pixelated`）**：三处图标 `<img>` 都用最近邻缩放，
    保留像素质感（改动前是浏览器默认双线性，会发虚）。位置：
    `.item-tile-icon`（[`selection-grid.css`](../../frontend/src/styles/selection-grid.css)）、
    `.item-reference-icon`（[`styles.css`](../../frontend/src/styles.css)，16px）、
    `.daohua-floating-card-title-icon`（[`floating-popover.css`](../../frontend/src/styles/floating-popover.css)，24px）。
    瓦片绘制区固定为 52px，故 64×64 源在 DPR1 是**缩小** 0.81×、在 DPR2 是**放大** 1.63× —— 两种密度均已实测无走样。
    **但若源图升到 ≥128px，缩小倍率会明显变大（DPR1 约 2.46×），届时必须重新实测这三处。**
  - **hover 反馈零重绘架构**：瓦片子树不做任何 `transition`——实测密集网格 hover 时任意 transition（含 transform）都会逐帧主线程重绘（79 格扫过约 19fps）。动效全部由合成器线程承担：辉光为常驻 `.item-tile-glow` 叠加层做 `opacity` fade-in（`ItemTile` 内置；裸写 `.item-tile` 的功法槽/功法挑选器需手动补 `<span class="item-tile-glow" aria-hidden="true" />`），丝带与角标缩放走 keyframe `animation`（`item-ribbon-zoom-in` / `item-corner-zoom-in`），边缘扫光保持 transform+opacity keyframes；绘制类属性（丝带底色、角标 `box-shadow`）瞬时切换。选中态辉光为静态 `opacity: 1`。
  - **强化装备 `+N` 资产变体**：钱包以 `variant="+N"` 区分强化等级资产行。展示链路统一约定——`parseEnhancementVariant(variant)` 解析等级；`ItemCard`/候选条目名称显示 `装备名 +N`；`ItemTooltipTarget` 接收 `enhancementLevel`，`describeEquipmentEffects` 按 `enhancementBonus × level` 在主卡追加 `(+强化加成)` 后缀；已装备槽位名同样带 `+N`。任何新增装备展示位必须沿用该口径，不得自行拼字符串或丢等级。
  - **坊市装备强化等级盘口选择与开单弹窗联动**：装备标的在坊市选中后先进入 `MarketEnhancementPicker`（`market-enhance-*` 四列网格行：物品瓦片 + 悬浮详情按 `enhancementLevel` 缩放 + 该变体行持有量 | 最佳出售价+购买 | 最佳收购价+出售 | 查看全部进入该级盘口）；挂单弹窗（`MarketOrderModal`）在装备标的下内置强化等级字段与背包持存快捷芯片组，等级切换即时动态读取对应变体行背包库存与穿戴可用量（开单/持存/已装备拦截全部按 `(itemId, "+N")` 变体键对账），自动钳位最大可挂售量并异步拉取对应等级盘口深度，`market.place` 负载 `variant: "+N"`。
  - **多气泡联动、Buff/特殊机制副窗口与独立数量气泡**：
    - 主卡片展示品质、基础说明、即时回血回蓝、冷却时长与装备基础攻防；
    - 丹药持续 Buff、修炼/技艺效率加成提取为独立的 `[丹药增益]` / `[修炼增益]` 副卡片窗口；
    - 装备特殊机制提取为独立的 `[特殊机制]` 副卡片窗口；
    - 数量副气泡并排联动（标题为「数量」，内容为中文四位分段「具体数值」）。
- **组件源码**：[`frontend/src/components/ItemDisplay.tsx`](../../frontend/src/components/ItemDisplay.tsx)
- **图标注册表**：[`frontend/src/game/itemIcons.ts`](../../frontend/src/game/itemIcons.ts)（构建期 `import.meta.glob` 扫描 `frontend/src/assets/items/` 的 `*.png` 与 `*.webp`，去扩展名的文件名即资源 ID）
- **构建约定**：[`frontend/vite.config.ts`](../../frontend/vite.config.ts) 的 `assetsInlineLimit` 对 `assets/items/` 下的资源返回 `false`（禁止内联成 base64）。原因：注册表是 eager 全量引用，默认 4KB 内联会让入口 JS 随图标数量线性膨胀（实测 64 张约 +240KB base64）。
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.item-tile`, `.item-reference-icon`, `.item-corner`), [`frontend/src/styles/selection-grid.css`](../../frontend/src/styles/selection-grid.css) (搜索 `.item-tile-icon`), [`frontend/src/styles/floating-popover.css`](../../frontend/src/styles/floating-popover.css) (搜索 `.daohua-floating-aside-*`, `.daohua-floating-card-title-icon`)

---

## 7. 网格选择器与目录拾取器 (Item Grid & Catalog Pickers)

- **适用场景**：
  - **通用待选列表**（贴靠目标槽位的浮层候选网格，取代全屏模态挑选：入参 = 设置目标锚点 `anchor` + 所有列表 `options`，`auto-fill` 自动判列，首格可带「空选/移除」，候选数超 `searchThreshold`（默认 12）才出现搜索框，选中/点外/ESC 即关闭，正在设置的槽位以 `is-picking` 高亮描边） -> `CandidatePicker`
  - **物品待选列表**（`ItemPickerEntry[]` 适配为候选：品质光晕瓦片 + 悬浮详情 + 持存 0 禁用 + 名称/品类/描述搜索文本） -> `ItemGridPicker`
  - **资产变体复合键纪律**：`ItemPickerEntry` 携带 `variant`（形如 `"+3"`）与 `enhancementLevel`；同 itemId 的多个强化等级资产行必须经 `itemPickerEntryKey(itemId, variant)` 复合键区分（与钱包 AssetKey 合入键同构），禁止仅以 itemId 去重或作选中态判定。`onSelect(itemId, entry)` 第二参回传完整条目，装备槽穿戴等需要保留强化等级的场景一律读取 `entry.enhancementLevel`，不得默认归 0。
  - **功法/战技待选列表**（功法装配槽位选择；按 `slotIndex` 过滤功法 `discipline`——0 号内功位只列内功心法，术法位只列术法；`levelFor` 提供参悟等级供瓦片角标与悬浮详情） -> `AbilityGridPicker`
  - **通用目录选择弹窗**（针对秘籍、配方、地图地点等条目的网格选择模板，仍为全屏 Modal；附带子导出 `PickerTrigger` 选择器触发按钮与 `ChoiceMenu` 下拉选项菜单，供触发条件编辑器等内嵌场景复用） -> `CatalogPicker`
- **组件源码**：
  - [`frontend/src/components/CandidatePicker.tsx`](../../frontend/src/components/CandidatePicker.tsx)（经 `FloatingPopover` manual 受控 + `anchorEl` 外部锚点贴槽定位，顶部方向优先、自动翻转）
  - [`frontend/src/components/ItemGridPicker.tsx`](../../frontend/src/components/ItemGridPicker.tsx) (含 `ItemUseSlot` 消耗品槽位组件，支持与技能/物品冷却完全一致的持续消耗品旋转填充倒计时遮罩 `item-slot-duration-overlay`；`onClick` 回传事件供调用方取锚点)
  - [`frontend/src/components/AbilityGridPicker.tsx`](../../frontend/src/components/AbilityGridPicker.tsx)
  - [`frontend/src/components/CatalogPicker.tsx`](../../frontend/src/components/CatalogPicker.tsx) (导出 `CatalogPicker` 主组件 + `PickerTrigger` / `ChoiceMenu` 子组件)
- **样式文件**：[`frontend/src/styles/selection-grid.css`](../../frontend/src/styles/selection-grid.css)（`.candidate-picker-*` 面板与网格、`.is-picking` 目标槽位高亮；单元格复用 `.item-grid-picker-card*` 视觉体系）

---

## 8. 数量步进与快捷选择器 (Quantity Selector)

- **适用场景**：
  - 市场交易、批量制造、行囊使用、派遣探索次数的选择（包含数字输入框、输入框左侧 `leadingPresets` 档位钮、右侧快捷预设数量按钮 `presets`、右侧「最大」钮 `maxShortcut`（点击填入 `max`）、可选无限 `∞` 按钮） -> `QuantitySelector`
- **组件源码**：[`frontend/src/components/QuantitySelector.tsx`](../../frontend/src/components/QuantitySelector.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.quantity-selector`)

---

## 9. 通用模态弹窗与确认对话框 (Modals & Confirm Dialogs)

- **适用场景**：
  - **通用全屏居中遮罩弹窗**（基于 React Portal 顶层挂载、Focus 焦点锁定陷阱、ESC 键响应、三种标准尺寸 `small`/`medium`/`large`） -> `Modal`
  - **二次确认 / 高危操作对话框**（重置、遗忘功法、摧毁物品、批量购买等，带取消/确认与高危危险样式） -> `ConfirmDialog`
  - 战斗技能触发条件编辑器弹窗 `CombatTriggerEditor` 见 §22，此处不再重复登记。
- **组件源码**：
  - [`frontend/src/components/Modal.tsx`](../../frontend/src/components/Modal.tsx)
  - [`frontend/src/components/ConfirmDialog.tsx`](../../frontend/src/components/ConfirmDialog.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.modal-backdrop`, `.modal-shell`, `.confirm-dialog`)

---

## 10. 状态动效与平滑进度 Hooks (Smooth Progress Hook)

- **适用场景**：
  - 战斗气血/真元条、行动倒计时平滑插值动画（DOM Ref 直写，零 React 重渲染），防止卡顿跳跃 -> `useSmoothProgressRef`
- **源码路径**：[`frontend/src/game/useSmoothProgress.ts`](../../frontend/src/game/useSmoothProgress.ts)

---

## 11. 通用文本与数值格式化 (Format Helpers)

- **适用场景**：
  - 灵石/大额数值格式化（万、亿、兆）、时长格式化、百分比格式化 -> `formatNumber`, `formatDuration`, `formatPercent`
  - 通用进度描述文本（如 `5000/1万 (50%)`、`66万/100万 (66%)`、`已圆满 (100%)`）-> `formatProgressText`
  - 大数自动改后缀与万进制紧凑展示（最多6位数字、最多2位小数、默认向下取整） -> `formatUnitNumber`
  - 中文大数完整四位分段格式化，最多展示小数点后两位（如 `1234亿 7777万 5211`） -> `formatChineseSplitNumber`
  - 获取数值完整拆解信息 -> `getUnitNumberInfo`
- **源码路径**：[`frontend/src/game/format.ts`](../../frontend/src/game/format.ts)

---

## 12. 国际化与文本词典 (i18n & Localization)

- **适用场景**：
  - **任何新增 UI 文本**（按钮、标题、提示、aria-label、placeholder、模板消息）——禁止在组件内硬编码中文/英文字符串，一律登记词典并用 `t()` 取值
  - 需要随语言切换即时刷新的渲染路径 -> `useT()`；事件回调、非渲染辅助函数 -> `getT()`
  - 带参数的模板消息（如「选择使用物品 · 槽 {n}」）-> 词典值写 `{name}` 占位符，调用 `t(key, params)` 内插
  - 语言切换 UI 与偏好持久化（localStorage `daohua.locale`，默认 zh-CN）-> `useI18nStore` / `LOCALES`
- **内核源码**：[`frontend/src/i18n/index.ts`](../../frontend/src/i18n/index.ts)
- **词典结构与多语言架构**：
  - **中文 TS 强类型源**：[`frontend/src/i18n/domains/`](../../frontend/src/i18n/domains/) 按业务域维护 `<域>.zh.ts`（common/app/insight/market/combat/game 六域，根键互不重叠），聚合入口 [`locales/zh-CN.ts`](../../frontend/src/i18n/locales/zh-CN.ts) 作为全量 `Messages` 与点路径 `TranslationKey` 静态类型基准与首屏零延迟内置源。
  - **全自动生成翻译模板**：前端执行 `npm run dev` 或 `npm run build` 时，全自动从中文 TS 源生成最新 [`frontend/public/locales/template.csv`](../../frontend/public/locales/template.csv)（`key,zh_source,translation`），供外部/社区翻译对照。
  - **外语 CSV 动态扩展**：支持直接在 [`frontend/public/locales/`](../../frontend/public/locales/) 放入第三方 CSV 语言包（如 `ja-JP.csv`），构建时自动注册并显示到设置页，前端按需异步拉取并缓存；新增文件后需重新构建。空翻译或加载中回退至中文树状词典。
- **约定**：跨页面高频词只入 `common` 域（confirm/cancel/close 等），页面域不得重复定义；服务端下发的游戏内容（物品名、行动名、日志模板等协议数据）不走前端 i18n

---

## 13. 基础修仙单位数值与自动换算组件 (UnitNumber)

- **适用场景**：
  - **大额数值/灵石/修为/战力紧凑展示**：支持万、亿、兆、京等阶梯自动改后缀，确保有效数字不超过 6 个字符，小数点后最多 2 位，默认向下取整。
  - **内置 Hover 气泡与四位分割**：鼠标悬停自动弹出轻量 Tooltip，以中文每四位分割清晰展示完整数字（如 `1234亿 7777万 5211`），最多展示小数点后两位，兼具修仙色调高亮与标准千分位对照。
- **组件源码**：[`frontend/src/components/UnitNumber.tsx`](../../frontend/src/components/UnitNumber.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.unit-number`, `.unit-number-tooltip-*`)

---

## 14. 日志实体引用、复合物品胶囊与重复项乘数胶囊 (Log Entity Reference & Repeat Capsule)

- **适用场景**：
  - **事件日志与战报中的实体胶囊引用**（包含功法技能 `ability`、妖兽敌人 `enemy` / `encounter`、角色 `character`、机制 `mechanic`、地点 `location`、行动 `action`，自带专属修仙微光色彩与悬浮信息浮层；机制胶囊支持 `break` 破招橙、`crit` 暴击金、`dodge` 闪避水绿、`parry` 化解冰蓝等修仙专属色调；行内全部胶囊统一垂直居中对齐）。
  - **复合物品胶囊**（将物品名称与数量如 `灵石 × 41` 组合为一整个不可分割的紧凑修仙药丸胶囊，包含物品品质色调、微光背景与下划线数量，支持悬浮弹出物品属性卡片） -> `ItemReference`, `.item-reference-compound`。
  - **完全相同日志连续折叠与乘数胶囊**（挂机采集、连续战斗等产生的完全相同模板日志在尾部自动合并显示微光乘数胶囊形如 `× 3`，更新为最新触发时刻并保留历史缓冲容量） -> `.log-repeat-capsule`, `collapseLogEntries`。
  - **底部传音与日志未读数字角标**（世界、附近、系统、好友、队伍、宗门与综合频道新消息未读提醒红点角标，上限 99+，点击切换后自动清零；战斗频道高频推进严格排除） -> `useLogUnreadStore`, `.log-tab-badge`。
- **组件源码**：
  - [`frontend/src/components/ItemDisplay.tsx`](../../frontend/src/components/ItemDisplay.tsx)
  - [`frontend/src/components/GameLogPanel.tsx`](../../frontend/src/components/GameLogPanel.tsx)
  - [`frontend/src/logging/gameLogService.ts`](../../frontend/src/logging/gameLogService.ts)
  - [`frontend/src/logging/logUnreadStore.ts`](../../frontend/src/logging/logUnreadStore.ts)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.log-reference`, `.item-reference-compound`, `.log-repeat-capsule`, `.log-tab-badge`)
---

## 15. 改名/改密弹窗、登录界面与独立连接状态屏幕 (Modal Form, Login & Connection Screens)

- **适用场景**：
  - **小型表单模态窗统一范式**：`Modal`（`size="small"`，`className="modal-form-shell"`）内放 `.modal-form` 表单——`.modal-form-field`（`.modal-form-label` + `.modal-form-input` + `.modal-form-error`/`.modal-form-hint`）与 `.modal-form-actions`（`.modal-form-cancel` + `.modal-form-submit`）。修改道号与修改密码共用此范式。
  - **修改修士道号弹窗**（含 2~12 字符校验、正则无特殊字符校验、查重与 24 小时改名冷却） -> `RenameModal`
  - **修改登录密码弹窗**（当前密码 + 新密码 + 确认密码三段校验，走 `/api/v1/auth/change-password`，成功后吊销其他设备会话） -> `ChangePasswordModal`
  - **独立全屏登录/注册界面**（无账号或登出时展示，支持输入账号、道号与可选访问令牌，自带粒子与修仙太极光晕） -> `LoginPage`
  - **独立全屏连接中与断网重试屏幕**（避免未连接时渲染默认 3 任务测试界面引发数据丢失疑惑，提供连接动画、重试与登出/切换账号入口） -> `ConnectingPage`, `ConnectionErrorPage`
- **组件源码**：
  - [`frontend/src/components/RenameModal.tsx`](../../frontend/src/components/RenameModal.tsx)
  - [`frontend/src/components/ChangePasswordModal.tsx`](../../frontend/src/components/ChangePasswordModal.tsx)
  - [`frontend/src/components/LoginPage.tsx`](../../frontend/src/components/LoginPage.tsx)
  - [`frontend/src/components/ConnectingPage.tsx`](../../frontend/src/components/ConnectingPage.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.modal-form`, `.login-screen`, `.status-screen`, `.status-card`)

---

## 16. 通用角色与妖兽立绘形象组件 (CharacterAvatar)

- **适用场景**：
  - **后缀无关智能形象加载**：仅需传入角色/妖兽标识 `actorId`（如 `demonized-rabbit`、`wood-spirit`、`fire-spirit`、`player`），组件自动在 WebP / PNG / SVG 格式间无缝回退加载。
  - **敌方左右镜像自适应**：支持 `isFlipped` 属性，敌方在战场卡片与属性详情模态窗（`CharacterAttributeModal`）中自动水平镜像翻转（`scaleX(-1)`）。
  - **三级优雅降级**：高清透明立绘图 (WebP/PNG/SVG) -> 原生纯 SVG 矢量修仙法相 (`VectorCharacter`) -> 单字徽章。
  - **阵亡状态置灰**：支持 `isDead` 状态半透明与灰度滤镜。
- **组件源码**：[`frontend/src/components/CharacterAvatar.tsx`](../../frontend/src/components/CharacterAvatar.tsx)
- **状态与解析引擎**：[`frontend/src/game/avatarStore.ts`](../../frontend/src/game/avatarStore.ts)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.character-avatar`, `.character-attribute-avatar-stage`), [`frontend/src/styles/expedition.css`](../../frontend/src/styles/expedition.css) (搜索 `.arena-avatar .character-avatar`)

---

## 17. 角色虚影装备阵列与技艺工具面板 (Equipment & Phantom Stage)

- **适用场景**：
  - **角色虚影与六部位装备环绕**：中间展示修士角色立绘虚影与灵光法阵，左三（头部/衣服/腿部）右三（武器/饰品/鞋靴）竖排对称排列，支持穿脱交互与网格拾取 -> `EquipmentPanel`
  - **技艺生活工具扩展槽位**：底部排布 7 个技艺工具专属槽位（采集/挖掘/炼丹/炼器/符箓/阵法/强化，槽位即 `tool-<techniqueId>`），点击呼出 `ItemGridPicker` 按槽位过滤穿脱，经 `loadout.replace` 上行；已装备工具为对应技艺行动提供 `techniqueBonus.speedRatio` 速度加成。
- **组件源码**：[`frontend/src/components/EquipmentPanel.tsx`](../../frontend/src/components/EquipmentPanel.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.equipment-view-panel`, `.equipment-stage`, `.equipment-slot-btn`)

---

## 18. 右侧多 Tab 容器与功法神通面板 (Right Sidebar & Abilities Panel)

- **适用场景**：
  - **右侧多 Tab 顶层容器**（聚合储物袋、装备、功法三大核心面板，支持平滑切换） -> `RightSidebar`
  - **储物袋分段陈列**：`InventoryPanel` 按「重要/消耗品/装备法宝/功法/材料」五组小标题分段展示物品网格（非 Tab），分组判定统一走 `resolveInventoryGroup`（物品目录 kind/category + 装备目录命中，itemId 前缀兜底），空组不渲染。
  - **储物袋物品交互单元**（背包格子完整交互收拢：悬停保留 `ItemTooltipTarget` 详情卡片；左键弹出 `FloatingPopover` 操作菜单——秘籍/残卷「参悟」(`ability.learn` 数量弹窗批量研读)、装备「装备」(`loadout.replace` 槽位互斥剔除)、可交易物品「交易」跳转坊市标的详情、`sellPrice > 0` 物品「出售」(`item.sell`)、任意物品「丢弃」(`item.discard`)；右键触发使用——秘籍/残卷进批量研读弹窗、装备直接穿戴；使用/丢弃/出售经统一 `Modal` + `QuantitySelector` 数量确认弹窗执行；已装备物品禁用丢弃/出售） -> `InventoryItemCell`
  - **功法神通面板**（1 首位内功心法 + 动态扩展术法槽位横排装配与触发器/等级徽标，冷却倒计时仅限战斗详情行动槽展示；已习得功法按内功心法/神通术法两组方块瓦片网格分列（`item-tile` 方形瓦片：左上元素丝带、右上 Lv 参悟等级、右下装配槽位角标）；槽位与功法 `discipline` 双向互斥——内功位只收 internal、术法位只收 spell；装配等级恒等于已参悟等级（由 `abilityExperiences` 立方曲线导出）；重复装配按「移动」处理——`assignAbilityToSlot` 把功法从原槽位移至新槽位（触发条件按功法 ID 跟随，见 §22 末尾）；所有 `loadout.replace` 发送点统一经 `sendLoadoutReplace` 归一化） -> `AbilitiesPanel`
- **组件源码**：
  - [`frontend/src/components/RightSidebar.tsx`](../../frontend/src/components/RightSidebar.tsx)
  - [`frontend/src/components/InventoryItemCell.tsx`](../../frontend/src/components/InventoryItemCell.tsx)
  - [`frontend/src/components/AbilitiesPanel.tsx`](../../frontend/src/components/AbilitiesPanel.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.right-sidebar-panel`, `.abilities-view-panel`)
---

## 19. 功法与效果悬浮气泡 (Effect Tooltip)

- **适用场景**：
  - 功法、词条、Buff/效果说明的 Hover 悬浮卡片（主标题 + 可选徽章 + 效果简述 + 详细词条行，词条行自动经 `ValueText` 做数值高亮） -> `EffectTooltipTarget`
  - 未提供 content 时自动降级为原生包裹 span，不产生额外 Popover 容器开销；`CatalogPicker` 目录选择弹窗已复用该组件展示条目提示。
- **组件源码**：[`frontend/src/components/EffectTooltip.tsx`](../../frontend/src/components/EffectTooltip.tsx)

---

## 20. 全局错误边界 (Error Boundary)

- **适用场景**：
  - React 子树渲染异常的全局兜底（捕获渲染期错误并展示降级界面，避免整页白屏）；已在 [`main.tsx`](../../frontend/src/main.tsx) 根级挂载，无需重复包裹。 -> `ErrorBoundary`
- **组件源码**：[`frontend/src/components/ErrorBoundary.tsx`](../../frontend/src/components/ErrorBoundary.tsx)

---

## 21. 原生矢量修仙立绘 (Vector Character)

- **适用场景**：
  - 缺少高清立绘资源时的纯 SVG 矢量人物/妖兽法相兜底渲染（`CharacterAvatar` 三级降级链路的中间层，见 §16；支持 `isDead` 阵亡置灰）。 -> `VectorCharacter`
- **组件源码**：[`frontend/src/components/VectorCharacter.tsx`](../../frontend/src/components/VectorCharacter.tsx)

---

## 22. 战斗技能触发条件编辑器 (Combat Trigger Editor)

- **适用场景**：
  - 功法自动施放触发条件的编辑与配置弹窗（支持自身/敌方/队友目标依赖，气血/真元比例或固定阈值条件，生效/未生效状态比较，最多 4 条逻辑与规则约束） -> `CombatTriggerEditor`
  - **触发条件跟随功法 ID 而非槽位**：`combat.abilityTriggers`（`abilityId → CombatTrigger[]` 映射）是玩家侧唯一的条件存储——卸下再装配、跨槽位移动同一功法时条件保留；已学功法目录瓦片与装配槽位均可打开同一功法维度的编辑器，编辑器标题用功法名。槽位字段 `CombatAbilitySlot` 不再携带 `triggers`。
  - **未展开时 Hover 浮现与展开时弹窗大按钮接管互斥机制**：
    - **未展开选择时**：瓦片（功法槽、已学功法、战斗详情消耗品槽）鼠标 hover/focus-within 时从上缘/下缘外侧浮现暗金微光条件胶囊（11px 字号、3px 9px 内边距、`::before` 扩充 34px×60px 连续热区防滑脱）；
    - **展开选择弹窗时**：槽位进入 `.is-picking` 状态，CSS 强制彻底隐藏瓦片上的 hover 条件按钮（消除遮挡与双按钮混乱），由选择弹窗（`CandidatePicker` / `AbilityGridPicker` / `ItemGridPicker`）顶部 Header 右侧呈现的**放大一倍大号「⚙ 触发条件」胶囊按钮**（高度 32px / 触屏 36px，字号 13px，金边微光，`has-triggers` 态微光高亮）全权接管，点击直接打开条件编辑器并关闭选择弹窗。
  - **战斗详情消耗品物品条件支持**：
    - 仅战斗详情页面内的自动消耗品槽位（`AutoUseSlotGroup bar="combat"`：药品栏与通用栏）支持触发条件配置，支持 Hover 浮现及展开选药弹窗时顶部大按钮配置；
    - 探索、炼丹、炼器等非战斗行动窗口的持续增益物品及行囊背包物品属于挂机或存取流，一律不显示任何条件入口，纯净无冗余。
  - **已学功法目录瓦片点击直达**：在已学功法列表中，支持直接轻点任意已学功法瓦片直达该功法的触发条件编辑器，触屏与桌面体验完全闭环。
  - **编辑态抗快照刷新**：编辑器打开期间外部 `combat` 快照刷新（战斗进行中/结束）不会重置未保存的编辑内容——`initialTriggers` 经 ref 中转，仅在弹窗打开瞬间同步一次。
- **组件源码**：[`frontend/src/components/CombatTriggerEditor.tsx`](../../frontend/src/components/CombatTriggerEditor.tsx)、[`frontend/src/components/CandidatePicker.tsx`](../../frontend/src/components/CandidatePicker.tsx)、[`frontend/src/components/AbilityGridPicker.tsx`](../../frontend/src/components/AbilityGridPicker.tsx)、[`frontend/src/components/ItemGridPicker.tsx`](../../frontend/src/components/ItemGridPicker.tsx)、[`frontend/src/components/ConditionedTile.tsx`](../../frontend/src/components/ConditionedTile.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.combat-trigger-editor`, `.combat-trigger-card`, `.combat-trigger-fields`)、待选弹窗大号条件按钮与瓦片框外互斥样式在 [`frontend/src/styles/selection-grid.css`](../../frontend/src/styles/selection-grid.css) (搜索 `.candidate-picker-condition-btn`、`.item-slot-inline-action--condition`)

---

## 23. 纯净行动卡片与品质等阶角标 (Action Card & Capsule Level Badge)

- **适用场景**：
  - 修炼、炼丹、炼器、采集与历练等交互行动的纯净微型展示卡片（标准每行 4 个网格排布，严格遵循“只显示名称，不展示多余图标或详情”规范，右上角复用标准 `item-corner item-corner--top-right` 内部胶囊角标，整张卡片与角标受 `data-rarity` 品质色彩驱动，点击唤起对应配置/执行弹窗） -> `ActionCard`
  - **真实等级换算**：未指定显式等级时，由 `convertTierToLevel` 统一将等阶换算为等级（等阶1->1级，等阶2->18级，等阶3->30级，等阶N->`18 + (N - 2) * 12` 级）。
  - **品质色彩联动**：由 `resolveActionRarity` 依据产出物/装备/遭遇解析品质色调（`mundane`/`spirit`/`mystic`/`earth`/`heaven`等），驱动边框微光与背景着色。
- **组件源码**：[`frontend/src/App.tsx`](../../frontend/src/App.tsx)
- **等级与品质解算引擎**：[`frontend/src/game/ids.ts`](../../frontend/src/game/ids.ts)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.action-cards`, `.action-card`, `.item-corner--top-right`)

---

## 24. 行动统计/记录面板与账本分区 (Stats Panel / Records Panel & Report Blocks)

- **适用场景**：
  - **三个入口三块面板**：`StatsPanel`（详情页右上角「统计」）只显示本次行动——当前行动账本 + 斗法暂存区；`CombatStatsPanel`（战场详情左上角浮层「战斗统计」入口，仅队首为战斗行动时显示，按钮与弹窗均收拢在 `CombatArena` 内）展示当前行动的单位级战斗统计——「我方/敌方」阵营页签 ×「输出/承伤/减免/治疗」指标页签，条形图把当前侧全部单位按所选指标总值降序做横向对比（条宽按该组合最大值归一化），伤害系三项物理红·术法蓝分段、治疗按自动回复翠·其他治疗紫分段并配同色图例；`RecordsPanel`（主区顶级页签 `'records'`：左侧栏底部「记录」按钮切换进入，移动端由详情页头部 `.records-open-mobile` 入口兜底）显示离线挂机 / 今日统计 / 近期战报历史。三本账后台照常记账，仅展示位置不同。战斗统计仅进行中账本携带 combatants，归档即弃。
  - **行动收益账本统一渲染**：共用组件收拢在 `reportSections.tsx`——`ReportBlock` 分区容器按固定顺序排三个 memo 小节且**不渲染节标题**：`ReportInfoSection`（行动信息：轮次/时长/遭遇/分级击杀/死亡/斗法胜负）→ `ReportItemsSection`（物品得失：获得与失去两列分组，复用 `ItemReference` 复合胶囊横排，与斗法暂存区同款）→ `ReportExpSection`（获取经验：修为/底蕴/六维/功法技能经验/等级提升）。
  - **空账本抑制**：`reportHasContent` 判定全零账本不渲染分区；`endedAtUnixSeconds` 决定时长口径（进行中=估算服务端当前时刻−开始，归档=结束−开始）。
  - **localStorage 历史缓存**：`battleReportStore` 读取时对旧缓存条目补齐账本新增字段默认值，防渲染崩溃。
- **组件源码**：[`frontend/src/components/StatsPanel.tsx`](../../frontend/src/components/StatsPanel.tsx)、[`frontend/src/components/CombatStatsPanel.tsx`](../../frontend/src/components/CombatStatsPanel.tsx)、[`frontend/src/components/RecordsPanel.tsx`](../../frontend/src/components/RecordsPanel.tsx)、[`frontend/src/components/reportSections.tsx`](../../frontend/src/components/reportSections.tsx)
- **缓存层**：[`frontend/src/game/battleReportStore.ts`](../../frontend/src/game/battleReportStore.ts)
- **样式文件**：[`frontend/src/styles/expedition.css`](../../frontend/src/styles/expedition.css) (搜索 `.battle-report-`、`.combat-stats-`、`.records-page`)；侧栏底部工具区 `.sidebar-footer` 在 `frontend/src/styles.css`

---

## 25. 斗法战斗属性统一面板 (CombatAttributePanel)

- **适用场景**：
  - 内视「斗法」Tab 与战场单位详情弹窗（`CharacterAttributeModal`，单击己方角色/敌怪头像呼出）共用同一属性面板，两组以 `.insight-sub-tabs` 二级导航 Tab 切换，外加可选「当前运转丹药与神通加持」Buff 分区：
  - **敌怪详情追加第三「掉落」Tab**：传入 `drops`（`MonsterDropDefinition[]`，由 `findCombatantInCatalog(encounters, maps, enemyId)` 从遭遇/节点生态静态目录解析）时渲染，内容为 `ItemDropReference` 复合胶囊列表（`图标 名称 ×数量区间（概率）`，容器复用 `.world-monster-drops-row`）；空列表展示「没有掉落物」空态，未传 `drops`（内视面板/玩家/斗法镜像）维持原两 Tab。
    - **基础**：气血/灵力/五秒回复、物理/术法攻击、护甲/法抗、命中/闪避/暴击/免暴/破招/化解（全部为点数原值，暴击/免暴不预折百分比）；
    - **特殊**：攻击速度/行动速度/冷却缩减（NonLinear 点数；怪物侧含同构基准分量，玩家仅装备/内功/特质/Buff 等获取来源）、暴击伤害（点数折算倍率显示）/普攻增幅/技能增幅/威胁增幅、治疗增幅/气血汲取/灵力汲取/伤害反弹（Linear 点数），**伤害增减**整组以 `insight-pill-group` 分组收在本 Tab 最下面（全部伤害/物理伤害/术法伤害 + 金/木/水/火/土 共 8 个条目，每条目以 `增伤 / 减伤` 成对显示）；出手间隔为系统常量机制，不上面板；
    - **默认值隐藏**：等于默认值的属性不渲染——点数类修饰与基础数值默认 0（1e-9 容差吸收浮点残差）；增减伤对两侧皆默认才隐藏、单边非零整对显示；整行默认整行不渲染、增减伤全默认整组隐藏；暴击伤害为例外恒显。
  - **服务端权威直渲**：面板直接接收 `CombatAttributes` 协议终值（全点数口径，玩家/敌怪/斗法镜像同一形状），不做任何本地估算或配表反查；格式化统一由面板渲染层完成（`linearText`/`nonLinearText`/`factorText`/`boostReduceText`），条目为数据驱动渲染（词条按 `insight.combat.{key}`/`{key}Tooltip` 约定派生）。
  - **敌方/盟友属性为按需一次性交付**：`enemies[].attributes`/`duel.opponentAttributes`/`allies[].attributes` 不随快照常驻下发——`CombatArena` 将 `inspectedActor`（`enemy:{下标}`/`ally:{memberIndex}`）经 `UnitInspect` 火忘帧登记为会话观察目标，服务端在下一份成功入队的快照帧中投影一次即自动解除武装（帧被丢弃则保留武装重投）；前端按 `enemy:{enemyId}`/`duel:{对手名}`/`ally:{playerId}` 缓存已交付属性，重复点开即显不触发加载态；盟友投影的是开打冻结面板（`ActionParticipantSnapshot.Stats`），与敌怪定义级静态值同一交付口径；图鉴/生态/地图等战前查看走 `CombatantDefinition.attributes` 本地目录反查，与战斗详情通道相互独立。
  - 新增战斗属性字段时只改协议字段集与面板一处，禁止在弹窗/其他页面另写属性行。
- **组件源码**：[`frontend/src/components/CombatAttributePanel.tsx`](../../frontend/src/components/CombatAttributePanel.tsx)
- **样式类名**：复用 `.insight-pill-group` / `.insight-base-layout` / `.insight-base-row` 与 `PillStatLabel` 胶囊体系

---

## 26. 日志/传音分组面板与传音流组件 (Log Groups & Chat Channels)

- **适用场景**：
  - **日志与传音的通用「时间 + 内容」渲染行**：服务端日志事件（含 `{{kind:id|label}}` 富文本引用胶囊与 `× N` 重复折叠胶囊）与传音消息共用同一网格排版 -> `LogFeed.tsx`（`FeedList` / `toLogFeedItems` / `toChatFeedItems` / `mergeFeedItems` / `useStickyScroll`）。
  - **8 个分组 Tab 的日志面板**（综合/世界/附近/系统/战斗/好友/宗门/队伍）-> `GameLogPanel`：综合 = 系统日志 + 好友私聊 + 队伍群聊按时间合并（只读）；系统/战斗 = 服务端日志事件按 `channel` 分流；世界/附近/好友/队伍 = 传音频道；宗门为占位。
  - **传音频道面板**（世界/附近/队伍共用单会话模板）-> `ChatChannelPanel`；**好友私聊面板**（好友列表 + 申请处理 + 道号检索 + 私聊会话）-> `FriendChannelPanel`；**队伍面板**（成员列表 + 邀请 + 队伍群聊）-> `PartyChannelPanel`；**综合只读合并面板** -> `AggregatePanel`。
  - **传音输入框**：独立持有草稿状态，聊天列表高频刷新不重建输入框，输入焦点与选区保持稳定 -> `ChatComposer`（`frontend/src/components/ChatPanel.tsx`）。
  - **贴底滚动 Hook**：`useStickyScroll(revision)`，玩家未向上翻查时新内容自动滚到最新行；贴底标记只由用户滚动行为决定。
- **组件源码**：
  - [`frontend/src/components/GameLogPanel.tsx`](../../frontend/src/components/GameLogPanel.tsx)
  - [`frontend/src/components/LogFeed.tsx`](../../frontend/src/components/LogFeed.tsx)
  - [`frontend/src/components/ChatPanel.tsx`](../../frontend/src/components/ChatPanel.tsx)
- **状态服务**：[`frontend/src/logging/gameLogService.ts`](../../frontend/src/logging/gameLogService.ts)（日志事件，按 `channel` 过滤）、[`frontend/src/logging/chatService.ts`](../../frontend/src/logging/chatService.ts)（传音消息，按会话键分组）、[`frontend/src/game/socialStore.ts`](../../frontend/src/game/socialStore.ts)（好友/申请/队伍/附近修士）
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.log-tabs`, `.chat-composer`, `.chat-entry-list`, `.chat-presence`, `.nearby-player-actions`)
- **设计真源**：[`docs/design/传音频道与日志分组.md`](../design/总纲/14-传音频道与日志分组.md)



---

## 27. 战斗技能特效加载模块 (Combat VFX)

- **适用场景**：
  - 历练遭遇战/斗法回放中功法出手、普攻命中、吟唱前摇的粒子演出；统一走 `src/vfx/` 模块，禁止在 `CombatArena` 或其他业务组件内硬编码技能特效。
  - **单一真源**：特效脚本真源在 `docs/design/战斗演武画廊/vfx/`（画廊与正式前端共享同一批 classic-script 队列模块，无 import/export）；脚本向 `window.DaohuaVFXModules` 推入注册函数，前端经 `import.meta.glob(eager)` 引入后在 `createCombatVfx` drain 注册——改画廊脚本即改正式页面演出。
  - **模块化注册**：`vfx/abilities/<skill>.js` 每真实功法一个模块，`VFX.register('ability.x', handler)`；前端 glob 自动发现——新增技能只需在画廊目录新增文件并在画廊 `index.html` 加一行 script 标签，正式前端零改动。
  - **演出上下文 `VfxPlayContext`**：`{side, casterEl, targetEls, ability, castMs, after}` 由桥接层在出手时刻解析（目标按配表 `targetType` 从 `.arena-actor--player/enemy:not(.is-dead)` DOM 快照），技能模块只做纯粒子演出，不写结算。
  - **触发源**：玩家 `combatResources.castingAbilityId`（出现播 `feedback.castRing`、完成时刻播技能）；敌方 `ExpeditionEnemy.pendingAbilityId` + `nextAttackAtSeconds`；斗法 `duel.events[].abilityId + atSeconds`；普攻出手时刻播 `feedback.attack`；全部按 signature 防重，未知 `abilityId` 回退 `feedback.burst`。
  - **结算边界**：伤害跳字/血条碎条/受击震颤/出手动作由服务端快照驱动的既有组件（`ArenaResource`/`ArenaActor`）承担，特效模块不触碰战斗状态。
  - **普攻与受击变体**：常态攻击变化复用共享 `vfx/feedback.js`；气血下降的局部反馈复用 `ArenaActor` 与 `expedition.css` 的 `.arena-impact-fx`，画廊由 `vfx/core.js` 挂载相同样式。逐项查看使用画廊演武控制台。
  - **尸体消散**：波次清空转入搜寻相位后服务端即清空 `enemies[]`，`CombatArena` 缓存战斗相位末帧敌方阵容继续以尸体形态（`is-dead`、不可交互）展示至寻敌倒数 1 秒，到点调 `CombatVfxRuntime.playCorpseDissipate()` 对全部 `.arena-actor--enemy` 播残魂萤火（画廊 `core.js` `defeat()` 同参数），槽位 CSS 淡出（`arena-corpse-dissipate` + `--corpse-fade-ms`）同步收束至新一波入场。
- **模块源码**：[`frontend/src/vfx/`](../../frontend/src/vfx/)（`engine.ts` 引擎原语+DaohuaVFX 门面 · `useCombatVfx.ts` React 桥 · `index.ts` 加载器与目标解析）；**脚本真源** [`docs/design/战斗演武画廊/vfx/`](../../docs/design/战斗演武画廊/vfx/)
- **样式文件**：[`frontend/src/styles/vfx.css`](../../frontend/src/styles/vfx.css)（`.vfx-stage-overlay`/`.vfx-node`/震屏 keyframes；技能专属样式各脚本 `VFX.injectStyles` 自含）
- **接入方式**：`CombatArena` shell 挂 `ref` + 渲染 `<div className="vfx-stage-overlay"/>` + `useCombatVfx(shellRef, overlayRef)` 一行；返回值是运行时 ref，供快照调度之外的一次性演出主动触发（当前仅尸体消散）。
- **注意**：`vite.config.ts` 的 `server.fs.allow: ['..']` 为 dev server serve 仓库根共享脚本所必需，勿删。

---

## 28. 讨伐行动节点生态面板 (CombatEcologyPanel)

- **适用场景**：
  - 讨伐/战斗行动配置弹窗（`ActionModal`）内的当前地点妖兽生态展示区：主体为怪物**图标瓦片网格**（立绘 + 生命层次角标 + 等级底标），**悬停**弹简要信息卡（生命层次徽记、境界·等级·等阶副标、气血/攻击威能/护甲三行核心值），**单击**弹详细信息卡（六维属性、攻速间隔、消耗预算、出没周期、击杀收益、施展功法与掉落物胶囊）。
  - **双层浮层嵌套结构**：外层 `FloatingPopover trigger="click"`（详卡，`placement="bottom"`）包内层 `FloatingPopover trigger="hover" interactive={false}`（简卡，`placement="top"`），均 `displayContents` 锚定同一瓦片按钮——hover 与 click 分侧展开互不遮挡，对齐 `InventoryItemCell` 既有嵌套模式。
  - **数据源即节点生态**：取静态目录 `maps[world.mapId].nodes[world.nodeId]` 的 `monsters`/`eliteEnemies`/`bossEnemies`（服务端 `StaticGameCatalogMapper.ToNode` 按节点 `encounterIds` 预解析的 `CombatantDefinition` 全字段），与服务端 `ResolveCombatEncounter` 实战遭遇同根同源；行进中或节点无生态时回退行动钉死 `combatEncounterId` 的遭遇怪群，禁止再按行动遭遇常驻展示。
  - **行动名按驻留地点本地化**：`localizeActionDisplayText(text, world, maps)`（`frontend/src/game/ids.ts`）把行动名/描述中内嵌的当前地图名替换为当前节点名（「讨伐 青云仙山」→「讨伐 漱玉溪」）；行进中（`nodeId=null`）不替换。已统一接入 `ActionCard`、`ActionModal`、行动队列显示名与 `CombatArena` 各显示点。
- **组件源码**：[`frontend/src/components/CombatEcologyPanel.tsx`](../../frontend/src/components/CombatEcologyPanel.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.combat-ecology-panel`, `.ecology-monster-tile`, `.ecology-monster-rank`, `.ecology-monster-level`)

---

## 29. 设置页布局模板（左栏分类导航 + 分组设置行）

- **适用场景**：
  - 游戏设置中心及一切「多分类偏好/账号管理」页面：`.settings-layout` = 左侧竖排分类栏（`.settings-nav`，`.settings-nav-item` 图标格 + 楷体标签 + 金色内描边高亮，分类图标经 `SETTINGS_NAV_ICONS` 内联 SVG 按分类键注册）+ 右侧限宽 640px 的 `.settings-content` 内容列（`role="tablist"`/`role="tab"`/`role="tabpanel"` 语义齐备）。
  - 分组卡片 `.settings-group`（金色左边条小标题 `.settings-group-title`）内统一使用 `.setting-row`「`.setting-text`（`.setting-label` + `.setting-desc`）| 控件/`.setting-value`」设置行，行间虚线分隔；危险操作分组加 `.settings-group--danger`（红调边框与标题）。
  - 账号资料卡 `.settings-profile`（立绘 `.settings-profile-avatar` + 道号/序号 `.settings-profile-name` + 账号 `.settings-profile-meta` + 改名/冷却入口）；账号安全分组承载修改密码入口（`.inline-modify-btn` + `ChangePasswordModal`）；分段选项按钮复用 `.presence-options`。
  - 「形象」独立分类页：`.avatar-selection-grid` 内排列 `.avatar-tile` 紧凑瓦片（`.avatar-tile-icon` 立绘 + `.avatar-tile-name` 名称，选中态金色边框），扩展形象只需在 `AVATAR_OPTIONS` 追加条目。
  - **移动端（≤960px）自动降级**：左栏分类栏变顶部横向滚动分类条（active 态改下划线；`align-self: start` + 布局 `align-content: start` 防止网格行被剩余空间撑高），内容列解除限宽。
- **组件源码**：[`frontend/src/components/SettingsPage.tsx`](../../frontend/src/components/SettingsPage.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.settings-layout`, `.settings-nav-item`, `.settings-group`, `.setting-row`, `.settings-profile`, `.avatar-tile`, `.modal-form-`)

---

## 30. 运维监控面板外壳与展示原语（Ops Panel）

- **适用场景**：
  - **运维监控后台页面**（路由 `/ops` 或动态随机路径 `/ops-<hex>`，按需懒加载分包）：`OpsApp` 提供「独立密码登录门禁页 + 顶部状态条 + 左侧竖排分组导航 + 内容区」外壳，视图涵盖概览 / 健康 / 调度 / 计算 / 日志 / 玩家 / 会话 / 存储 / 账号检测 / 盘口 / 挂单 / 成交 / 市场指标 / 系统下单 / 配置 / 运维操作。
  - **通用运维展示原语**（`opsUi.tsx`，任何后台或诊断页可直接复用）：
    - `OpsCard` 卡片容器（标题 + 右上角附加区）、`OpsStatGrid` + `OpsStat` 指标卡网格、`OpsTable` 通用数据表格（`OpsColumn<T>` 定义列，`numeric` 列右对齐等宽数字）、`OpsKeyValues` 键值清单、`OpsPill` 语义胶囊（`OpsTone` 七色）、`OpsAsyncView` 加载/失败/内容三态。
    - `useOpsData(load, deps, intervalMs)` 数据 Hook：首次加载 + 可选轮询 + 手动重试；每次加载新建 AbortController 并在卸载或重载时中止上一次请求，避免旧响应覆盖新数据。
    - 格式化辅助：`formatBytes` / `formatDuration` / `formatNumber` / `formatTime` / `formatMillis`；枚举中文化 `levelLabel` / `presenceLabel` / `healthLabel` 与色调 `levelTone` / `healthTone`。
  - **面板数据客户端**（`opsClient.ts`）：自动解析当前路由前缀（`/ops` 或 `/ops-<hex>`），`opsGet` 只读与 `opsPost` 写操作统一附带 `X-Ops-Token` 密码头（密码存 `sessionStorage`，通过登录门禁页校验后写入，支持一键退出锁定）。
- **复用约定**：颜色与控件外观全部取自 `frontend/src/styles.css` 游戏主题变量；面板只补充结构样式 `ops.css`（`.ops-*` 前缀），不新造配色体系。写操作统一经 `ConfirmDialog` 二次确认。
- **组件源码**：[`frontend/src/ops/`](../../frontend/src/ops/)（`OpsApp.tsx` 外壳 · `opsUi.tsx` 原语 · `opsClient.ts` 客户端 · `views/` 视图分组）
- **样式文件**：[`frontend/src/ops/ops.css`](../../frontend/src/ops/ops.css) (搜索 `.ops-root`, `.ops-topbar`, `.ops-nav`, `.ops-card`, `.ops-stat`, `.ops-table`)
- **设计真源**：[`docs/backend/ops-panel.md`](../backend/ops-panel.md)

---

## 31. 显示主题与配色 (Theme)

- **适用场景**：设置 → 显示的浅色/深色分段选择；各页面、公共组件、弹窗与 Portal 使用同一根节点主题（含配色与主题字重），默认浅色并在当前浏览器保存。
- **设置入口**：[`frontend/src/components/SettingsPage.tsx`](../../frontend/src/components/SettingsPage.tsx)（沿用 `settings-group` 与 `presence-options`）。
- **首屏恢复 / 状态**：[`frontend/public/theme.js`](../../frontend/public/theme.js)、[`frontend/src/game/themeStore.ts`](../../frontend/src/game/themeStore.ts)。
- **统一配色样式**：[`frontend/src/styles/theme.css`](../../frontend/src/styles/theme.css)。

---

## 32. 万象图鉴与百科展示模板 (Codex Page · Compendium Layout)

- **适用场景**：
  - **万象图鉴主页面**（四大分类：制造配方、建筑图谱、功法神通、大世界妖兽）：整合全景收集度进度胶囊、多级选项卡切换、未解锁条目的暗墨置灰与锁头遮罩。
  - **悬浮卡片解锁条件**：统一条目 Hover 悬浮卡（`FloatingPopover` + `FloatingInfoCard`），在卡内显著位置挂载 `.codex-unlock-condition-pill`，统一注明对应解锁方式（境界等级、秘籍研读、妖兽讨伐降服）。
- **组件源码**：[`frontend/src/components/CodexPage.tsx`](../../frontend/src/components/CodexPage.tsx)
- **样式文件**：[`frontend/src/styles/codex.css`](../../frontend/src/styles/codex.css)
- **设计真源**：[`docs/design/总纲/23-万象图鉴与解锁机制.md`](../design/总纲/23-万象图鉴与解锁机制.md)

---

## 33. 节点公共建筑卡片与工程角标模板 (Node Building Card)

- **适用场景**：
  - **地图附近面板公共建筑卡片**（`NearbyPanel.tsx`）：
    - **右上角工程状态角标**（`.building-corner-badge`）：针对处于施工期的公共建筑，右上角挂载专属状态徽标，解决列表层级状态不明显问题。
      - 建造中（`level === 0 && targetLevel > 0`）：`.is-constructing` 琥珀金色调微光胶囊（`#fbbf24`），文字为「建造中」；
      - 升级中（`level > 0 && targetLevel > 0`）：`.is-upgrading` 灵气紫色调微光胶囊（`#c084fc`），文字为「升级中」。
    - **加成状态视觉分层**：
      - 已建成建筑（`level > 0`）：金色高亮 `.building-card-bonus-tag` 呈现当前实际生效加成；
      - 未建成/建造中建筑（`level === 0`）：使用 `.building-card-bonus-tag.is-pending` 次级文本色，文案前缀统一规范为「建成生效: ...」，杜绝未建成时显示金色数值误导玩家以为加成已提前生效的视觉冲突。
    - **工程进度与材料投入引导**：在建建筑卡片展示 `材料 X% · 工期 Y%`，材料未满时右侧显示 `.building-card-progress-hint`（「待投材料」），直接引导玩家交互。
- **组件源码**：[`frontend/src/components/NearbyPanel.tsx`](../../frontend/src/components/NearbyPanel.tsx)
- **样式文件**：[`frontend/src/styles/expedition.css`](../../frontend/src/styles/expedition.css) (搜索 `.building-corner-badge`, `.building-card-bonus-tag.is-pending`, `.building-card-progress-wrapper`)
- **设计真源**：[`docs/design/总纲/09-节点公共建筑系统.md`](../design/总纲/09-节点公共建筑系统.md)
