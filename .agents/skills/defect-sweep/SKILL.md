---
name: defect-sweep
description: >-
  自动巡检并修复道化太初前后端潜在缺陷：按九个切片（协议信封契约、领域不变量、ECS 通道闸门、持久化迁移、市场经济、前端网络状态、前端 UI 稳定性、i18n 与配表、部署脚本）逐轮取证 → 分诊 → 最小修复 → 门禁验证，跨轮收敛并落盘证据台账。适用于「巡检」「排查潜在问题」「自动发现问题并修」「全仓体检」「协议对不上」等请求，也用于 goal 模式持续收敛。
---

# 全仓潜在缺陷巡检与修复

产出不是评审建议，而是**有 `文件:行号` 证据的真实缺陷 + 已修到门禁通过的代码**。goal 模式会跨轮续跑，因此每轮必须把状态写进磁盘（`docs/tasks/TASK-*.md`），不能只留在上下文里。

## 0. 启动

- **单轮**：`/skill:defect-sweep`（可带参数，如 `/skill:defect-sweep 切片1 只修不审`）→ 只跑一轮（一个切片）。
- **持续收敛**：先 `/goal`，目标句粘贴：

```
按 .agents/skills/defect-sweep/SKILL.md 规程自动巡检并修复前后端潜在缺陷，每轮一个切片，直到该文件「退出判据」满足；报审级只汇总不擅改，禁止 push/deploy。
```

`/goal` 默认每轮结束会继续，所以「退出判据」是本目标唯一的停机条件，必须严格执行。

## 1. 退出判据（满足任一条即停机并出最终报告）

1. 九个切片各至少扫过一轮，且最近一整轮 **零新增自主级缺陷**。
2. 连续两轮既无新证据也无新工具活动（goal 自身在无工具活动时也会停，不要靠它兜底）。
3. 达到轮数上限（默认 8 轮，目标句可覆盖）。

停机必须回答未覆盖切片与残留风险，**禁止**以「已全面检查，无明显问题」收尾。

## 2. 切片清单（每轮只做一片，禁止跨片发散）

| # | 切片 | 取证面 | 典型缺陷 |
| :--- | :--- | :--- | :--- |
| 1 | 协议与信封契约 | `src/Protocol/Contracts.cs`、`src/Server/Clean/ProtocolSnapshotMapper.cs` ↔ `frontend/src/network/{protocol,snapshot,gameClient}.ts` | `WireMessageType` 单边新增或编号漂移、字段名/可空性不一致、快照 gap 未检测、delta 合并竞态、`requestId` 失配 |
| 2 | 领域不变量 | `src/Domain/Player`、`src/Domain/Simulation`、`src/Domain/Combat` | 队列 `RemainingRepeats == -1` 被自动出队、非 `double` 数值、在线/离线/重连三路径结算不一致、快进非确定性、掉落/经验越界 |
| 3 | ECS 通道与闸门 | `src/Ecs` + `docs/backend/ecs-development-guide.md` | 绕过 `MutatePlayer` 直改聚合、脏标记差集漏字段、`CanBulkAdvance` 资格误判、`ShouldUpdate` 闸门失效、段内派发顺序错乱 |
| 4 | 持久化与迁移 | `src/Server/Clean/Postgres*Repository.cs`、`PostgresSchema.cs`、`migrations/` | `SectionCodec.CurrentSchemaVersion` 与 `PostgresSchema.Version` 不同步、CAS/fence 缺失、outbox 丢写、写回失败无恢复证据、表与存档标签脱节 |
| 5 | 市场与经济 | `src/Server/Clean/Market*`、`src/Domain/Market`、`src/Server/Clean/PostgresEconomyRepository.cs` | 非 Serializable、缺行锁、一个分区两个写者、扣款与发货非原子、盘口/回收价与配表不符 |
| 6 | 前端网络与状态 | `frontend/src/network/`、`uiStore.ts` 与各状态 store | 无限静默重连、错误不可见、空函数体 store 方法、高频更新整体重建、竞态下陈旧视图覆盖新快照 |
| 7 | 前端 UI 稳定性与公共组件 | `frontend/src/components/`、`frontend/src/styles/`、`docs/frontend/common-components.md` | 输入焦点/选区/滚动位置丢失、布局跳动与文本溢出、自制组件未复用统一模板、事件与定时器未清理、悬浮层裁切 |
| 8 | i18n / 日志标记 / 配表 | `scripts/check-i18n-keys.mjs`、`scripts/check-log-markup.mjs`、`config/game-data`、`src/DataTool` | 缺词条导致裸显 key、`{{kind:id\|label}}` 转义退化成单花括号、DataTool `--strict` 不过、配表交叉引用断裂、持续消耗品配了正 CD |
| 9 | 部署与运维脚本 | `scripts/*.sh`、`deploy/`、`Dockerfile`、`compose.yaml` | 端口暴露、权限/只读根文件系统、回滚与备份链路、健康端点泄露、Caddy 拦截缺失、引号/`set -e`/退出码缺陷。**可改脚本**，但只做静态修复与本地验证（`bash -n`、`docker compose config`、`--dry-run`）；任何远程执行、镜像推送、Caddy reload、数据操作仍属报审级 |

切片归属歧义时，归到**最靠上游**的切片处理，下游切片只记一行引用，避免同一根因重复修。

## 3. 每轮六步（禁止跳步）

1. **盘点**：先读该切片源码与对应真源文档（`docs/design/总纲/`、`docs/backend/`、`docs/frontend/`），建立事实基线，不预设结论。
2. **取证**：`read` / `grep`；类型、符号、调用点、实现必须走 `xd://lsp`（references/定义/hover），禁止用文本搜索代替代码智能。每条结论落到 `文件:行号`。
3. **分诊**：按第 4 节分级，写清「故障场景 → 触发路径 → 后果」。没有触发路径的一律降级为记录级。
4. **修复**：只改该切片的最小 diff；同一根因一处修，不留 shim、不留双实现、不留兼容分支。
5. **验证**：跑覆盖改动面的最小验证（对应 xUnit 套件 / 前端 build / 契约脚本）；轮次末跑 `scripts/verify.sh`，未跑项必须在报告注明原因。
6. **落盘**：把本轮表格行追加进 TASK 详情文档并更新看板状态；报审级条目累积进待裁决清单。

## 4. 缺陷分级

**自主级（直接修）**——机械、可判定、不改变设计语义：

- 前后端协议枚举/字段/可空性不一致，且仅一侧新写；
- i18n 缺键、日志标记转义退化、DataTool strict 失败；
- 类型错误、空函数体 store 方法、未捕获异常、明显的资源未释放；
- 违反 `AGENTS.md` 硬规则且无歧义：非 `double` 数值、`-1` 被出队、Domain 反向依赖、`/api/debug/*` 出现在非 Development、UI 说明性文案泄漏实现细节；
- 可复现的回归（现有测试已覆盖却红）。

**报审级（只汇总，等用户裁定）**——改变玩法/数值/格式语义或有破坏性：

- 设计与实现语义冲突、数值公式分歧；
- 协议字段增删或信封结构变更、存档格式与 `platform.schema_version` 推进；
- 删除或放宽任何测试、删除文档、`vendor/YuoCore` 内改动；
- 任何生产/部署/远程数据操作、`capacity-target` 阈值调整。

**记录级（不进本轮 diff）**：风格、注释措辞、文档表达 → 分别转 `comment-audit` / `doc-optimize`。

拿不准时**降级**处理，禁止靠猜升格为自主修复。

## 5. 硬约束

- 动手前 `git status --short` 分辨归属：用户已有未提交改动不得覆盖、格式化或夹带进提交。
- 不删/跳过/放宽测试；不新增防御式判空、兼容层、未要求的重试或缓存；不做「顺手」重构。
- 纯文档改动**不提交**；代码修复过门禁后按 `AGENTS.md` §15 自动原子提交（中文 Conventional Commits，只暂存本切片文件）。
- 禁止 `push` / `tag` / `deploy` / 生产数据操作 / `docker compose down --volumes`；切片 9 只允许改脚本本体，验证限定在 `bash -n`、`docker compose config`、脚本自带的 `--dry-run`，不得对远端服务器执行任何命令。
- 不改 `vendor/YuoCore`；不新增 debug/metrics 端点。
- 与既有门禁去重：`doc-audit.mjs` 不在 `verify.sh` 内且不属本目标；`check-i18n-keys` / `check-log-markup` / `check-frontend-contracts` 只在相关切片或轮末执行，不每步重跑。
- 无证据的结论视为幻觉；不确定处标 `[INFERENCE]` 并说明缺口，不得当作已确认缺陷去修。

## 6. 每轮必填表（追加到 TASK 详情文档）

| 轮次 | 切片 | 发现 | 证据（file:line） | 分级 | 处置 | 验证 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |

## 7. 最终报告

- 已修 N 项（列提交哈希与验证命令）；
- 报审 M 项（按严重度排序，每项含证据、建议方案、风险）；
- 记录 K 项（已转交哪个 skill）；
- 未覆盖切片与原因；
- 残留风险与下一步建议。
