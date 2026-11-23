# 《道化太初》Gemini 全仓自动审查 · 待决定问题台账（需二次确认）

> **维护与联动规则（Gemini 全权维护）**：
> 1. 本台账专门收录全仓自动审查中发现的**涉及协议契约变更、玩法/数值语义裁定、数据库/持久化机制调整、跨层重构或历史测试去留**等需你二次确认的问题。
> 2. 每轮自动审查前，Gemini 会先检查本台账：
>    - 已存在于本台账且尚未批注的条目，**绝不重复提报**；
>    - 你可以在任意条目的「**用户裁定批注**」栏填写指令（例如：`同意修复`、`按方案A改`、`不是问题`、`暂不处理`），或在对话中直接告知编号；下一轮自动任务会立即按你的批注执行或排除，并将该条归档移入 [`docs/audits/gemini-audit-resolved.md`](./gemini-audit-resolved.md)。

---

## 待决定条目清单

### 1. `[GA-PEND-001]` WebSocket 连续 PVE 战斗增量包（`SnapshotDelta`）高频重复下发未变冷字段（网络传输损耗）

- **首次检出轮次**：2026-11-24 第 1 轮
- **类别**：可避免的网络传输损耗 / WebSocket 协议载荷设计
- **精确位置**：
  - `src/Server/Clean/ProtocolSnapshotMapper.cs:270-355`（`SectionPayload` 整段投影）
  - `src/Server/Clean/ProtocolSnapshotMapper.cs:544-592`（`ToCombatSection` 每次携带完整 `attributes`）
  - `src/Server/Clean/ProtocolSnapshotMapper.cs:343-353`（`PlayerSectionKind.Duel` 每次携带 `Reports`、`OfflineReport`、`DailyReport`）
  - `frontend/src/network/snapshot.ts:1464-1560`（前端增量分段覆盖合并）
- **现象与量化影响**：
  - 在持续 PVE 讨伐期间，每当战斗伤害、气血灵力或战报计数推进触发 `combat` 与 `duel` 分段标脏时，`ProtocolSnapshotMapper` 会按整段全量投影：
    1. `combat` 分段每次都重新投影并下发完整的玩家面板属性 `attributes`（即使 ` snapshot.WireAttributesChanged == false`、装备/Buff/境界在当帧均未改变）；
    2. `duel` 分段在 `CurrentReport` 每轮更新时，每次都顺带序列化并下发未发生变化的 `Reports`（最多 10 条历史战报列表）与 `OfflineReport`（离线挂机单例战报）。
  - 实测在连续单人 PVE 场景中，`reports` / `offlineReport` / `dailyReport` / 未变 `attributes` 占 WebSocket 下行应用层字节的 **26% ~ 35%**（约 `7.9 MB ~ 16.7 MB / 在线战斗小时 / 人`）。
- **需二次确认原因**：
  - 修改 `combat` / `duel` 分段在 `SnapshotDelta` 中的字段可选性属于前后端共享 WebSocket 协议结构调整，需同步约定断帧重同步（`DeltaResyncPending`）与首次全量 `Snapshot` 的基线合入规则。
- **候选根治方案**：
  - **方案 A（推荐，增量热冷字段按修订号裁剪）**：全量 `Snapshot` 保持全部字段不变；在增量 `SnapshotDelta` 中，当 `snapshot.WireAttributesChanged == false` 时 `combat.attributes` 置 `null`（前端 `snapshot.ts` 保留上一次 `combat.attributes`），`duel.reports` 与 `duel.offlineReport` 仅在列表引用/版本发生变化时随增量下发，未变时省略并由前端保留既有内存引用。
  - **方案 B**：保持当前整段幂等覆盖语义不变，仅依赖已启用的 WebSocket `permessage-deflate`（`ServerMaxWindowBits = 12`）在传输层压缩重复 JSON 子串。
- **用户裁定批注**：`[待决定]`

---

### 2. `[GA-PEND-002]` 持久化 Worker 空队列固定 1 秒轮询产生常驻 PostgreSQL 空事务流量（后端数据库与网络开销）

- **首次检出轮次**：2026-11-24 第 1 轮
- **类别**：可直接优化的性能问题 / 数据库空转开销
- **精确位置**：
  - `src/Server/Clean/PostgresOutboxRepository.cs:66-103`、`src/Server/Clean/OutboxDispatchHostedService.cs`
  - `src/Server/Clean/PostgresSectionRepository.cs:758-835`、`src/Server/Clean/SectionFlushHostedService.cs`
  - `src/Server/Clean/ServerOptions.cs:130-134`
- **现象与量化影响**：
  - `OutboxDispatchHostedService` 与 `SectionFlushHostedService` 默认以固定 `1s` 周期轮询 `platform.integration_outbox` 与 `platform.section_flush_queue`。
  - 即使线上处于无待刷分段、无待派发 outbox 的空闲窗口，两个 Worker 仍每秒各自执行一次 `BEGIN -> UPDATE ... RETURNING (0 rows) -> COMMIT -> 连接池重置`，在真实 PG 协议层产生约 `8.7 ~ 8.8 MB / 小时` 的恒定双向空轮询流量与数据库唤醒开销。
- **需二次确认原因**：
  - 调整刷盘与 Outbox 轮询节拍会影响跨进程入队时的最大可见延迟上限（SLA）。
- **候选根治方案**：
  - **方案 A（有界阶梯退避 + 进程内信号唤醒）**：队列命中记录时保持立即连刷；连续空轮询时从 `1s` 阶梯退避至 `3s ~ 5s` 上限，同时在进程内 `EnqueueSectionsAsync` / Outbox 入队处通过 `SemaphoreSlim` / `Channel` 信号立即唤醒本地 Worker（兼顾本地 0 延迟与空闲期减少 70%~80% 空事务）。
  - **方案 B**：保持固定 `1s` 轮询不变。
- **用户裁定批注**：`[待决定]`

---

### 3. `[GA-PEND-003]` 服务端下发的自由句式战斗/系统日志与前端静态更新日志在英文模式下仍为中文（多语言架构）

- **首次检出轮次**：2026-11-24 第 1 轮（承接历史巡检 `L02` / `L03` 未决架构项）
- **类别**：语言包与跨端协议设计
- **精确位置**：
  - `src/Server/Clean/SimulationLogMapper.cs:34-120`、`src/Ecs/Systems/CombatActionRules.cs`
  - `frontend/src/components/LogFeed.tsx:33-110`、`frontend/src/components/EntityReference.tsx:860-905`
  - `frontend/src/data/changelog.ts:35-420`
- **现象与影响**：
  - 英文（`en-US`）模式下，日志流中的实体标签 `{{item:...}}` / `{{ability:...}}` / `{{enemy:...}}` 已能正确解析为英文名，但服务端 `SimulationLogMapper` 拼接的外层模板句式（如 `完成行动 ...，获得 ...`、`开始搜寻：...`、`施放 ...，对你造成 ... 点伤害`）以及 `changelog.ts` 的版本公告正文仍为中文原文。
- **需二次确认原因**：
  - 若将服务端日志改为下发结构化 `templateKey + args`，会改动 `LogEventDto` 协议与前端日志折叠/正则解析契约；同时你也无需维护不必要的英文长篇公告，需确认英文国际化的目标边界。
- **候选根治方案**：
  - **方案 A**：为 `LogEventDto` 增加可选结构化模板键与参数字典（或在前端 `LogFeed` 对固定服务端句式做无损模式映射翻译），`changelog.ts` 保持中文开发日志不翻译。
  - **方案 B**：当前阶段游戏日志流与更新公告明确以中文为准，将此项直接移入 `gemini-audit-resolved.md` 排除列表，后续不再扫描提报。
- **用户裁定批注**：`[待决定]`

---

### 4. `[GA-PEND-004]` 前端模拟器特质静态表 `combatConstants.ts` 与 `simulator/engine/rules.ts` 双份维护及描述国际化

- **首次检出轮次**：2026-11-24 第 1 轮
- **类别**：前端代码结构 / 语言包覆盖
- **精确位置**：
  - `frontend/src/game/combatConstants.ts:1150-1340`（`TRAIT_METADATA_MAP` 与动态缩放描述拼接）
  - `frontend/src/game/simulator/engine/rules.ts:508-555`（模拟器独立硬编码特质名称与属性表）
- **现象与影响**：
  - `combatConstants.ts` 中的 `TRAIT_METADATA_MAP` 与 `simulator/engine/rules.ts` 各自维护了一份特质 ID、中文名、`quality`、`cost` 与数值映射；且 `combatConstants.ts:1298-1315` 在计算特质成长描述时使用中文 `.replace('10 点', ...)` 硬编码字符串替换，在英文界面下会产生中英混杂或替换失效。
- **需二次确认原因**：
  - 收拢特质元数据真源并改为参数化 i18n 模板（`{value}` 占位符）会触及模拟器同构校验测试（`parityResolve.test.mjs` / `traitDescriptions.test.mjs`）。
- **候选根治方案**：
  - **方案 A**：将特质数值系数统一收拢为单一纯数据表，展示层名称与成长描述全部走 `game.trait.<id>.name` / `game.trait.<id>.description` 参数化占位符，彻底消除 `.replace('10 点', ...)` 中文字符串替换。
  - **方案 B**：保持现有双表与中文替换逻辑不变。
- **用户裁定批注**：`[待决定]`

---

### 5. `[GA-PEND-005]` 外部宗门驻地或未展开节点在同道队伍卡片中的 `nodeId` 显示回退策略

- **首次检出轮次**：2026-11-24 第 1 轮
- **类别**：裸 ID 漏出边界 / 协议投影
- **精确位置**：
  - `frontend/src/components/SocialPage.tsx:46-49`（`nodeLabel(nodeId, nodeNames, anyLabel)`）
  - `src/Server/Clean/Social/SocialRuntime.cs`（队伍 `PartyProfileDto` 节点字段）
- **现象与影响**：
  - 本轮已在 `ZongmenPage.tsx` 修复了玩家处于**本宗**驻地 `node.zongmen.<guid>` 时的显示名称；但在 `SocialPage.tsx` 的公开队伍大厅或聊天分享卡片中，如果某支队伍的目标地点指向**其他宗门**的私有驻地 `node.zongmen.<guid>`（不在静态 `maps` 目录中，且非当前玩家所属宗门），`nodeNames.get(nodeId) ?? nodeId` 仍会回退为原始 `node.zongmen.<guid>`。
- **需二次确认原因**：
  - 涉及非本宗私有驻地在公开队伍列表中应展示为通用文案（「宗门驻地」）还是由服务端在 `PartyProfileDto` 下发 `nodeName`。
- **候选根治方案**：
  - **方案 A（前端零协议改动兜底）**：在 `SocialPage.tsx` 的 `nodeLabel` 与 `entityNames.ts` 中识别 `node.zongmen.*` 前缀，当静态地图与本宗信息均未命中时，统一展示本地化词条 `social.zongmen.residenceFallback`（「宗门驻地」/ `Sect Residence`），绝不向界面暴露 `node.zongmen.<guid>` 裸 ID。
  - **方案 B（服务端协议扩展）**：在 `PartyProfileDto` 中由服务端附带解析后的 `nodeDisplayName`。
- **用户裁定批注**：`[待决定]`

---

### 6. `[GA-PEND-006]` 源码文本正则断言测试与历史里程碑命名测试的清理与下沉（遵循 `AGENTS.md` §9 测试瘦身纪律）

- **首次检出轮次**：2026-11-24 第 1 轮
- **类别**：冗余门禁测试 / 历史过渡命名清理
- **精确位置**：
  1. `frontend/src/components/worldMapVisibility.test.mjs`（读取 `WorldMapPanel.tsx` 源码字符串，用正则断言局部变量名与 `AbortController` 书写形式，而非测试行为）
  2. `frontend/src/game/terminalIdleTelemetry.test.mjs`（读取 `TerminalIdleOverlay.tsx` 源码字符串做正则匹配）
  3. `tests/Server.Tests/PersistenceResilienceV59Tests.cs`（类名带历史版本号 `V59`，内部覆盖的是现行权威死信队列、租约续期与毒丸冷却不变量）
  4. `tests/Server.Tests/ParticipationPersistenceTests.cs:625-628`（测试内部直接硬编码读取 `migrations/0037_v36_to_v37.sql`，而 `AGENTS.md` §3 规定 `migrations/` 应满足“可整体删除而不影响任何编译与测试门禁”的约束）
- **现象与影响**：
  - 上述第 1、2 项在 `AGENTS.md` 规定的 `<ItemTile>` 核心标的网格契约之外使用源码字符串正则锁死组件内部实现细节，重构局部变量名即导致假红灯；第 4 项使 `Server.Tests` 反向依赖了 `migrations/0037_v36_to_v37.sql` 文件存在性，违背了“`migrations/` 可整体删除”的硬规则。
- **需二次确认原因**：
  - `AGENTS.md` §9 规定“禁止删除、跳过或放宽测试来掩盖问题”，清理或改写既有测试文件需你明确授权。
- **候选根治方案**：
  - **方案 A**：
    1. 将 `ParticipationPersistenceTests.cs` 中对 `migrations/0037_v36_to_v37.sql` 的文件读取测试移除或改为基于 `PostgresSchema.Sql` 权威空库结构的行为验证，恢复 `migrations/` 目录可随时整体清空的独立性；
    2. 将 `PersistenceResilienceV59Tests.cs` 重命名为 `PersistenceResilienceTests.cs`（去除历史过渡版本号 `V59`）；
    3. 将 `worldMapVisibility.test.mjs` 与 `terminalIdleTelemetry.test.mjs` 中的脆弱源码正则断言精简或替换为纯逻辑行为单测。
  - **方案 B**：保留现有测试文件与断言不动。
- **用户裁定批注**：`[待决定]`
