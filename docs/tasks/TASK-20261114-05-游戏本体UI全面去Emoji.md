# TASK-20261114-05 游戏本体 UI 全面去 Emoji

## 需求

用户提出：当前有不少地方（如顶部修炼队列消耗灵石徽标）使用 Emoji（💎 钻石等）充当图标，与游戏美术风格不统一。要求：**所有可以用真实图标的，凡是能用灵石图像本身等真实美术资产显示的，一律改用图像；游戏本体 UI 中不再显示任何 Emoji**。

## 边界与口径

- **目标范围**：`frontend/src` 内所有会渲染到游戏界面的 Emoji/彩色符号。
- **替换优先级**：
  1. 有真实美术资产的实体 → 走既有图标管线（`resolveItemIconUrl` + `ItemIcon`/`ItemTile`/`ItemReference`、`resolveBuildingIconUrl`、`StatGlyph` 属性字形、`Icon` 全站 SVG）。
  2. 无美术资产的功能性符号（锁/聊天/定位/警告/骰子/指南针等）→ `Icon.tsx` 内联 SVG。
  3. 纯排版字符（↑↓←→、▴▾▼▶、×、＋、♣♦♥♠ 牌面花色、中文单字）不属于 Emoji，保留。
- **不在范围**：聊天/表情包功能本身的 Emoji 内容（TASK-20261109-05 定制表情是聊天功能载体），仅替换系统侧功能按钮与徽标中的 Emoji。
- **元数据层**：`ids.ts` `ITEM_META_CORES.glyph` 等历史 Emoji 字形在 CombatArena 生活目标卡改走图标后已无渲染出口，仍统一替换为中性字符兜底，杜绝未来回潮。

## 盘点（扫描基线）

- `app.zh.ts`：`spiritStoneBadge '💎 {count}/轮'`、`randomNameBtn '🎲 随机道号'`；`insight.zh.ts`：`breakthroughBtn '⚡ 破境突破'`、`openBtn '⛩ 开天门'`。
- 组件直写：App.tsx 🔒、CodexPage 🔒×4+🏛️、SettingsPage 🔒、ChatMessageContent 👥×2+📍×2、ChatPanel 😊、ChatSticker 📷、ChatEmojiPicker 🖼️、SocialPage 💬×2、WorldMapPanel 💬+◎+⏳+🚶、ZongmenPage 🏛️×2、ConnectingPage ⚡、ErrorBoundary ⚠️、CandidatePicker ⚙、CombatSimulatorModal ⚔、CombatArena 🧭+生活目标卡 glyph。
- 元数据 glyph：`ids.ts` ~110 条 Emoji 字形 + `ItemDisplay.fallbackMetadata` 📘/📜、`marketStore`/`CombatAttributePanel` ⚔️、`PlayerCardModal` 🧪。

## 实施

1. `Icon.tsx` 扩展矢量图标集（lock/building/chat/pin/users/smile/image/alert/bolt/dice/gate/compass/hourglass/walk/target/gear/sword）并支持 `size` 属性。
2. i18n 词条去 Emoji + `template.csv` 同步；灵石徽标改 `ItemIcon itemId="currency.spirit-stone"`。
3. 逐组件替换；CombatArena 生活目标卡产出图标优先 `resolveItemIconUrl` 图片、资源产出走 `StatGlyph`、赶路卡走 `Icon`。
4. `ids.ts` 等 glyph Emoji → 中性字符。

## 验证

- `node --test "frontend/src/**/*.test.mjs"`：248 过 / 2 失败——`parityRun.test.mjs` 两条模拟器黄金用例（`solo_spell_dot_shield_2h`、`solo_t2_buildings_2h`，断言 `combatSkills.cooldowns[].remainingSeconds` 数值漂移），系工作区既有 TASK-20261114-04 引擎在途改动所致，与本任务纯 UI/文案替换无关。
- `npm --prefix frontend run build`：tsc + vite 生产构建通过（顺带由 `export-i18n-csv.mjs` 重生成 template.csv，同步了 zh 源中的全部词条）。
- `node scripts/check-i18n-keys.mjs` + `node scripts/check-log-markup.mjs`：双门禁通过（3610 条词条）。
- Emoji 复扫（U+1F300–1FAFF / U+2600–27BF / U+2B00–2BFF / U+FE0F）：`frontend/src` 与 `frontend/public/locales` 仅剩 `doudizhuArt.ts` 的 `cardName` ♣♦♥♠——该函数仅产出 `aria-label` 无障碍文本，牌面视觉为内联 SVG 花色符号（`#ddz-suit-*`），非界面 Emoji，按口径保留。

## 实施记录

- `Icon.tsx`：新增 `lock/building/chat/pin/smile/image/alert/bolt/dice/gate/compass/hourglass/walk/target/close` 矢量图标并支持 `size` 像素属性。
- `App.tsx`：顶部行动队列两处灵石徽标改 `<ItemIcon itemId="currency.spirit-stone">`（新增 `.badge-item-icon` 样式）；行动卡锁定 🔒 改 `Icon lock`。
- `CodexPage`：5 处 🔒 + 🏛️ 回退 → `Icon lock/building`。
- `SettingsPage`：头像锁定 🔒 → `Icon lock`。
- Chat 系列：`ChatMessageContent` 👥→`Icon social`、📍→`Icon pin`；`ChatPanel` 😊→`Icon smile`；`ChatSticker` 📷→`Icon image`；`ChatEmojiPicker` ✕→`Icon close`、🖼️→`Icon image`（表情选择功能本身保留）。
- `SocialPage`/`WorldMapPanel`：💬→`Icon chat`、⏳→`Icon hourglass`、🚶→`Icon walk`。
- `ZongmenPage`：🏛️ 回退 ×2 → `Icon building`；`ConnectingPage` ⚡→`Icon bolt`；`ErrorBoundary` ⚠️→`Icon alert`；`CandidatePicker` ⚙→`Icon settings`；`CombatSimulatorModal` ⚔→`Icon combat`。
- `CombatArena` 生活目标卡：`LifeYieldRow.glyph` 重构为 `icon: ReactNode`——物品产出走 `ItemIcon`（缺图标回退行动类型 `StatGlyph` 剪影），修为/灵力/气血走 `StatGlyph experience/mp/hp`，神识/技艺走 `consciousness/enhancement`，Buff 走首字文本兜底；赶路卡 🧭→`Icon compass`；新增 `.arena-life-icon-img`/`.arena-life-glyph-text` 样式。
- `ids.ts`：`ITEM_META_CORES` 172 处 Emoji glyph + `ACTION_TAB_META` 5 处符号字形统一替换为语义单字（材/卷/谱/武/甲/裤/冠/丹/佩/阵/草/盾/匕/矿/香/珠/牙/骨/旗/锤/砧/鼎/符/铃/水/壶/砂/粉/醒/雷/道/踪/炼）；`ItemDisplay.fallbackMetadata` 📘/📜→谱/卷；`marketStore`/`CombatAttributePanel` ⚔️→武；`PlayerCardModal` 🧪→丹。
- 斗地主：牌背 ✦ 与喝彩粒子 ✦ 改 CSS `clip-path` 四角星矢量绘制；♣♦♥♠ 仅存于 aria-label。
- i18n：`spiritStoneBadge`/`randomNameBtn`/`breakthroughBtn`/`openBtn` 四条词条去 Emoji，template.csv 已由构建脚本同步。

## 时间线

- 2026-11-14 03:40:00 需求登记。
- 2026-11-14 全量替换完成，门禁验证通过（单测 2 处失败为既有引擎在途改动所致）。
