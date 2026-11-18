---
name: changelog-writer
description: >-
  《道化太初》面向玩家的双端更新日志采编工作流：盘点上次收录版本以来的 Git 提交与任务看板，筛除工程内部杂项并净化全部非游戏开发术语，编写带图鉴级富文本实体引用与可选首次弹窗（noticeId）的游戏内更新日志（frontend/src/data/changelog.ts），以及符合 Git游戏（gityx.com）专栏字数与预设规范的外部更新日志草稿。适用于「写更新日志」「补齐近期版本公告」「更新游戏内日志」「准备 gityx 更新日志」等请求。
---

# 道化太初 · 更新日志采编 Skill（changelog-writer）

本 Skill 用于将代码提交与开发任务转化为**专业、准确、通俗易懂且零开发黑话**的玩家向更新日志，统一覆盖「游戏内更新日志页 / 首次登录弹窗」与「Git游戏（gityx.com）外部专栏」两条链路。

---

## 一、核心文风与玩家视角红线（最高优先级）

更新日志的目标读者是**修仙放置玩家（道友）**，不是程序员或运维工程师。每一条描述必须回答「**我在游戏里能体验到什么变化、有什么用、数值怎么变**」。

### 1. 严禁出现的非游戏开发术语（零容忍）

正文、标题、导语中**绝对禁止**出现以下内部工程词汇（已由 `frontend/src/data/changelog.test.mjs` 正则门禁硬拦截）：

| 禁用开发黑话 | 玩家向正确表达 |
| :--- | :--- |
| `ECS` / `Domain` / `Shard` / `Pipeline` / `Tick` | 直接写实际结算/战斗行为（如「优化战斗状态结算」） |
| `Schema v58` / `DDL` / `PostgreSQL` / `WAL` / `迁移` / `落盘` / `持久化` / `分表` | 不写纯数据库内部改造；若带来跨端体验，写「支持跨设备同步」 |
| `Canvas 2D` / `VFX` / `WAAPI` / `DOM` / `SVG` / `DPI` / `Emoji` | 「重制技能与普攻特效」「统一全界面国风图标」「适配高分屏显示」 |
| `WebSocket` / `WS` / `DTO` / `Snapshot` / `ETag` / `API` / `Origin` | 「优化断线重连」「常规更新不再中断在线状态」 |
| `Caddy` / `Docker` / `Compose` / `systemd` / `CI` / `GitHub Action` / `Google Drive` / `rclone` / `odin` / `yuohira` | 运维基建内部改动**直接整条略过不写**；玩家可见的网关维护页写「新增服务器更新维护提示页，发版期间可自动检测恢复并刷新进入游戏」 |
| `MOSS` / `RMBG` / `ComfyUI` / `cf-uploader` / `Worker` | 「补齐新妖兽立绘与功法秘籍图标」「升级传音表情包云端同步」 |
| `TypeScript` / `WASM` / `React` / `Vite` / `localStorage` / `IndexedDB` | 「重构内置战斗模拟器，秒级完成演武测算与收益对比」 |
| `is_bot` / `MarketBots` / `种子脚本` / `安全验签` / `HMAC` / `XSS` / `死信` / `锁序` | 机器人屏蔽、防作弊、安全漏洞加固一律**不写入或仅概括为「提升服务与坊市结算稳定性」** |
| `Q1~Q5` / `T1~T5` / `AD` / `AP` / `HP` / `MP` / `CD` / `Lv.20` | 使用游戏内标准词：`凡/黄/玄/地/天品`、`一阶/二阶`、`物理攻击/法术攻击`、`气血/灵力`、`冷却`、`20 级` |

### 2. 应该写什么 vs 应该略过什么

- **必须重点写**：新地图/新节点、新功法/新特质/新状态机制、新系统（如签到/传书/天榜/小游戏/挂机终端）、影响玩家流派与收益的数值平衡调整（经验曲线、掉率、装备属性、回收价格、技艺倍率）、高频交互优化、玩家实际遇到过的体验 Bug 修复。
- **合并一句话带过**：多项底层极端边界修复（如「修复多项极端情况下的战斗状态清理与坊市挂单结算稳定性问题」）。
- **彻底略过不写**：CI/CD 流水线、备份同步、数据库索引/表结构/留存清理、代码重构（玩家无感知）、文档治理、单测门禁、密钥轮换、反多开/防刷内部策略。

---

## 二、标准执行流程

### 步骤 1：盘点增量范围

1. 读取 `frontend/src/data/changelog.ts`，查看 `CHANGELOG_ENTRIES[0]` 的最新收录日期与 `versionLabel`（如 `v0.3.6`）。
2. 执行 Git 与看板盘点，列出未收录区间的提交与任务：
   ```bash
   git log <上次发布commit>..HEAD --oneline
   ```
   并对照 `docs/tasks.md` 顶部记录及对应 `docs/tasks/TASK-*.md`，核实每项改动的**真实配表数值**（切勿凭印象编造百分比、层数或概率）。
3. **向用户复述理解与范围**（覆盖日期/版本、分期方式、是否设 `noticeId` 触发首次登录弹窗、是否同步生成外部渠道草稿）。

### 步骤 2：校验配表实体 ID 与显示名（游戏内富文本必做）

游戏内更新日志支持 `{{kind:id|label}}` 富文本胶囊，玩家鼠标悬停即可直接弹出万象图鉴级详情卡片（地名支持点击直达节点）。

支持的 `kind` 清单与真源核查路径：

| `kind` | 含义 | 配表/代码核查真源 | 示例 |
| :--- | :--- | :--- | :--- |
| `item` | 物品 / 装备 / 秘籍 / 货币 | `config/game-data/items/*.json`、`equipment/*.json` | `{{item:item.dragon-pearl\|龙珠}}`、`{{item:equipment.qingfeng-sword\|青锋剑}}` |
| `ability` | 功法技能 | `config/game-data/abilities/*.json` | `{{ability:ability.tianlei-po\|天雷破}}` |
| `trait` | 先天特质 | `frontend/src/game/combatConstants.ts` (`TRAIT_DEFINITIONS`) | `{{trait:trait.jianyuxin\|剑与心}}` |
| `buff` | 战斗状态（增益/减益） | 特质 `buff`、功法 `buff`/`buffs`/`onHitBuff`/`selfBuff`、物品 `buffEffects` | `{{buff:buff.mabi\|麻痹}}`、`{{buff:buff.shibukedang\|势不可挡}}` |
| `enemy` | 妖兽怪物 | `config/game-data/monsters/*.json` | `{{enemy:enemy.mist-sword-wraith\|雾隐剑魄}}` |
| `building` | 公共/宗门建筑 | `config/game-data/buildings/*.json` | `{{building:building.rejuvenation-array\|回春阵}}` |
| `action` | 修行/讨伐/百艺行动 | `config/game-data/actions/*.json` | `{{action:combat.sweep-wumeng-marsh\|讨伐 雾梦泽}}` |
| `location` | 大地图节点 | `config/game-data/maps/*.json` (`nodes[]`) | `{{location:node.sword-abyss\|剑冢沉潭}}` |

**硬性语法铁律**：
1. `label` **必须与配表 `name` 字段逐字完全一致**（如秘籍带书名号 `《天雷破秘籍》`、讨伐带空格 `讨伐 雾梦泽`），否则 `changelog.test.mjs` 会直接断言失败。
2. 正文行内**禁止出现 `v0.3.14` 这类版本号字符串**（会被 `ValueText` 数值胶囊切碎）；版本号一律只写在卡片头部的 `versionLabel` 字段中。
3. 同一段话首次提及某核心实体时挂接 `{{kind:id|label}}` 即可，同一句内重复提及时用普通纯文本名称，避免满屏胶囊影响阅读呼吸感。

### 步骤 3：编写游戏内更新日志（`frontend/src/data/changelog.ts`）

1. 按日期严格**倒序**（最新日期在数组最前）编排 `CHANGELOG_ENTRIES`。
2. 合理划分 `sections` 分类：
   - `feature`（新增内容）：新地图、新功法、新玩法系统、核心新功能；
   - `optimize`（体验优化）：界面交互、操作减负、美术与特效补齐、性能流畅度提升；
   - `balance`（数值平衡）：经验、掉率、装备词条、妖兽强度、技艺产出调整；
   - `fix`（问题修复）：玩家可感知的异常修正。
3. **`noticeId` 弹窗策略**：
   - `noticeId` 与客户端版本号解耦。
   - **需要向全服玩家主动弹窗告知**的大版本/阶段汇总：在最新一期设置全新唯一标识 `noticeId: 'notice-YYYYMMDD-vX.Y.Z'`。
   - **仅做日常小修补、不想打扰玩家**：不设 `noticeId` 字段（保持历史最新 `noticeId` 不变，已读玩家不会二次弹窗）。

### 步骤 4：编写 Git游戏（gityx.com）更新日志草稿（按需）

当用户要求同步产出 `gityx` 更新日志时，注意区分**「追加到游戏主条目（`1575.html`）原说明底部」**与**「单独发布更新日志专栏文章（`tid=65`）」**两种场景：

1. **默认采用「游戏主说明追加模式（一行一个版本 / 一行一期，极简凝练）」**：
   - 因游戏原说明有严格的 1500 字总长度空间限制，更新记录必须极度精炼——**一个版本号（或单日版本段）严格只占一行**，每行控制在 **40~70 字** 左右，只保留玩家最关心的硬核干货（新地图、新系统、核心功法、关键减负调优），省略次要细节修补。
   - **剥离所有游戏内专属富文本标记**：将 `{{kind:id|label}}` 还原为自然中文。
   - 推荐行格式：`- **2026-11-17 (v0.3.14)**：新增纯字符挂机终端与战斗特效开关，同道斗法支持自动消耗品，表情包升至 100 上限并支持跨设备同步。`
2. **若明确要求单独发 `tid=65` 专栏长文时**：
   - **标题（`--title`）**：`《道化太初》vX.Y.Z 更新日志：<核心亮点>`；
   - **摘要（`--description`）**：**150 字以内**；
   - **正文（`body`）**：**1500 字以内**。
3. **安全发布边界（与 `gityx-publish` 技能联动）**：
   - 若用户说「先不提交 / 先看草稿」，将草稿保存至对话临时目录并在回复中直接展示（标注字数统计），**绝不私自调用不带 `--dry-run` 的 `gityx-publish.mjs publish`**。
   - 仅当用户审阅定稿并明确下达发布/编辑指令后，才按 `.agents/skills/gityx-publish/SKILL.md` 先跑 `--dry-run` 再跑 `--confirm`。

### 步骤 5：门禁验证

每次修改 `frontend/src/data/changelog.ts` 后必须执行并通过：

```bash
node --test frontend/src/data/changelog.test.mjs
node scripts/check-log-markup.mjs
npm --prefix frontend run build
```

若涉及跨端或准备直接提交交付，按仓库规范补跑 `bash scripts/verify.sh --frontend`（或 `--full`）。
