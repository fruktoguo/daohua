# TASK-20261023-09 前端UI字号分组与DesignTokens统一收拢

## 1. 任务背景与核心目标

当前前端 UI 中散落着超过 600 处硬编码字号，且存在 `10.5px`、`11.5px`、`12.5px`、`0.85rem` 等多处临时微调值。颜色（`--gold`、`--text-dim`）、字重（`--font-weight-*`）均已变量化，但字号仍缺乏统一管理。
本次任务目标：
1. **建立全局 Typography Tokens**：在 `frontend/src/styles.css` `:root` 中定义全套符合仙侠高密度 UI 布局特征的阶梯字号（`--text-3xs` 到 `--text-hero`）及语义功能别名（`--font-size-badge`、`--font-size-caption`、`--font-size-body` 等）。
2. **消灭离散微调碎片值**：将 `10.5px`、`11.5px`、`12.5px`、`0.85rem` 等离散值平滑归并至标准字号 Token。
3. **收拢公共组件样式**：将核心公共样式库（`selection-grid.css`、`floating-popover.css`、`shop.css`、`tutorial.css`、`codex.css` 及 `styles.css` 核心排版类）全面迁移至标准字号变量。
4. **统一设计规约文档**：在 `docs/frontend/common-components.md` 登记字号使用标准查找表，杜绝后续继续出现随手硬编码像素。

## 2. 详细方案设计

### 2.1 字号 Token 设计表

| Token 变量 | 对应像素 | 功能语义别名 | 典型应用场景 |
| :--- | :--- | :--- | :--- |
| `--text-3xs` | `8px` / `7px` / `6px`（微缩角标） | `--font-size-micro` | 装备极小强化标、极小微标 |
| `--text-2xs` | `10px` (含 `9px` 紧凑微标) | `--font-size-badge` | 胶囊角标、状态微标签、进度条百分比 |
| `--text-xs` | `11px` | `--font-size-caption` | 属性名 Label、弱化副文本、气泡说明（全仓最高频 193 处） |
| `--text-sm` | `12px` | `--font-size-sub` | 列表项、日志正文、副卡内容、次要按钮（次高频 166 处） |
| `--text-base` | `13px` | `--font-size-body` | 按钮文本、选择器项、Tab 页签、紧凑正文 |
| `--text-md` | `14px` | `--font-size-regular` | 全局 `body` 基准字号、标准正文 |
| `--text-lg` | `16px` (含 `15px`) | `--font-size-title` | 卡片名称、二级小标题、重点金额数值 |
| `--text-xl` | `18px` (含 `17px`) | `--font-size-header` | 页面/面板主标题、模态弹窗 Header |
| `--text-2xl` | `20px` | `--font-size-display` | 核心大标题、突破境界醒目大字 |
| `--text-hero` | `24px` ~ `32px` | `--font-size-hero` | 战场暴击跳字、开卷立绘大字 |

### 2.2 离散碎片值归并规则

- `10.5px` -> 归入 `--text-xs` (11px) 或 `--text-2xs` (10px)；
- `11.5px` -> 归入 `--text-sm` (12px) 或 `--text-xs` (11px)；
- `12.5px` -> 归入 `--text-base` (13px)；
- `13.5px` -> 归入 `--text-md` (14px)；
- `0.85rem` (`~11.9px`) -> 归入 `--text-sm` (12px)；
- `0.75rem` (`~10.5px`) -> 归入 `--text-xs` (11px) 或 `--text-2xs` (10px)；
- `0.88rem` (`~12.3px`) -> 归入 `--text-sm` (12px) 或 `--text-base` (13px)。

## 3. 子任务与进度追踪

- [x] **子任务 1：登记任务看板与详情归档** (2026-10-23 21:00:00)
- [x] **子任务 2：在 styles.css 建立 Typography 字号 Tokens 与功能语义别名** (2026-10-23 21:05:00)
- [x] **子任务 3：在 docs/frontend/common-components.md 补充字号规约与查找表** (2026-10-23 21:10:00)
- [x] **子任务 4：公共样式库字号收拢与离散碎片值平滑治理** (2026-10-23 21:15:00)
- [x] **子任务 5：执行前端构建、测试与门禁验证** (2026-10-23 21:25:00)
- [x] **子任务 6：更新任务文档与状态归档，执行 Git 自动提交** (2026-10-23 21:30:00)

## 4. 时间日志

- 任务提出时间：`2026-10-23 20:55:00`
- 需求分析与方案设计：`2026-10-23 21:00:00`
- 字号 Tokens 与公共样式库收拢完成：`2026-10-23 21:18:00`
- 前端打包构建与全量门禁回归通过：`2026-10-23 21:28:00`
- 任务归档与自动提交完成：`2026-10-23 21:30:00`
