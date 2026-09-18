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
  - **通用 Buff / Debuff / 特殊机制附属副气泡窗口 (Aside Cards)**：支持将丹药持续 Buff、修炼/技艺效率加成、装备特殊机制/词缀以及角色身上生效的 Buff/Debuff 自动解析为独立的副气泡卡片窗口，并排挂载联动展示（`resolveItemAsideCards`, `resolveEquipmentAsideCards`, `createBuffAsideCard`）。
  - **功法书与招式残卷参悟副气泡联动**：针对 `item.tome.*` 与 `item.fragment.*`（或 `manual`/`manualFragment` 类别），自动检索全局战技配置，通过 `describeAbility(ability, 1)` 生成包含伤害系数、威能、前摇、冷却、灵力消耗与触发条件的 `[秘籍参悟]` / `[残卷参悟]` 副卡片（色调 `qi`），挂载于悬浮主卡片旁联动展示（`resolveManualAsideCard`）。
  - **懒求值与密集长列表滚动性能保障**：`FloatingPopover` 具备 `isOpen` 短路懒计算机制，未展开时完全不执行 `content` 与 `mergedAsideCards` 的解析与 JSX 构建；在密集网格长列表（如坊市 `MarketBrowseGrid`）中，支持滚动抑制（`is-scrolling` 配合 `pointer-events: none`）与视口渲染裁剪（`content-visibility: auto`），彻底消除滚动期间的高频 Hover 风暴与 Layout Thrashing。
  - 点击/右键呼出的悬浮操作菜单、处置选项列表 -> `FloatingPopover`（菜单容器）、`FloatingMenuItem`、`FloatingMenuGroup`、`FloatingMenuDivider`
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

## 3. 境界徽章与位阶牌匾 (Realm Badge)

- **适用场景**：
  - 角色/敌方境界标准牌匾展示（包含大境界牌匾框、位阶棱形徽章框【武/玄/天/初/中/后/极】、小境界灵气胶囊框，自带 Hover 弹出大境界与属性详情浮层） -> `RealmBadge`
- **组件源码**：[`frontend/src/components/RealmBadge.tsx`](../../frontend/src/components/RealmBadge.tsx)
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
  - **坊市多级分类子导航 (`MarketBrowseGrid`)**：在一级主 Tab（全部/材料/装备/丹药/功法）下方，复用 `.insight-sub-tabs` 与 `.insight-sub-tab-btn` 展示对应二级子分类（材料细分矿石/药材/通用材料；装备细分武器/防具/饰品；功法细分秘籍全本/招式残卷）。
- **样式类名**：`.insight-sub-tabs`, `.insight-sub-tab-btn`, `.market-sub-tabs`
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.insight-sub-tabs`)

---

## 6. 物品方块与图元展示 (Item Tile & Display)

- **适用场景**：
  - 行囊、掉落、交易、装备槽位中的单个物品方块（包含品阶光晕色调、数量角标、品质边框、内置 Hover 悬浮信息） -> `ItemTile`, `ItemTooltipTarget`
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
      详见 [`TASK-20260913-06.md`](../tasks/TASK-20260913-06.md)。
    - **编码档位**：**降色 256 + 无损 WebP**（`--web-palette 256 --web-lossless`）。
      90 张合计 **168.1 KB**：同口径下比原先的有损 q90（267.2 KB）**小 37%**，可见像素 RGB 还高 4.13 dB。
      有损 WebP 会把**边缘半透明像素的 RGB** 带偏（表现为轮廓发白、整体褪色发灰），降色 + 无损没有这个毛病。
      换编码档位前先读 [`TASK-20260913-07.md`](../tasks/TASK-20260913-07.md) 的实测对照表。
  - **图标渲染统一关闭线性插值（`image-rendering: pixelated`）**：三处图标 `<img>` 都用最近邻缩放，
    保留像素质感（改动前是浏览器默认双线性，会发虚）。位置：
    `.item-tile-icon`（[`selection-grid.css`](../../frontend/src/styles/selection-grid.css)）、
    `.item-reference-icon`（[`styles.css`](../../frontend/src/styles.css)，16px）、
    `.daohua-floating-card-title-icon`（[`floating-popover.css`](../../frontend/src/styles/floating-popover.css)，24px）。
    瓦片绘制区固定为 52px，故 64×64 源在 DPR1 是**缩小** 0.81×、在 DPR2 是**放大** 1.63× —— 两种密度均已实测无走样。
    **但若源图升到 ≥128px，缩小倍率会明显变大（DPR1 约 2.46×），届时必须重新实测这三处。**
  - **多气泡联动、Buff/特殊机制副窗口与独立数量气泡**：
    - 主卡片展示品阶、基础说明、即时回血回蓝、冷却时长与装备基础攻防；
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
  - **物品网格选择弹窗**（支持搜索、分类筛选、物品图元网格排列、空态选项、数量/品质高亮） -> `ItemGridPicker`
  - **功法/战技网格选择弹窗**（功法装配槽位选择，网格排列 + 冷却/装配状态标记） -> `AbilityGridPicker`
  - **通用目录选择弹窗**（针对秘籍、配方、地图地点等条目的网格选择模板；附带子导出 `PickerTrigger` 选择器触发按钮与 `ChoiceMenu` 下拉选项菜单，供触发条件编辑器等内嵌场景复用） -> `CatalogPicker`
- **组件源码**：
  - [`frontend/src/components/ItemGridPicker.tsx`](../../frontend/src/components/ItemGridPicker.tsx) (含 `ItemUseSlot` 消耗品槽位组件，支持与技能/物品冷却完全一致的持续消耗品旋转填充倒计时遮罩 `item-slot-duration-overlay`)
  - [`frontend/src/components/AbilityGridPicker.tsx`](../../frontend/src/components/AbilityGridPicker.tsx)
  - [`frontend/src/components/CatalogPicker.tsx`](../../frontend/src/components/CatalogPicker.tsx) (导出 `CatalogPicker` 主组件 + `PickerTrigger` / `ChoiceMenu` 子组件)
- **样式文件**：[`frontend/src/styles/selection-grid.css`](../../frontend/src/styles/selection-grid.css)

---

## 8. 数量步进与快捷选择器 (Quantity Selector)

- **适用场景**：
  - 市场交易、批量制造、行囊使用、派遣探索次数的选择（包含数字输入框、快捷预设数量按钮、可选无限 `∞` 按钮） -> `QuantitySelector`
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
  - **事件日志与战报中的实体胶囊引用**（包含功法技能 `ability`、妖兽敌人 `enemy` / `encounter`、角色 `character`、机制 `mechanic`、地点 `location`、行动 `action`，自带专属修仙微光色彩与悬浮信息浮层）。
  - **复合物品胶囊**（将物品名称与数量如 `灵石 × 41` 组合为一整个不可分割的紧凑修仙药丸胶囊，包含物品品质色调、微光背景与下划线数量，支持悬浮弹出物品属性卡片） -> `ItemReference`, `.item-reference-compound`。
  - **完全相同日志连续折叠与乘数胶囊**（挂机采集、连续战斗等产生的完全相同模板日志在尾部自动合并显示微光乘数胶囊形如 `× 3`，更新为最新触发时刻并保留历史缓冲容量） -> `.log-repeat-capsule`, `collapseLogEntries`。
- **组件源码**：
  - [`frontend/src/components/ItemDisplay.tsx`](../../frontend/src/components/ItemDisplay.tsx)
  - [`frontend/src/components/GameLogPanel.tsx`](../../frontend/src/components/GameLogPanel.tsx)
  - [`frontend/src/logging/gameLogService.ts`](../../frontend/src/logging/gameLogService.ts)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.log-reference`, `.item-reference-compound`, `.log-repeat-capsule`)

---

## 15. 改名弹窗、登录界面与独立连接状态屏幕 (Rename Modal, Login & Connection Screens)

- **适用场景**：
  - **修改修士道号弹窗**（含 2~12 字符校验、正则无特殊字符校验、查重与 24 小时改名冷却） -> `RenameModal`
  - **独立全屏登录/注册界面**（无账号或登出时展示，支持输入账号、道号与可选访问令牌，自带粒子与修仙太极光晕） -> `LoginPage`
  - **独立全屏连接中与断网重试屏幕**（避免未连接时渲染默认 3 任务测试界面引发数据丢失疑惑，提供连接动画、重试与登出/切换账号入口） -> `ConnectingPage`, `ConnectionErrorPage`
- **组件源码**：
  - [`frontend/src/components/RenameModal.tsx`](../../frontend/src/components/RenameModal.tsx)
  - [`frontend/src/components/LoginPage.tsx`](../../frontend/src/components/LoginPage.tsx)
  - [`frontend/src/components/ConnectingPage.tsx`](../../frontend/src/components/ConnectingPage.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.rename-modal`, `.login-screen`, `.status-screen`, `.status-card`)

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
  - **技艺生活工具扩展槽位**：底部排布 4 个预留技艺工具法宝槽位（采矿/伐木/炼丹/炼器）。
- **组件源码**：[`frontend/src/components/EquipmentPanel.tsx`](../../frontend/src/components/EquipmentPanel.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.equipment-view-panel`, `.equipment-stage`, `.equipment-slot-btn`)

---

## 18. 右侧多 Tab 容器与功法神通面板 (Right Sidebar & Abilities Panel)

- **适用场景**：
  - **右侧多 Tab 顶层容器**（聚合储物袋、装备、功法三大核心面板，支持平滑切换） -> `RightSidebar`
  - **功法神通面板**（1 首位内功心法 + 动态扩展术法槽位横排装配与冷却/触发器/等级调整 + 全部已学功法秘籍目录列表） -> `AbilitiesPanel`
- **组件源码**：
  - [`frontend/src/components/RightSidebar.tsx`](../../frontend/src/components/RightSidebar.tsx)
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
  - 战斗技能槽位自动施放触发条件的编辑与配置弹窗（支持自身/敌方/队友目标依赖，气血/真元比例或固定阈值条件，生效/未生效状态比较，最多 4 条逻辑与规则约束） -> `CombatTriggerEditor`
- **组件源码**：[`frontend/src/components/CombatTriggerEditor.tsx`](../../frontend/src/components/CombatTriggerEditor.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.combat-trigger-editor`, `.combat-trigger-card`, `.combat-trigger-fields`)

---

## 23. 纯净行动卡片与品质等阶角标 (Action Card & Capsule Level Badge)

- **适用场景**：
  - 修炼、炼丹、炼器、采集与历练等交互行动的纯净微型展示卡片（标准每行 4 个网格排布，严格遵循“只显示名称，不展示多余图标或详情”规范，右上角复用标准 `item-corner item-corner--top-right` 内部胶囊角标，整张卡片与角标受 `data-rarity` 品质色彩驱动，点击唤起对应配置/执行弹窗） -> `ActionCard`
  - **真实等级换算**：未指定显式等级时，由 `convertTierToLevel` 统一将品阶换算为等级（1阶->1级，2阶->18级，3阶->30级，N阶->`18 + (N - 2) * 12` 级）。
  - **品质色彩联动**：由 `resolveActionRarity` 依据产出物/装备/遭遇解析品质色调（`mundane`/`spirit`/`mystic`/`earth`/`heaven`等），驱动边框微光与背景着色。
- **组件源码**：[`frontend/src/App.tsx`](../../frontend/src/App.tsx)
- **等级与品质解算引擎**：[`frontend/src/game/ids.ts`](../../frontend/src/game/ids.ts)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.action-cards`, `.action-card`, `.item-corner--top-right`)

---

## 24. 行动统计面板与账本分区 (Stats Panel & Report Blocks)

- **适用场景**：
  - **行动收益账本统一渲染**：当前行动 / 离线挂机 / 每日统计 / 近期历史共用 `ReportBlock` 分区容器，内部统一拆为三个 memo 小节——`ReportExpSection`（获取经验：修为/底蕴/六维/功法技能经验/等级提升）、`ReportItemsSection`（物品得失：获得与失去两列分组，复用 `ItemReference`）、`ReportInfoSection`（行动信息：轮次/时长/遭遇/分级击杀/死亡/斗法胜负）。
  - **空账本抑制**：`reportHasContent` 判定全零账本不渲染分区；`endedAtUnixSeconds` 决定时长口径（进行中=估算服务端当前时刻−开始，归档=结束−开始）。
  - **localStorage 历史缓存**：`battleReportStore` 读取时对旧缓存条目补齐账本新增字段默认值，防渲染崩溃。
- **组件源码**：[`frontend/src/components/StatsPanel.tsx`](../../frontend/src/components/StatsPanel.tsx)
- **缓存层**：[`frontend/src/game/battleReportStore.ts`](../../frontend/src/game/battleReportStore.ts)
- **样式文件**：[`frontend/src/styles/expedition.css`](../../frontend/src/styles/expedition.css) (搜索 `.battle-report-`)


