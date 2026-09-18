# 道化太初 · 全面并行项目分析（2026-09-15）

- 任务：TASK-20260915-01
- 方法：五路只读子代理并行（A 领域/协议/配表、B 服务端运行时、C 前端、D 测试/运维、E 文档与改动面）+ 主会话量化统计
- 基线：HEAD `7f315fd`，YuoCore submodule `3a7b1e3` 无漂移；工作区 109 个未提交文件（73 已跟踪 +1335/−422），主体为 TASK-20260914-09 审计整改在途

## 0. 规模总览

| 区域 | 文件 | 行数 |
| --- | --- | --- |
| src/Domain | 20 | ~5,200 |
| src/Protocol | 7 | ~500 |
| src/Server | 34 | ~7,300 |
| src/ComputeNode / LoadTests / DataTool | 13 / 15 / 7 | ~760 / ~1,380 / ~210 |
| tests（Domain 82 + Server 39 用例） | 41 | ~3,600 |
| frontend/src | 74 | ~34,000（TS/TSX ~21,800 + CSS ~12,250） |
| config/game-data | 25 JSON | ~6,800 |
| docs/ | ~140 | — |

## 1. 总体结论

代码正确性风险自 08-28 → 09-10 → 09-14 三轮审计后**明显收敛**：协议信封两端逐字节对齐且有跨端契约测试、市场 Serializable + fence 租约完备、快照写回失败证据链完整、备份/PITR/容量门禁真实探测。当前最大风险不在代码内部，而在**工程卫生与未接线链路**：

1. 109 个混合来源未提交改动（含破坏性 schema v2 / 快照 v2），提交切分方式未定；
2. ComputeNode 冷挂机链路整体未接线且补算引擎是"假确定性"；
3. YuoCore ECS/Persistence 依赖声明与实现完全脱节；
4. 若干历史遗留项（market/book 无鉴权、audit_events 只删不写）不在本轮 69 项整改清单内，易漏。

## 2. 高优先级风险（跨路共识）

| # | 风险 | 证据 | 来源 |
| --- | --- | --- | --- |
| H1 | **混合工作区未提交**：73 跟踪 +1335/−422 横跨坊市 UI、时钟修复、图标管线、审计整改四个主题；阻断 `docker-build-prod.sh`（dirty 拒绝），回滚/追责困难 | `git status` 实测；`docs/tasks/TASK-20260914-09.md` 七阶段全未勾选 | E |
| H2 | **schema v2 + 快照 v2 双断点**：现存本地库与 v1 存档将被拒绝启动；`README.md:67` 仍写"schema v1" | `PostgresSchema.cs:311,363`；`snapshot.ts:134` | E/A |
| H3 | **ComputeNode 假补算 + 全链路未接线**：`DeterministicBatchEngine` 只改写 JSON 字段、版本 +1，不复用 `SimulationEngine`；`platform.compute_jobs` 无生产者；Server 无任何 `advance-batch` 调用；无 ComputeNode 测试 | `DeterministicBatchEngine.cs:117-157`；`ComputeNode.csproj` 零项目引用 | B/D/E |
| H4 | **YuoCore 引用悬空**：`Domain.csproj:13-14`、`Server.csproj:14-17` 引用 Abstractions/Ecs/Persistence/Runtime，src 全树零 `using YuoCore`；AGENTS 命名规约（Task*/Executor*/*Component/*System）对应未实施架构，属规则-代码漂移 | grep 零命中；`docs/临时设计讨论/任务队列系统与组件设计草案.md` 标注"尚未进入代码实现" | A/B/D |
| H5 | **`.workbuddy/` 未入 .gitignore**：6 个记忆文件永久污染 `git status`，其中含攻击性操作说明，误提交即入库 | E 路重建 | E |
| H6 | **`/api/v1/market/book` 无鉴权**：内存盘口无界创建 DoS 面，09-10 P3 遗留且不在 69 项清单 | `Program.cs:302-324` | B/E |

## 3. 分路关键发现

### A 路：Domain / Protocol / 配表

- 实际架构为「聚合根 + 静态引擎」（`PlayerAggregate` + `SimulationEngine` 1,145 行），非文档描述的 ECS/Fiber。
- 不变量落地良好：`RemainingRepeats==-1` 永不自动出队（`SimulationEngine.cs:969,992-1007`、`PlayerAggregate.cs:339-357`）、全量 double、整秒量化确定性、`ProtocolSnapshotMapper` 强制配表透传（`ProtocolSnapshotMapper.cs:34,155`）。
- 配表问题：
  - `currency.spirit-stone` 被 `item.` 前缀过滤静默丢弃（`GameDataCatalog.cs:276` vs `materials.json:216`），却是市场货币；
  - `encounter.qi-beast`/`encounter.sunset-ridge` 无任何行动可达，节点↔遭遇双向声明不一致（`combat.json:9` vs `qingyun_mountain.json:19-116`）；
  - `world.travel` 的 `type` 是 `exploration`，`ActionKind.Travel` 分支成死代码（`world.json:6`、`SimulationEngine.cs:1111`）；
  - `ability.palm-lightning` 0 CD；`manaCostRatio*10` 致 heavy-slash 耗魔 100 > 1 级 MP 上限 50（`CombatSkillEngine.cs:90`）。
- 其他：配表热删会 KeyNotFound（`SimulationEngine.cs:177,180`）；死亡玩家软锁（`:478-486`）；`OrderBook._orders` 终态订单无界驻留 + 撮合 O(n) 全扫（`OrderBook.cs:275-307`）；`AdvanceBy`/`ActionIntent` 旧枚举等文档漂移残留。

### B 路：Server / ComputeNode / DataTool / LoadTests

- 运行时实为 `PlayerActor + SemaphoreSlim` 单写者 + `player_runtime_leases` 45s 跨实例租约，无 YuoCore Fiber。
- 持久化链路完整：latest-only `section_flush_queue` + `FOR UPDATE SKIP LOCKED` 批量刷盘 + head checksum + RepeatableRead 合并读；失败保留 `last_error`/退避重试。
- 市场全链路 Serializable + `FOR UPDATE`/`FOR SHARE` + fence token + 幂等键 + fail-closed，质量高。
- **死管道**：`integration_outbox` 无生产者（`UnconfiguredIntegrationEventDispatcher`）；`identity.audit_events` 只删不写；`player_projection`/`player_asset_instances`/`compute_jobs` 仅 DDL。
- ComputeNode 默认绑 `0.0.0.0:8090` 仅靠 token 守门；安全基线（PBKDF2 60 万轮、session 存 hash、健康端点不泄密）合格。

### C 路：frontend/

- 实际栈：React 19 + TS + Vite 7 + Zustand 5 + 自研 i18n + ~12k 行手写 CSS，**未使用 Tailwind**（AGENTS 描述与实际不符）。
- 质量亮点：DHW1 信封逐字节对齐且 decode 全量校验 + section SHA-256；重连指数退避 + 分级自愈非静默；store 无 `() => {}` 虚空方法；高频更新走 rAF/DOM 旁路；公共组件收拢度高；i18n 有类型 + 自动导出模板双闭环（抽查 20/20 命中）。
- 待修：`snapshot.ts:480-489` `mapWorld` 残留虚假默认值（整改未完）；`App.tsx:1575-1589` 功法等级输入逐键发命令 + 清空强制回 1；`CombatArena.tsx` 用中文名匹配服务端内容（改名即静默失效）；~15 处硬编码中文绕过 i18n；`snapshot.ts:535` `isTradable:true` 硬编码。

### D 路：tests / scripts / deploy / Docker

- 测试 121 用例，覆盖核心不变量；`FrontendContractTests` 用真实快照驱动 Vite SSR 解码，跨端契约有真门禁。
- `verify.sh` 链路完整；`capacity-target` 用临时 schema COPY + 真实 WAL 字节测量，预算门禁（12GiB 盘 / 2GiB WAL / 512KiB/s / 30s 补算 / 768MiB 内存）。
- Dockerfile 五阶段、非 root、只读根、OCI 标签、镜像无数据断言全部合规；compose 双文件拓扑清晰；备份（dump+base+WAL 归档）与 PITR、月度归档、ccr-auto-update 回滚链路完整。
- 问题：**smoke-production.mjs 内置默认口令 `Daohua-Smoke-Only-Change-Me-2026!`** 且 `deploy-prod.sh:251` 未注入覆盖 → 公网可用公开口令注册真实账号；`dev-service.sh:24` 卷名陈旧（`daohua_daohua-postgres` vs 实际 `daohua-local-postgres18`）；本地 compose app 为 Development 环境，"生产同构验证"名不副实；生产 app 用超级用户 `daohua` 连库，最小权限角色仅落地本地；`base-backup.sh` 与 ccr-auto-update 不共享锁；无 CI（`.github/` 不存在）。

### E 路：docs/ 与改动面

- 整改实际进度：领域新增 5 文件（BaseAttributeCatalog/PlayerAttributeResolver/CombatSkillEngine/EncounterWaveSelector/PlayerLoadout）、市场 expires_at+税落库、PG18、Caddy 拦截、schema v2、快照 v2、hpRegen 下发均已落地；Domain 83/83、Server 39/39、前端 build 已通过（记忆与会话证据）。
- 整改未完成：69 项文档逐项闭环只做一半（`initial-design.md:58` 重复次数、`common-components.md` 三处失效指路、README schema v1 残留）、浏览器/容器/容量验收未做、提交未做。
- 文档体系：`docs/README.md` 索引缺 `audits/`、`临时设计讨论/`；临时草案引用已删除的 PlayerShardHost/ActionQueueComponent/Fiber 架构；废弃文档堆积；master-design 单真源与多分文档并存的历史病灶未根除。
- 看板：6 个"实质完成待提交"任务堵在同一工作区瓶颈；TASK-20260824-29（悬浮岛屿 UI，3 周）疑似真滞留。
- 未收敛历史项：ComputeNode 未接线（P2）、market/book 无鉴权（P3）、`player_asset_instances` 零读写（C-04）、威胁/多目标与 SpeedBonus 搁置未落锤、i18n 实质单语言（D-06 半闭环）。

## 4. 演进判断

- **已收敛**：08-28 的代码正确性高危（Lazy 变砖、分片停摆、RepeatCount、ToDelta 丢资产等）与 09-10 的大部分玩法缺口（Buff、回复/复活/暴击/五行、市场税与过期）经本轮整改已落地或正在落地。
- **未收敛**：ComputeNode 冷挂机真实链路（容量目标 2000+ 无支撑）、市场读端鉴权、审计事件写入、装备实例语义、第二语言词条。
- **新增**：schema/快照 v2 断点的环境重建成本、巨型工作区提交切分、`.workbuddy/` 污染。

## 5. 建议行动顺序

1. **切分并提交当前工作区**（最紧迫）：按主题拆 commit——审计整改（领域+市场+schema+测试）、坊市 UI、时钟修复、图标资产；提交前补跑 `scripts/verify.sh` 全链路；`.workbuddy/` 先决定入库或入 `.gitignore`。
2. **ComputeNode 决策**：要么让 `DeterministicBatchEngine` 复用 Domain `SimulationEngine`（需解除"零项目引用"约束或抽出共享模拟包）并接通 `compute_jobs` 生产者，要么明确标记为未启用脚手架并从容量叙事中摘除。
3. **YuoCore 依赖决策**：删引用（承认聚合根路线）或排期 ECS 迁移；同步修正 AGENTS 命名规约与 docs 草案的过期描述。
4. **小修批量**：market/book 鉴权、smoke 默认口令移除（改必填 env）、ComputeNode 默认绑 127.0.0.1、dev-service.sh 卷名、`currency.spirit-stone` 入目录、遭遇绑定对齐、`world.travel` type 修正、前端 mapWorld 默认值与硬编码中文清理。
5. **文档闭环**：完成 69 项逐项勾销、`docs/README.md` 索引补齐、临时草案加日期/过期标记、TASK-20260824-29 裁定挂起或拆分。
