# 前端双主题配色审查与浅色适配修复

> 审查时间：2026-10-20
> 判定基线：《总纲 13.1.1 深色与浅色显示主题》——浅色普通文本对实际底色对比度 ≥ 4.5:1，焦点与必要控件边界 ≥ 3:1；不得把深色主题的高亮文字直接复用到浅色底。
> 对比度按 WCAG 2.1 相对亮度公式实算；半透明底色先按 alpha 合成到实际承载底色（`--panel #f9f5ea`、`--bg-raised #e4dbc4`、`--bg #f1ecdc`）再计算。

## 1. 审查范围与方法

| 维度 | 覆盖对象 |
| --- | --- |
| 样式表 | 11 个文件共 21,547 行（`styles.css` 10,409 / `expedition.css` 4,731 / `selection-grid.css` 1,363 / `simulator.css` 1,406 / `floating-popover.css` 1,193 / `theme.css` 1,011 / `codex.css`、`shop.css`、`tutorial.css`、`vfx.css`、`ops/ops.css`） |
| 组件内联色 | 53 个 `components/*.tsx`、`simulator/`、`market/`、`ops/` 全部 `style` 与 SVG 属性 |
| 主题机制 | `data-theme` 根属性、`theme.css` 覆盖层、CSS 变量定义域、四条样式导入顺序 |

三层扫描：

1. **硬编码颜色 × 主题作用域**：提取每条声明，标记其位于 `[data-theme="light"]` 作用域内外，剔除 `var()` 回退值后统计字面量色。
2. **覆盖比对**：对每条基础规则，检索浅色层是否存在同元素、同属性的改写；无改写者进入人工复核。
3. **变量闭合性**：比对「被使用」与「被定义」的变量集合，并检查是否只在深色根作用域定义；重点排查 `var(--x, 浅色回退值)` 这类在浅色下必然取到浅色的写法。

## 2. 结论摘要

- 深色主题整体自洽，**本次未改动任何深色分支**；全部修复限定在 `[data-theme="light"]` 作用域或新增的主题令牌。
- 确认并修复 **38 项缺陷**，修复落点 `theme.css` 新增 62 条选择器 / 86 条声明，另有 1 处组件级 SVG 常量与 11 个颜色令牌。
- 缺陷归因分布：
  - **浅色文本/控件对比度不足**：深色主题的高明度高亮色被直接复用，实测普遍落在 1.4~3.6:1；
  - **未做浅色适配的深色遗留面**：`rgba(0,0,0,.2~.85)` 卡面、纯黑进度槽、`rgba(255,255,255,.0x)` 微光边未随主题翻转；
  - **主题变量未正确应用**：`--text-muted` / `--text-normal` / `--text-sub` 三项只有深色回退值，浅色下取到浅色字面量。
- 另有 6 类「看似问题、实为设计」的项经核对后**有意保留**，见第 4 节。

## 3. 逐界面清单

### 3.1 全局变量层

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `--text-muted`（用于 `.abilities-section-count`、`.abilities-empty-state`、`.inventory-group-count`） | 从未定义，`color: var(--text-muted)` 计算值无效而继承父级 | 浅色定义为 `var(--text-dim)` |
| `--text-normal`（`styles.css:1271` `.spirit-stone-tier-btn:hover`） | 未定义，回退 `#f0f0f0`；浅色底实测 **1.05:1**，悬停即隐形 | 浅色定义为 `var(--text-bright)` |
| `--text-sub`（`floating-popover.css:433` `.daohua-floating-aside-desc`） | 未定义，回退 `rgba(233,227,210,.85)`；浅色浮层实测 **1.18:1** | 浅色定义为 `var(--text-dim)` |

> 这三项是「机制未产生实际效果」的典型：变量名存在、调用点存在，唯独浅色域没有定义，最终落到深色主题的浅色回退字面量。

### 3.2 顶栏 / 左侧导航 / 右侧栏

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.nav-sub-progress`（`styles.css:1557`） | 技艺子菜单经验槽底为 `rgba(255,255,255,.08)`，纸白侧栏上不可见，未填充段与已填充段无从区分 | 底槽改 `rgba(125,82,26,.18)` |
| 顶栏、底部导航、导航选中态、右栏标签页 | 已由 `theme.css` 既有规则适配（印章红选中、纸白底） | 无需改动 |

### 3.3 行动面板与战斗调度

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.action-stat-pill--rootexp strong`（`styles.css:3983`） | 荧光绿 `#34d399` 压 8% 绿底，实测 **1.67:1** | 改 `var(--jade)`（5.93:1），边框加深至 35% |
| `.action-stat-pill--technique strong`（`styles.css:3995`） | 荧光橙 `#fb923c`，实测 **1.96:1** | 改 `var(--amber)`（5.64:1） |
| `.duel-option-status .duel-node-teams`（`styles.css:4384`） | 浅紫 `#a78bfa`，实测 **2.50:1** | 改 `var(--purple)`（7.11:1） |
| `.queue-combat-vital`（顶部敌我血条） | 已在 TASK-20261015-13 修复，本次复核通过 | 无需改动 |

### 3.4 斗法竞技场

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.arena-resource-track`（`expedition.css:2148`） | 底槽 `#090b10` + 纯黑描边，浅色面板上是一条黑板 | 与 `.progress` 同口径：米褐渐变槽底 + 金褐描边 |
| `.arena-action-track`（`expedition.css:3315`） | 同上 | 同上 |
| `.arena-life-progress-bar`（`expedition.css:4609`） | `rgba(0,0,0,.7)` 底槽；浅色层只改了文字阴影（白色），字仍是 `#fff` | 槽底提亮；`.arena-life-progress-text` 改 `var(--text)`，与现有白色描边组合 |
| `.arena-search-progress-vertical`（`expedition.css:3534`） | `rgba(0,0,0,.65)` 竖向进度槽 | 纳入槽底统一改写 |
| `.arena-resource-badge` / `.arena-action-badge` / `.arena-resource-floats em` | 白字或浅色字，但均带 2px 黑色描边（`paint-order: stroke fill`） | **保留**：描边保证任意底色可读，改色反而降低战斗可读性 |

### 3.5 大世界地图与地点

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| 地图舞台、视口、节点、边线、边标签、区域名、玩家箭头 | `theme.css` 已给出成套浅色改写（`world-map-stage/layout/node/edge-*`），逐条复核通过 | 无需改动 |

### 3.6 建造与地点详情

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.building-section-card`（`expedition.css:1240`） | `rgba(0,0,0,.25)` 卡面 + 深墨 `--text-dim` 文字，实测 **2.8:1** | 改纸白 72% 底 + `--gold-faint` 描边 |
| `.building-load-item`（`expedition.css:1350`） | `rgba(0,0,0,.3)`，同上 | 纸白 55% 底 |
| `.building-bonus-pill`（`expedition.css:1301`） | `rgba(0,0,0,.45)`，胶囊内文字在其上仅 2.5:1 | 纸白 80% 底 |
| `.building-load-status-bar.is-overload`（`expedition.css:1397`） | 浅红 `#fca5a5` 压 8% 红底，实测 **2.0:1** | 改 `var(--danger)`（5.14:1） |
| `.building-progress-track`（`expedition.css:1219`） | 白色 8% 底槽，浅色下不可见 | 统一槽底改写 |

### 3.7 背包、物品与品质

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.item-corner` / `.item-corner--bottom-right` / `.action-card .item-corner`（`styles.css:5910`、`3704`） | 底色已被浅色主题提亮为纸白 94%，文字仍是 TASK-20261015-13 为深色底选定的 `#c5bfb0` / `#c8c3b7`，实测 **1.67:1** | 改 `var(--text-dim)`（7.54:1） |
| `.item-reference-compound`（`styles.css:1925`） | 边框 `rgba(255,255,255,.12)`，纸白上无边界，行内物品胶囊失去可点提示 | 边框改 `rgba(125,82,26,.35)`，悬停 `--gold-dim` |
| 物品瓦片、品质八色、冷却遮罩、品质丝带 | 已有成套浅色改写 | 无需改动 |

### 3.8 内视、根基与特质

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.insight-progress-bar`（`styles.css:7130`） | 境界层进度槽 `#090a0f` + 纯黑描边 | 统一槽底改写 |
| `.insight-traits-progress-bar`（`styles.css:10152`）、`.trait-card-exp-track`（`10305`）、`.trait-capacity-track`（`10378`） | `rgba(0,0,0,.4~.5)` 深色槽，与金色填充形成黑板 | 统一槽底改写 |
| `.trait-card-checkbox`（`styles.css:10318`） | `rgba(0,0,0,.3)` 底 + `--text-dim` 字，实测 **3.64:1** | 底改 `rgba(125,82,26,.1)`（6.61:1） |
| `.compound-stat-progress-track`（`styles.css:7359`） | 白色 8% 底槽，浅色不可见 | 改 `rgba(125,82,26,.18)` |
| 六维雷达图（`RootAspectRadarChart.tsx`） | 网格/轴线 `rgba(233,227,210,.08/.12)` 在纸白上 **1.03:1**；100% 基准字 42% 金 **1.5:1**；100%~200% 数值 `#7dd3fc` **1.7:1**；多边形描边 `#38bdf8` | 抽出 11 个 `--radar-*` 令牌，浅色改写为金褐网格、`--qi` 描边与深色数值（组件零逻辑改动） |

### 3.9 坊市

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.market-level-trade-btn--bid` / `--ask`（`styles.css:4971`、`4976`） | 浅碧 `#8fc3a6` / 浅赭 `#dda184` 压 3% 白底，实测 **1.83:1 / 2.03:1** | 改 `var(--jade)` / `var(--amber)`，底色改纸白 55% |
| `.daohua-market-aside-stock-val--ask.is-active` / `--bid.is-active`（`floating-popover.css:583`、`594`） | 浅赭 `#f6bfa8` / 浅碧 `#aae0c2`，实测 **1.49:1 / 1.36:1**；同卡片的徽章与单价已有深色改写，唯独存量数值遗漏 | 改 `#a8502f` / `#277850`，与卡片内既有徽章同色 |
| 行情副气泡徽章、价格、成交量 | 已有成套改写 | 无需改动 |

### 3.10 万象图鉴

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.codex-tab-count`（`codex.css:97`）、`.codex-sub-tab-count`（`127`） | `rgba(0,0,0,.3/.25)` 底 + 深墨字，实测 **3.64:1** | 底改 `rgba(125,82,26,.12)`（6.38:1） |
| `.codex-card-avatar-box`（`codex.css:231`） | `rgba(0,0,0,.4)` 图台，浅色下是黑块 | 纸白 88% 台面 + 金褐描边 |
| `.codex-lock-badge`（`codex.css:185`） | `rgba(10,10,10,.85)` 圆底 + 弱金字（3.27:1） | 纸白 95% 底 + `var(--gold)` 图标 |
| `.codex-sub-tab-btn`（`codex.css:112`） | 3% 白底 + 15% 金边，浅色下无边界 | 未选中态边框改 `--gold-dim` |
| `.codex-unlock-condition-pill--locked`（`codex.css:305`） | `#d87060` 压 12% 红底，实测 **2.09:1** | 改 `#a12b20`（4.60:1），底色 10% |

### 3.11 同道、队伍与角色名片

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.party-pvp-teams-badge`（`styles.css:9055`） | 浅紫 `#a78bfa`，实测 **2.26:1** | 改 `var(--purple)`（6.12:1） |
| `.party-malus-badge`（`styles.css:9334`） | 浅红 `#ff7875`，实测 **1.95:1** | 改 `var(--danger)`（4.74:1） |
| 队伍槽位卡、角色名片（hero/stats/consumable） | 已有成套浅色改写（3.14 节前的专项段落） | 无需改动 |

### 3.12 设置、账号与邀请

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.invite-code`（`styles.css:9852`） | 深金 `--gold-bright` 压 `rgba(0,0,0,.35)`，实测 **3.54:1** | 底改 `rgba(125,82,26,.06)`（5.73:1），字改 `var(--gold)` |
| `.spirit-stone-tier-btn`（`styles.css:1255`） | 4% 白底 + 12% 白边，浅色下按钮无边界；悬停字 `#f0f0f0` | 常态补金褐边与纸白底，悬停字改 `--text-bright` |

### 3.13 弹窗与确认

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.action-modal-submit--danger`（`styles.css:4413`） | 浅红 `#ffb4b4` 压 30% 红底，实测 **1.06:1**；`:hover` 白字同样失效 | 常态淡红底 + `var(--danger)`；悬停实心 `--seal` + `--on-accent` |
| `.breakthrough-modal-row-gain`（`styles.css:7938`） | `rgba(0,0,0,.45)` 深底，浅色层又把内部 `.underlined-value` 改成深色，实测 **2.09:1** | 底改 `rgba(125,82,26,.08)`（6.17:1） |
| 遮罩、表单、字段、关闭按钮 | 已有成套改写；输入控件由 `theme.css` 的 `input/select/textarea` 统一接管 | 无需改动 |

### 3.14 悬浮浮层与副气泡

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.daohua-floating-aside-progress`（`floating-popover.css:312`） | `rgba(8,10,14,.75)` 深色进度槽，副气泡已改为纸白 | 统一槽底改写 |
| `.daohua-floating-aside-desc` | 见 3.1（`--text-sub`） | 变量补齐 |
| 市场/效果/数量/描述副卡主体、徽章、价格 | 已有成套改写 | 无需改动 |

### 3.15 战斗模拟器

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.sim-log-badge.badge-victory` / `.badge-defeat`（`simulator.css:1307`、`1312`） | `#10b981` / `#ef4444` 压 15% 同色底，实测 **2.04:1**；同文件其余诊断徽章、日志行均已按 `#047857`/`#b91c1c` 体系改写，唯此两项遗漏 | 改 `#036a4d` / `#b91c1c` |

### 3.16 运维面板（Development-only）

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| `.ops-tone--warn`（`ops/ops.css:268`） | `#d2a24c` 压 `--bg-raised #e4dbc4`，实测 **1.9:1** | 改 `var(--amber)`（5.18:1） |

### 3.17 登录、连接与新手引导

| 位置 | 现状 | 修复 |
| --- | --- | --- |
| 登录/连接屏背景、卡片、主按钮、错误提示 | 已有成套改写 | 无需改动 |
| 教程遮罩 `rgba(7,6,4,.58)`、主按钮 `#1a150c` 压金底 | 遮罩属模态压暗；按钮为深墨压亮金（8.9:1） | **保留** |

## 4. 有意保留（避免误改）

| 项 | 理由 |
| --- | --- |
| 深色主题全部配色 | 审查确认深色分支自洽；所有改动均在浅色作用域内 |
| 战斗飘字、资源徽标、行动徽标 | 均有 2px 黑色描边（`paint-order: stroke fill`），任意底色可读，改色会削弱战斗演出辨识度 |
| 模态遮罩、教程遮罩 | 模态压暗是通用交互范式，不属于「深色遗留面」 |
| 登录页阴阳鱼、角色立绘、图标投影 | 图形资产按规范不做主题反色，仅阴影已按浅色纸面调暖 |
| `--gold-dim #8f7243`（4.14:1） | 纹饰与描边专用弱金色；作为文字时多为 11px 以下的图标/悬停提示，已达 3:1 图形阈值，整体加深会破坏浅色纹饰层次 |
| 装备品质八色、稀有度光晕 | 已有独立的浅色色相映射 |

## 5. 改动落点

| 文件 | 改动 |
| --- | --- |
| `frontend/src/styles/theme.css` | 新增「全界面浅色对比度与深色遗留面补齐」段落：3 个语义变量定义、62 条选择器、86 条声明（含 12 组槽底统一改写） |
| `frontend/src/styles/floating-popover.css` | `.root-aspect-radar-svg` 增加 11 个 `--radar-*` 颜色令牌（深色取原值） |
| `frontend/src/components/RootAspectRadarChart.tsx` | 将硬编码 SVG 颜色替换为令牌引用，数值色改为按主题解析；无逻辑与结构变更 |

## 6. 验证

| 项 | 结果 |
| --- | --- |
| `npm --prefix frontend run build`（含 `tsc -b`） | 通过，0 类型错误 |
| `node scripts/check-frontend-contracts.mjs` | 通过 |
| 人工验收路径 | 顶栏太极开关切换 → 依次核对：登录屏 / 顶栏与导航 / 行动面板 / 斗法竞技场 / 大世界 / 建造详情 / 背包与悬浮卡 / 内视与特质 / 坊市 / 图鉴 / 同道 / 设置 / 弹窗 / 模拟器 |

## 7. 后续建议（未纳入本次）

1. `--gold-dim` 作为正文文字的少数位置（如 `.chat-side-section h4`）建议单独提级为 `--gold`，但需先确认纹饰不被连带加深。
2. 图鉴锁定条目的 `filter: grayscale/brightness` 在浅色下会进一步压暗缩略图，建议改为降低不透明度。
3. 建议把「浅色对比度」纳入可执行门禁：对新增的 `color/background` 字面量做 lint，禁止在未声明主题作用域时使用高明度文字色。
