# frontend/AGENTS.md - 道化太初前端专项设计与开发规则

本文件适用于 `frontend/` 目录及其所有子模块。本文件是仓库根目录 `AGENTS.md` 在前端工程与视觉交互领域的具体化延伸与执行细则。在前端目录内工作的所有 Agent 与开发者必须无条件严格遵循本文件。

---

## 1. 设计真源与规范对齐

- **设计文档先行**：业务玩法逻辑与数值以 `docs/design/总纲/` 为设计真源；公共组件与交互模式以 `docs/frontend/common-components.md` 为统一模板索引真源。
- **禁止随意自制相似样式或私建组件**：凡是涉及模态弹窗、气泡提示（Hoverinfo）、复合标签（Composite Label）、境界徽章（Realm Badge）、选择网格（Selection Grid）、品阶丝带（Ribbon）等交互形态，必须优先检索并复用已有模板。
- **模板沉淀同步更新**：凡新增或收拢公共模式，必须同步登记写入 `docs/frontend/common-components.md`。

---

## 2. 实体视觉呈现强标识契约（硬红线·绝不犯错）

游戏中所有实体（物品、装备、材料、消耗品、功法技能、Buff、阵盘、丹药、生活技艺等）在界面呈现时，必须严格遵守以下强契约，杜绝任何形式的视觉降级事故：

### 2.1 强制图标化展示
- 凡展示游戏实体条目，必须使用专用视觉组件：
  - **整块网格瓦片**：必须使用 `<ItemTile ... />`（具备品质光晕边框、右上角等阶、左上角丝带品类、左下角强化等级及右下角角标）；
  - **行内小图标**：必须使用 `<ItemIcon ... />`（或 `<AbilityIcon ... />`）；
  - **复合文本引用/胶囊**：必须使用 `<ItemReference ... />`；
- **严禁仅用裸露纯文本名称或普通文字标签代替实体展示**。所有选择网格卡片必须接入 `frontend/src/styles/selection-grid.css` 的共享 `:is()` 锚点组。

### 2.2 强 ID 绑定防降级红线（Anti-Silent-Degradation）
- **核心规约**：只要上下文持有实体的唯一标识（包括但不限于 `item.itemId`、`instrument.itemId`、`offer.itemId`、`loot.itemId`、`recipe.itemId`、`ability.abilityId`），调用 `<ItemTile>`、`<ItemIcon>`、`<ItemReference>` 时**必须显式、无条件传递 `itemId={...}` 或 `abilityId={...}`**。
- **严禁依赖纯文本兜底**：`ItemTile` 内部对缺失 `itemId` 的情况提供文本名称（`<strong className="item-tile-name">{name}</strong>`）仅用于处理极其罕见的无资源原型。**绝对禁止在生产卡片、商店/坊市网格、背包、仓储、制造列表等核心业务场景中漏传 `itemId`**。任何漏传将导致整屏 100+ 个物品图片瞬间消失退化为纯文字，构成严重的 P1 级视觉事故。
- **重构守卫防呆**：
  - 在封装或解构 Props（例如包装 `MarketBrowseCardItem`、`InventoryCell`、`RecipeCard` 等）时，**必须将 `itemId` 列为显式入参第一梯队**，禁止在中间层遗失；
  - 即使组件 Props 将 `itemId` 声明为可选属性（`itemId?: string`），业务调用端也必须视为**必填属性**传入真实 ID。

---

## 3. 角标数据维度与样式物理隔离

标的网格瓦片（`ItemTile`）右下角同时承载多种业务维度的角标信息，必须在语义与渲染层实现**严格物理隔离**，禁止维度混淆：

### 3.1 数量维度与单价/价值维度互斥
- **数量维度（持有量 owned / 在售总量 onSale / 求购总量 bid）**：
  - 传参规范：通过 `amount={displayAmount}` 传递数值；
  - 渲染表现：瓦片角标自动附加数量前缀乘号 `×`（如 `×10`、`×500`）；
  - 样式映射：
    - 自身持有：常规白色/灰度；
    - 全服在售：`.is-onsale-mode`（道金色，`var(--gold-bright)`）；
    - 全服求购：`.is-bid-mode`（翡翠碧青色，`#8fc3a6`）。
- **单价/价格维度（市场参考价 price / 回收底价 / 自定义指标）**：
  - 传参规范：**必须将 `amount` 显式置为 `undefined`**，并通过 `bottomRightLabel={formattedPrice}` 传递纯价格字符串（如 `15`、`120`、`1.2万` 或 `-`）；
  - 渲染表现：**绝对禁止在价格数字前显示乘号 `×`**（价格不是数量，不得显示为 `×120`）；无有效成交价时统一定义为 `-`；
  - 样式映射：
    - 参考价格：`.is-price-mode`（灵石道金琥珀色，深色 `#ffd875`，浅色 `#8c6b12`）。

### 3.2 模式切换与无障碍对齐
- 提供多态切换（如 `owned` -> `onSale` -> `bid` -> `price`）时：
  - 顶部切换控件必须具备明确的 `role="radiogroup"` / `role="radio"` 与选中国际化文案；
  - 卡片的外层容器 `aria-label` 必须严格根据当前模式动态插值生成无障碍辅助文本（如 `{name}，参考价 {price}` / `{name}，暂无参考价`），保证无障碍读屏与视觉角标 100% 同步。

---

## 4. 关键渲染路径静态代码契约测试（防回归双保险）

为防止未来重构时人为或自动化 Agent 再次不慎误删关键属性，必须在测试体系中建立**源码级静态代码契约门禁**：

- **核心网格卡片强制建立 AST/静态断言**：
  凡是坊市（`MarketBrowseGrid`）、背包（`InventoryGrid`）、仓储、制造配方、战利品等直接面向玩家的大面积网格渲染组件，其对应的测试文件（如 `marketOrders.test.mjs`）中必须包含源码静态断言测试：
  ```javascript
  test('marketBrowse: 标的卡片 ItemTile 必须显式携带 itemId 属性，确保图标资源正常解析', async () => {
    const fs = await import('node:fs/promises')
    const path = await import('node:path')
    const filePath = path.resolve('frontend/src/components/market/MarketBrowseGrid.tsx')
    const content = await fs.readFile(filePath, 'utf8')
    assert.match(
      content,
      /<ItemTile[\s\S]*?itemId=\{item\.itemId\}[\s\S]*?\/>/,
      'ItemTile 必须显式声明 itemId 属性，否则瓦片将无法命中图标回退为纯文字'
    )
  })
  ```
- **门禁阻断效应**：任何重构如果意外碰掉 `itemId={...}`，前端逻辑单测（`node --test "frontend/src/**/*.test.mjs"`）将立即在 10ms 内抛出断言错误并退出，从源头上阻断带缺陷的代码合入或发布。

---

## 5. 状态管理与交互稳定性红线

- **Store 行为落地红线**：禁止在状态管理器（Zustand、React Context 等）中声明仅有空函数体 `() => {}` 的虚空方法。任何对外暴露的操作方法必须具备实际的状态流转、数据变更或明确的报错抛出。
- **高频更新不丢状态**：行动推进、即时通讯或行情推送等高频更新，必须使用 `memo` 隔离卡片渲染粒度（如 `MarketBrowseCardItem` 单独抽离并使用 `memo`），严禁全量重建列表导致用户丢失滚动条位置（`scrollTop`）、文字选区或输入焦点。
- **React Rules of Hooks 静态红线**：严禁在条件分支语句（`if`）、循环或早期 `return` 之后调用任何 React Hook（`useMemo`/`useCallback`/`useEffect`/`useState`）。仓库门禁已配置静态 AST 扫描，违规代码直接阻断构建（防 `Minified React error #300`）。

---

## 6. 国际化与文案简练规范

- **i18n 词条闭环**：UI 代码中每引入一个新的 `t('...')` 国际化键名，必须同步在 `frontend/src/i18n/domains/*.zh.ts` 与 `frontend/public/locales/template.csv` 中登记，且必须严格遵循 `scripts/check-i18n-keys.mjs` 的字母序与覆盖检查。
- **UI 文本简练红线**：界面文案只承载玩家可操作的信息，简短直白；严禁在界面中暴露内部术语（如持久化、哈希、CSV、协议字段、WebSocket、重试、预算）或废话连篇的解释性赘述。

---

## 7. 提交前验证基准

前端任何代码变更完成后，在提交前必须至少执行以下两项最小验证，确保 100% 绿灯：

1. **前端逻辑与静态契约单测**：
   ```bash
   node --test "frontend/src/**/*.test.mjs"
   ```
2. **TypeScript 类型检查与生产打包**：
   ```bash
   npm --prefix frontend run build
   ```
3. **i18n 覆盖与转义门禁**：
   ```bash
   node scripts/check-i18n-keys.mjs
   node scripts/check-log-markup.mjs
   ```
