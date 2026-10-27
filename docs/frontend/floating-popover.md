# 道化太初 · 通用悬浮气泡与菜单体系设计规范 (Floating Popover System)

本文件是《道化太初》前端通用的悬浮提示、信息气泡与操作菜单体系的标准设计与技术实现文档。

---

## 1. 设计背景与核心目标

在传统游戏与 Web 界面中，常见的 Hover 提示往往存在以下痛点：
1. **界面遮挡与容器裁切**：父级 DOM 节点的 `overflow: hidden`、`overflow: scroll`、`transform` 或局部 `stacking context` 会导致气泡被边缘截断；
2. **超出可视视口**：位于屏幕边缘的元素弹出的提示容易超出屏幕顶部或左右边界；
3. **移动端交互缺失**：手机/平板等触屏设备无法 hover，而简单绑定 click 容易吞掉子元素原生的业务点击；
4. **宽度参差不齐**：纯自适应宽度会导致不同长短文本的气泡忽宽忽窄，缺乏规整度；
5. **多气泡组合困难**：无法优雅同时展示装备主体 + 词缀说明 + 套装效果等多层附属气泡。

为此，本项目基于 React 19 + TypeScript 设计了专用的悬浮图层体系（`FloatingPopover`），实现**防裁切、防超出、0 阻断、标准宽度、多气泡组合与古典修仙美学**的统一封装。

---

## 2. 核心架构与组件划分

悬浮体系由 [`frontend/src/components/FloatingPopover.tsx`](../../frontend/src/components/FloatingPopover.tsx) 与 [`frontend/src/styles/floating-popover.css`](../../frontend/src/styles/floating-popover.css) 构成：

```text
┌─────────────────────────────────────────────────────────────┐
│                    FloatingPopover (核心底座)                 │
│  - Portal 顶层渲染 (#daohua-floating-root, z-index: 10000)   │
│  - 智能视口边界安全计算 (Auto Flip + Viewport Clamp)             │
│  - 双端事件分流 (Desktop Hover / Mobile Touch / Passthrough)  │
│  - Aside 附属气泡群 + 数量副气泡联动排布 (asideCards/quantity) │
└──────┬──────────────────┬───────────────────────┬───────────┘
       │                  │                       │
┌──────▼───────┐  ┌───────▼────────┐  ┌───────────▼────────────┐
│FloatingTooltip│  │ FloatingInfoCard│  │ FloatingMenuItem      │
│(轻量极简提示) │  │ (修仙标准卡片)  │  │ FloatingMenuGroup     │
└──────────────┘  └────────────────┘  │ FloatingMenuDivider   │
                                      │ (菜单内容子组件，      │
                                      │  承载于 content 插槽)  │
                                      └───────────────────────┘
```

### 2.1 组件职责

1. **`FloatingPopover`**：基础通用浮层底座，负责定位计算、生命周期、Portal 挂载、多端事件与 Aside 附属气泡容器管理；**同时也是操作菜单的容器**——菜单没有独立的 `FloatingMenu` 组件，菜单内容通过 `content` 插槽承载 `FloatingMenuItem`/`FloatingMenuGroup`/`FloatingMenuDivider` 子组件渲染；
2. **`FloatingTooltip`**：极简文字提示组件，默认配置 `interactive={false}`，鼠标与点击完全穿透底层，0 侵入；
3. **`FloatingInfoCard`**：标准修仙信息卡片，内置道号/法宝标题、境界徽章、副标题、属性键值对网格、词条列表、使用说明及底部操作区；**注意 `asideCards` 是 `FloatingPopover` 的属性而非 `FloatingInfoCard` 的属性**，附属卡片由外层 Popover 容器统一排布；
4. **`FloatingMenuItem` / `FloatingMenuGroup` / `FloatingMenuDivider`**：菜单内容子组件（菜单项、分组、分割线），仅作为 `FloatingPopover` 的 `content` 子节点使用，支持图标、快捷键与多种修仙色调。

---

## 3. 关键技术实现机制

### 3.1 Portal 顶层挂载与防裁切
- 浮层节点不挂载在业务组件内部，而是通过 React `createPortal` 挂载至全局独立的专用容器 `#daohua-floating-root`；
- 专用容器样式为 `position: fixed; top: 0; left: 0; width: 0; height: 0; z-index: 10000; pointer-events: none;`；
- 浮层彻底脱离任何局部 CSS 上下文，绝对避免被 `overflow: hidden` 或 `transform` 裁切。

### 3.2 智能视口安全定位与自适应等比缩放算法 (`computeSafeFloatingPosition`)
每次展开或窗口变化时，算法会根据触发器（Anchor）与浮层自身未缩放尺寸（Layout Size）执行安全计算：
1. **视口溢出检测与自适应等比缩放（Adaptive Viewport Scale）**：
   - 测量浮层自然尺寸（`floatingW`, `floatingH`）与视口可用空间（`availableW = viewportW - padding * 2`, `availableH = viewportH - padding * 2`）；
   - 当自然高度或宽度超出视口可用空间时（如竖屏模式多张副气泡纵向单列堆叠），自动计算等比缩小比例：
     $$\text{scale} = \max\left(0.5, \, \min\left(1.0, \, \frac{\text{availableH}}{\text{floatingH}}, \, \frac{\text{availableW}}{\text{floatingW}}\right)\right)$$
   - **硬性缩放下限（最多缩小至 50%）**：缩放比例绝对不低于 `0.5`，确保字形、胶囊标签与图标始终具备可读性与辨识度；
   - 空间充裕时保持 `1.0`（无任何缩小与性能开销）；
2. **上下自动翻转（Auto-Flip Y）**：基于缩放后的物理尺寸（`scaledH = floatingH * scale`），若优选下方（`bottom`）但下方可视空间小于 `scaledH + gap`，且上方空间更充足，则自动翻转至上方（`top`），反之亦然；
3. **左右自动翻转（Auto-Flip X）**：若优选右侧（`right`）但右侧空间小于 `scaledW + gap`，自动翻转至左侧；
4. **视口边界物理安全夹持（Viewport Clamp）**：
   $$\text{left} = \text{clamp}(\text{padding}, \text{rawLeft}, \text{viewportWidth} - \text{scaledW} - \text{padding})$$
   $$\text{top} = \text{clamp}(\text{padding}, \text{rawTop}, \text{viewportHeight} - \text{scaledH} - \text{padding})$$
   采用 `transform-origin: top left` 与几何包围盒对齐，消除任何负边距与居中偏移抖动；
5. **极端超长内容滚动兜底**：在 $\le 768\text{px}$ 移动端/竖屏下，即使卡片极多缩小到 50% 后仍超出屏幕，浮层整体容器（`.daohua-floating-layer`）无缝衔接 `overflow-y: auto` 与古典金灰细滚动条，100% 杜绝卡片掉到屏幕外无法查看的问题。
### 3.3 双端交互与 0 阻碍事件穿透

#### A. 桌面端 (Hover 模式)
- 带有微延时防抖（`openDelay` 默认 80ms~100ms），移出延时（`closeDelay` 默认 120ms）；
- 当 `interactive={true}` 时，鼠标从触发器滑入浮层卡片内部可保持悬停，方便划词或点击菜单项；
- 当 `interactive={false}`（如 `FloatingTooltip`、物品展示）时，浮层应用 `pointer-events: none !important`，鼠标划过或点击完全穿透到底层 UI，0 阻碍操作。

#### B. 移动端 (Touch 模式)
- 自动检测 `(pointer: coarse)` 与 `(hover: none)`；
- 点击触发元素展开提示，**绝不调用 `e.preventDefault()` / `e.stopPropagation()`**，确保业务自身 `onClick` 100% 正常执行；
- **长按（浏览器原生派发 `contextmenu`）统一展示本触发器的 hover 详情气泡**：`preventDefault()` 屏蔽系统右键/长按菜单，`stopPropagation()` 阻断冒泡——外层触发器挂载的业务右键回调（如背包格「右键快捷使用」）在触屏端不会被执行，桌面端语义不变；
- 滑动屏幕（`scroll` / `touchmove`）时平滑收起当前气泡；
- 点击外部空白处静默关闭，且不拦截目标元素的点击事件。

---

## 4. 标准固定宽度规范

为了避免不同长短文案导致气泡忽宽忽窄，系统制定了标准宽度体系：

| 预设尺寸 (`size`) | 标准宽度 (`width`) | 适用场景 |
| :--- | :--- | :--- |
| **`medium` (默认)** | **`min(300px, 100vw - 24px)`** | 装备、物品、功法技能、六维根基、境界徽章（经典统一黄金宽度） |
| **`small`** | **`min(240px, 100vw - 24px)`** | 简单属性数值、紧凑状态 Buff 提示 |
| **`large`** | **`min(380px, 100vw - 24px)`** | 详尽生平长篇、复杂战报对比、多列属性网格 |
| **`auto`** | **`max-content`** | 极简单行文字提示（`FloatingTooltip`）或操作菜单 |

### 多气泡组合规范 (Aside Cards) 与双列高度均衡堆叠
- **经典单副卡模式（副卡 $\le 1$）**：
  - 主卡片（宽 300px）与单张附属卡片（宽 220px）横向并排贴合，维持经典左右单挂形态；
- **多副卡双列高度均衡堆叠模式（副卡 $\ge 2$）**：
  - **痛点治理**：当物品同时携带数量、技能参悟、雷达图、内功增益、倾向说明、坊市价格等多个副卡片时，传统单列排布会导致右侧过度垂地（超 780px+ 触碰屏幕底边），且左列主卡下方闲置大面积空白；
  - **智能均衡分发（`balanceAsideColumns`）**：
    - **左列吸附**：标的直属属性（单行数量卡、坊市市场价格行情卡）与较矮副卡自动吸附于左列主卡片正下方，宽度撑满主卡（`width: 100%`，即 300px），排版规整舒展；
    - **右列承载**：战技参悟雷达图、六维资质倾向等重型面板归入右列；
    - **两列高度均衡**：左右两列高度从原先的 300px:780px 均衡压缩为 500px:550px，彻底消除长条拖尾与溢出风险；
- **移动端 / 窄屏（$\le 768\text{px}$）**：所有列与卡片自适应转为垂直纵向单列流式堆叠，卡片宽度等宽撑满（100%），视觉极其整齐对称。
---

## 5. 修仙古典美学规范

1. **暗墨玄石材质**：`rgba(14, 12, 7, 0.96)` 高透明度深黑底色，叠加 `backdrop-filter: blur(14px)` 毛玻璃效果；
2. **古典折角金纹**：卡片四角带有直角暗金装饰纹理（`.daohua-floating-corner`）；
3. **顶部金华流光**：细微暗金渐变高光细线（`.daohua-floating-shimmer-bar`）；
4. **典雅字体与排版**：标题采用楷体/宋体（`"Noto Serif SC", "Kaiti SC", serif`），词条采用古典胶囊排版；
5. **五行与修仙色调 (`FloatingTone`)**：
   - `gold`：暗金真金（法宝、主属性、极品）；
   - `jade`：苍翠碧玉（生命、防御、木行、恢复）；
   - `qi`：灵虚淡蓝（真元、术法、灵力、神识）；
   - `danger`：朱砂煞红（煞气、负伤、削弱、销毁）；
   - `purple`：紫极天道（神级词条、天阶秘宝）；
   - `muted`：玄灰幽暗（杂项、产出来源、背景注记）。

---

## 6. 使用范例

### 6.1 标准物品/装备展示（带多气泡组合）
```tsx
import { FloatingPopover, FloatingInfoCard } from './components/FloatingPopover'

<FloatingPopover
  placement="bottom"
  asideCards={[
    { mark: '词缀', title: '万剑归宗·极', tone: 'gold', lines: ['御剑伤害 +45%', '暴击率 +15%'] },
    { mark: '套装', title: '纯阳剑胚 (2/4)', tone: 'jade', lines: ['真元消耗降低 20%'] }
  ]}
  content={
    <FloatingInfoCard
      title="青索玄天剑"
      badge="天阶中品"
      badgeTone="purple"
      subtitle="来源：击败万剑宗大长老掉落"
      description="采九天玄铁与太清纯阳之气炼就，剑气如霜。"
      stats={[
        { label: '物理攻击', value: '+680', tone: 'gold' },
        { label: '真元加成', value: '+240', tone: 'qi' },
        { label: '破招机率', value: '+12%', tone: 'gold' },
      ]}
      lines={[
        '附带神通：【青索破煞】对妖邪伤害提升 50%',
        '铭刻：纯阳宫第七代掌门佩剑',
      ]}
      footer="可在行囊中点击使用或装备"
    />
  }
>
  <div className="item-cell">青索剑</div>
</FloatingPopover>
```

### 6.2 极简轻量提示 (FloatingTooltip)
```tsx
import { FloatingTooltip } from './components/FloatingPopover'

// 鼠标悬停显示，移走立即消失，且鼠标点击可直接穿透到底层按钮
<FloatingTooltip title="消耗真元 100 点，造成 250% 物理伤害" tone="qi" placement="top">
  <button className="skill-btn">御剑术</button>
</FloatingTooltip>
```

### 6.3 悬浮操作菜单（FloatingPopover 承载菜单子组件）
```tsx
import {
  FloatingPopover,
  FloatingMenuGroup,
  FloatingMenuItem,
  FloatingMenuDivider,
} from './components/FloatingPopover'

<FloatingPopover
  trigger="click"
  placement="bottom-start"
  content={
    <FloatingMenuGroup title="灵宝处置">
      <FloatingMenuItem label="祭炼温养" hint="耗灵石 100" onClick={() => handleRefine()} />
      <FloatingMenuItem label="注入真元" tone="qi" onClick={() => handleInfuse()} />
      <FloatingMenuDivider />
      <FloatingMenuItem label="熔炼销毁" tone="danger" onClick={() => handleDestroy()} />
    </FloatingMenuGroup>
  }
>
  <button>操作选项</button>
</FloatingPopover>
```

---

## 7. 维护与扩展准则

1. **避免在子节点使用原生 HTML `title="..."` 属性**：父子节点若带有 `title`，浏览器会在 1~2 秒后强行弹出系统默认提示框并干扰视觉；无障碍提示应使用 `aria-label`；
2. **纯展示组件优先配置 `interactive={false}`**：确保气泡浮层不会挡住玩家点击底层的游戏快捷栏或地图网格；
3. **保持宽度标准**：新增业务气泡默认继承 `medium`（300px）预设，避免随意书写无约束的宽度导致界面参差不齐。
