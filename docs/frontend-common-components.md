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
  - 点击/右键呼出的悬浮操作菜单、处置选项列表 -> `FloatingMenu`, `FloatingMenuItem`, `FloatingMenuGroup`, `FloatingMenuDivider`
- **组件源码**：[`frontend/src/components/FloatingPopover.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/FloatingPopover.tsx)
- **样式文件**：[`frontend/src/styles/floating-popover.css`](file:///home/yuohira/game/xiuxian/frontend/src/styles/floating-popover.css)
- **专项设计规范**：[`docs/ui-floating-popover.md`](file:///home/yuohira/game/xiuxian/docs/ui-floating-popover.md)

---

## 2. 复合修仙标签与胶囊 (Pill Stat & Bonus Labels)

- **适用场景**：
  - **复合数值展示**（暗墨卡片底、图标、胶囊包裹名称、专属下划线高亮数值、可选内联经验进度文本与底边进度条、可选集成 Hover Tooltip） -> `PillStatLabel`
  - **属性加成/词缀标签**（无外边框轻量流式、胶囊名、专属色调下划线值、可选集成 Tooltip） -> `PillBonusLabel`
  - 支持修仙色调 (`PillTone`)：`gold`, `jade`, `qi`, `purple`, `amber`, `crimson`, `emerald`, `cyan`, `violet`, `indigo`, `teal`, `bronze`, `silver`, `slate`, `sky`, `muted`, `default`
- **组件源码**：[`frontend/src/components/PillLabel.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/PillLabel.tsx)
- **样式文件**：[`frontend/src/styles.css`](file:///home/yuohira/game/xiuxian/frontend/src/styles.css) (搜索 `.compound-stat-label`, `.compound-bonus-label`, `.pill-tone--*`)

---

## 3. 境界徽章与位阶牌匾 (Realm Badge)

- **适用场景**：
  - 角色/敌方境界标准牌匾展示（包含大境界牌匾框、位阶棱形徽章框【武/玄/天/初/中/后/极】、小境界灵气胶囊框，自带 Hover 弹出大境界与属性详情浮层） -> `RealmBadge`
- **组件源码**：[`frontend/src/components/RealmBadge.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/RealmBadge.tsx)
- **样式文件**：[`frontend/src/styles.css`](file:///home/yuohira/game/xiuxian/frontend/src/styles.css) (搜索 `.insight-realm-*`)

---

## 4. 数值文本高亮与词条解析 (Value Highlighting & Diff)

- **适用场景**：
  - 战报日志、效果描述文本中自动识别正负数、百分比、数值单位，并自动包裹为修仙五行色彩的高亮小胶囊 -> `ValueText`
- **组件源码**：[`frontend/src/components/ValueText.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/ValueText.tsx)
- **样式文件**：[`frontend/src/styles.css`](file:///home/yuohira/game/xiuxian/frontend/src/styles.css) (搜索 `.value-capsule`, `.value-capsule--*`)

---

## 5. 面板二级导航与子选项卡 (Panel Sub-Tabs Navigation)

- **适用场景**：
  - 面板主分类下的二级切换栏（如内视根基下的「六维根基」与「基础数值」切换，采用紧凑暗墨胶囊按键与金色微光高亮）。
- **样式类名**：`.insight-sub-tabs`, `.insight-sub-tab-btn`
- **样式文件**：[`frontend/src/styles.css`](file:///home/yuohira/game/xiuxian/frontend/src/styles.css) (搜索 `.insight-sub-tabs`)

---

## 6. 物品方块与图元展示 (Item Tile & Display)

- **适用场景**：
  - 行囊、掉落、交易、装备槽位中的单个物品方块（包含品阶光晕色调、数量角标、品质边框、内置 Hover 悬浮信息） -> `ItemTile`, `ItemTooltipTarget`
- **组件源码**：[`frontend/src/components/ItemDisplay.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/ItemDisplay.tsx)
- **样式文件**：[`frontend/src/styles.css`](file:///home/yuohira/game/xiuxian/frontend/src/styles.css) (搜索 `.item-tile`, `.item-badge-rarity-*`)

---

## 7. 网格选择器与目录拾取器 (Item Grid & Catalog Pickers)

- **适用场景**：
  - **物品网格选择弹窗**（支持搜索、分类筛选、物品图元网格排列、空态选项、数量/品质高亮） -> `ItemGridPicker`
  - **通用目录选择弹窗**（针对秘籍、配方、地图地点等条目的网格选择模板） -> `CatalogPicker`
- **组件源码**：
  - [`frontend/src/components/ItemGridPicker.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/ItemGridPicker.tsx)
  - [`frontend/src/components/CatalogPicker.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/CatalogPicker.tsx)
- **样式文件**：[`frontend/src/styles/selection-grid.css`](file:///home/yuohira/game/xiuxian/frontend/src/styles/selection-grid.css)

---

## 7. 数量步进与快捷选择器 (Quantity Selector)

- **适用场景**：
  - 市场交易、批量制造、行囊使用、派遣探索次数的选择（包含数字输入框、快捷预设数量按钮、可选无限 `∞` 按钮） -> `QuantitySelector`
- **组件源码**：[`frontend/src/components/QuantitySelector.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/QuantitySelector.tsx)
- **样式文件**：[`frontend/src/styles.css`](file:///home/yuohira/game/xiuxian/frontend/src/styles.css) (搜索 `.quantity-selector`)

---

## 8. 通用模态弹窗与确认对话框 (Modals & Confirm Dialogs)

- **适用场景**：
  - **通用全屏居中遮罩弹窗**（基于 React Portal 顶层挂载、Focus 焦点锁定陷阱、ESC 键响应、三种标准尺寸 `small`/`medium`/`large`） -> `Modal`
  - **二次确认 / 高危操作对话框**（重置、遗忘功法、摧毁物品、批量购买等，带取消/确认与高危危险样式） -> `ConfirmDialog`
- **组件源码**：
  - [`frontend/src/components/Modal.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/Modal.tsx)
  - [`frontend/src/components/ConfirmDialog.tsx`](file:///home/yuohira/game/xiuxian/frontend/src/components/ConfirmDialog.tsx)
- **样式文件**：[`frontend/src/styles.css`](file:///home/yuohira/game/xiuxian/frontend/src/styles.css) (搜索 `.modal-backdrop`, `.modal-shell`, `.confirm-dialog`)

---

## 9. 状态动效与平滑进度 Hooks (Smooth Progress Hook)

- **适用场景**：
  - 战斗气血/真元条、行动倒计时平滑插值动画，防止卡顿跳跃 -> `useSmoothProgress`
- **源码路径**：[`frontend/src/game/useSmoothProgress.ts`](file:///home/yuohira/game/xiuxian/frontend/src/game/useSmoothProgress.ts)

---

## 10. 通用文本与数值格式化 (Format Helpers)

- **适用场景**：
  - 灵石/大额数值格式化（万、亿、兆）、时长格式化、百分比格式化 -> `formatNumber`, `formatDuration`, `formatPercent`
- **源码路径**：[`frontend/src/game/format.ts`](file:///home/yuohira/game/xiuxian/frontend/src/game/format.ts)
