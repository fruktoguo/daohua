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
  - **通用独立数量副气泡联动**：传入 `quantity={amount}` 属性，自动在主卡片旁附加独立的数量副气泡（标题为「数量」，内容为中文四位分段「具体数值」）。
  - 点击/右键呼出的悬浮操作菜单、处置选项列表 -> `FloatingMenu`, `FloatingMenuItem`, `FloatingMenuGroup`, `FloatingMenuDivider`
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
- **样式类名**：`.insight-sub-tabs`, `.insight-sub-tab-btn`
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.insight-sub-tabs`)

---

## 6. 物品方块与图元展示 (Item Tile & Display)

- **适用场景**：
  - 行囊、掉落、交易、装备槽位中的单个物品方块（包含品阶光晕色调、数量角标、品质边框、内置 Hover 悬浮信息） -> `ItemTile`, `ItemTooltipTarget`
  - **多气泡联动与独立数量气泡**：物品悬浮时在主信息卡旁自动附带独立的数量副气泡（标题为「数量」，内容为中文四位分段「具体数值」）。
- **组件源码**：[`frontend/src/components/ItemDisplay.tsx`](../../frontend/src/components/ItemDisplay.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.item-tile`, `.item-badge-rarity-*`), [`frontend/src/styles/floating-popover.css`](../../frontend/src/styles/floating-popover.css) (搜索 `.daohua-floating-aside-*`)

---

## 7. 网格选择器与目录拾取器 (Item Grid & Catalog Pickers)

- **适用场景**：
  - **物品网格选择弹窗**（支持搜索、分类筛选、物品图元网格排列、空态选项、数量/品质高亮） -> `ItemGridPicker`
  - **通用目录选择弹窗**（针对秘籍、配方、地图地点等条目的网格选择模板） -> `CatalogPicker`
- **组件源码**：
  - [`frontend/src/components/ItemGridPicker.tsx`](../../frontend/src/components/ItemGridPicker.tsx) (含 `ItemUseSlot` 消耗品槽位组件，支持与技能/物品冷却完全一致的持续消耗品旋转填充倒计时遮罩 `item-slot-duration-overlay`)
  - [`frontend/src/components/CatalogPicker.tsx`](../../frontend/src/components/CatalogPicker.tsx)
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
- **组件源码**：
  - [`frontend/src/components/Modal.tsx`](../../frontend/src/components/Modal.tsx)
  - [`frontend/src/components/ConfirmDialog.tsx`](../../frontend/src/components/ConfirmDialog.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.modal-backdrop`, `.modal-shell`, `.confirm-dialog`)

---

## 10. 状态动效与平滑进度 Hooks (Smooth Progress Hook)

- **适用场景**：
  - 战斗气血/真元条、行动倒计时平滑插值动画，防止卡顿跳跃 -> `useSmoothProgress`
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
  - **外语 CSV 动态扩展**：支持直接在 [`frontend/public/locales/`](../../frontend/public/locales/) 放入第三方 CSV 语言包（如 `ja-JP.csv`），前端按需异步拉取并缓存，未命中或加载中无缝回退至中文树状词典。
- **约定**：跨页面高频词只入 `common` 域（confirm/cancel/close 等），页面域不得重复定义；服务端下发的游戏内容（物品名、行动名、日志模板等协议数据）不走前端 i18n

---

## 13. 基础修仙单位数值与自动换算组件 (UnitNumber)

- **适用场景**：
  - **大额数值/灵石/修为/战力紧凑展示**：支持万、亿、兆、京等阶梯自动改后缀，确保有效数字不超过 6 个字符，小数点后最多 2 位，默认向下取整。
  - **内置 Hover 气泡与四位分割**：鼠标悬停自动弹出轻量 Tooltip，以中文每四位分割清晰展示完整数字（如 `1234亿 7777万 5211`），最多展示小数点后两位，兼具修仙色调高亮与标准千分位对照。
- **组件源码**：[`frontend/src/components/UnitNumber.tsx`](../../frontend/src/components/UnitNumber.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.unit-number`, `.unit-number-tooltip-*`)

---

## 14. 日志实体引用与复合物品胶囊 (Log Entity Reference & Compound Item Pill)

- **适用场景**：
  - **事件日志与战报中的实体胶囊引用**（包含功法技能 `ability`、妖兽敌人 `enemy` / `encounter`、角色 `character`、机制 `mechanic`、地点 `location`、行动 `action`，自带专属修仙微光色彩与悬浮信息浮层）。
  - **复合物品胶囊**（将物品名称与数量如 `灵石 × 41` 组合为一整个不可分割的紧凑修仙药丸胶囊，包含物品品质色调、微光背景与下划线数量，支持悬浮弹出物品属性卡片） -> `ItemReference`, `.item-reference-compound`。
- **组件源码**：
  - [`frontend/src/components/ItemDisplay.tsx`](../../frontend/src/components/ItemDisplay.tsx)
  - [`frontend/src/components/GameLogPanel.tsx`](../../frontend/src/components/GameLogPanel.tsx)
  - [`frontend/src/logging/gameLogService.ts`](../../frontend/src/logging/gameLogService.ts)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.log-reference`, `.item-reference-compound`)

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
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.rename-modal`, `.login-screen`, `.connecting-screen`, `.error-screen`)

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
  - **功法神通面板**（4 个斗法技能槽位的冷却/触发器/等级调整 + 全部已学功法秘籍目录列表） -> `AbilitiesPanel`
- **组件源码**：
  - [`frontend/src/components/RightSidebar.tsx`](../../frontend/src/components/RightSidebar.tsx)
  - [`frontend/src/components/AbilitiesPanel.tsx`](../../frontend/src/components/AbilitiesPanel.tsx)
- **样式文件**：[`frontend/src/styles.css`](../../frontend/src/styles.css) (搜索 `.right-sidebar-panel`, `.abilities-view-panel`)
