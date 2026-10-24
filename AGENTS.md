# AGENTS.md - 道化太初开发与发布规则

本文件适用于仓库根目录及全部子目录。用户当次明确要求优先于本文件；存在冲突时先说明冲突和风险，再等待用户裁定。

## 1. 语言与沟通

- 所有交流、计划、总结、注释、文档和 Git 提交说明使用中文。
- 文件名、命令、类型名、变量名、函数名、协议字段、框架和库名可保留英文。
- 修仙等难以自然英译的专用词汇（如「气」「灵根」「功法」），代码标识符直接使用拼音（`Qi`/`Linggen`/`Gongfa`），禁止生造易混淆的英文近义词；命名与中文语义的对照以 `docs/design/总纲/19-名词术语对照表.md` 为准。
- 不输出冗长思维链，只说明关键依据、结论和必要取舍。
- 生产问题先回答：是否仍在发生、是否有数据风险、现在是否需要处理。
- **需求理解与复述先行**：每次当用户提出新的需求之后，**必须先理解并复述用户的核心需求，给出自己的明确理解**（包括但不限于：业务目标、核心逻辑预期、影响范围与实现边界）；确保双方对需求预期完全对齐后再展开实现，严禁未经验证理解就盲目推进或自说自话。

## 2. 项目权威边界

- `src/Domain`：纯领域库——POCO 状态、配表、公式、编解码、市场模型与触发器契约（`PendingCombatTrigger`/`ICombatTriggerSink` 接口）；行动/战斗调度引擎已迁入 `src/Ecs`，Domain 不得依赖 ASP.NET Core、Docker、数据库、浏览器实现或 `YuoEntity`/`YuoComponent` 运行时对象。
- `src/Protocol`：服务端、浏览器和压测共享的纯数据协议；不得暴露领域聚合、运行时所有权对象或存储实现对象。
- `src/Server`：ASP.NET Core 宿主、WebSocket、运行时装配和持久化适配。
- `src/ComputeNode`：可选高延迟冷挂机计算节点；只执行确定性补算，不持有玩家权威状态，不直接访问生产数据库。
- `src/LoadTests`：负载场景，不是生产运行时依赖。
- `src/DataTool`：游戏数据 JSON 校验与指纹工具；独立命令行项目，无项目引用。
- `frontend`：唯一正式 React/Vite 前端。
- `../DaoHua-Private`（兄弟私有仓库，非 submodule）：反多开等不适合开源的扩展模块实现——`server/` 经 `Server.Clean.csproj` 条件 `Compile Include` 注入 `DaoHua.Server.Clean` 程序集，`frontend/antiMulti` 经 junction 挂载 `frontend/src/private-impl/`（.gitignore）后由 vite alias 切换，`tests/` 为私有回归门禁。主仓库对其零 Git 引用；存在即完整版构建、缺失即开源版，真源 `docs/backend/private-modules.md`。
- `src/Ecs`：游戏 ECS 层——组件、系统、分片、行动管线与门面（`EcsGameRuntime`/`EcsWorldShard`/`EcsActionPipeline`），`Engine/` 承载 `SimulationEngine` 行动/资源/旅行/战斗边界调度，`Combat/` 承载 `DeterministicCombatEngine`/`DuelScriptEngine` 与触发器域（`RunSystem<tag>` 段内派发），生产运行时的游戏状态与调度实现。
- `tests/Ecs.Tests`：ECS 运行时与适配器边界回归门禁（`EcsGameRuntime`/`EcsWorldShard`/`EcsPlayerRuntimeAdapter`）。
- `tests/Domain.Tests`：领域和协议回归门禁。
- `tests/Server.Tests`：服务端装配、持久化与跨边界协议回归门禁。
- `tools/`：本地开发辅助工具（`config-editor` 独立 Vite 应用、`combat-simulator.html`），不进入生产镜像。
- `art/`：美术与图像设计源文件、分层稿件与高分辨率母模资产库；被 `.dockerignore` 全局忽略，不进入生产镜像与构建上下文。所有 AI 原图、源 PNG、分层稿必须归档于此；生产前端 `frontend/public/` 与 `frontend/src/assets/` 只接收经过优化提取的单一最优运行时格式（全部角色形象统一 **128px WebP q80**，道具图标统一 **48px WebP q90**；矢量与高分辨率母模归档 `art/avatars/`、`art/items/`），严禁未压缩的原始 PNG 源码直入前端制造生产死重。
- `deploy/`：生产部署资产（`compose.prod.yaml`、systemd 单元、备份/归档/回滚脚本、Caddy 站点配置、PG 角色 SQL）。
- `vendor/YuoCore`：固定版本的 Git submodule（上游的 `ecs-optimized` 分支），生产运行时内核。除非任务明确涉及 YuoCore，不得在本仓库任务中修改 submodule 内容。**在 submodule 内工作时，必须遵循其仓库内 `AGENTS.md` → `docs/ai-development-guide.md` → `docs/yuocore-ecs-kernel-rules.md` 的规范链**（ECS 内核的稠密索引纪律、生命周期语义红线、观测零成本与消融归因门禁以该链为准）；submodule 内的提交在其自身分支完成且不推送，本仓库只更新 submodule 指针与 `scripts/bench-service.sh` 的 `CORE_SHA` 钉。
- `data`：运行数据，不进入镜像，不提交 Git，不随代码发布覆盖。

依赖方向必须保持（以各 `.csproj` 实际 `ProjectReference` 为准）：

```text
Server -> src/Ecs + Server.Clean + Domain + Protocol + YuoCore(Abstractions/Ecs/Runtime) + Npgsql
src/Ecs -> Domain + YuoCore(Abstractions/Ecs/Runtime)（游戏 ECS 组件/系统/分片/管线）
Domain -> YuoCore(Abstractions/Ecs)（纯领域原语引用，不持有宿主所有权对象）
Domain.Tests -> Domain + Protocol + src/Ecs（行动/战斗引擎已迁入 Ecs 层，领域语义测试经其公共入口回归）
Server.Tests -> Server + Domain + Protocol + ComputeNode
Ecs.Tests -> Server + src/Ecs + Server.Clean + Domain + Protocol
LoadTests -> Protocol + Npgsql
ComputeNode -> Domain + DaoHua.Ecs（共享 ECS 执行环境推进确定性补算：Entity/System/Pipeline 与主服同一代码路径，禁止任何宿主/存储引用）
DataTool -> 无项目引用（独立可执行进程/工具）
```

单内核直引：全仓只存在 `vendor/YuoCore` 一个内核根，所有 `ProjectReference` 为普通引用，不得重新引入 `YuoCoreRoot`/`EcsBuildVariant`/`AdditionalProperties` 等变体属性。同名程序集双变体机制已随基线 submodule 退役。

Domain 不依赖 Protocol；禁止让 Domain 反向依赖 Server、前端或部署层。跨层需求优先放入现有适配边界，不新增循环依赖。

## 3. 当前生产架构与容量目标

- 项目内部代表名统一使用 `daohua` / `DaoHua` / `DAOHUA`；不得新增历史旧名的任何大小写形式作为命名、卷名、环境变量或服务名。
- 当前内测阶段允许直接破坏历史数据、存档格式和部署拓扑兼容性；为了达成容量与落盘目标，可以直接改表、改快照和清空内测数据，但生产/远端真实数据操作仍必须先备份并获得明确许可。
- **本地开发阶段兼容清零（硬规则）**：当前工作默认处于本地开发/内测阶段，不设计、不实现任何历史兼容路径。旧数据、旧快照、旧数据库 schema、旧 API/WebSocket 字段、旧协议、旧部署拓扑和旧组件模型均可直接废弃；禁止为了它们新增 migration runner、双读双写、兼容 DTO、legacy flag、fallback adapter、旧字段回填或过渡分支。允许一次性改表、改快照、删旧模型和清空本地数据。所谓“行为基线”只用于保护当前明确的业务结算语义，不等于保留历史格式或历史调用方式。**清零玩家存档前必须先尝试对应版本的迁移脚本；迁移不可用或失败时，不得在无人值守下自动删除——必须暂停启动并在控制台交互确认（无 TTY 直接中止），操作者明确确认后才允许执行重置。**
- **兼容性迁移目录（唯一例外）**：数据库兼容性迁移脚本允许存在，但必须统一收拢在仓库根目录 `migrations/` 独立文件夹中，满足**可整体删除**约束：不参与任何项目编译、不被运行时装配或启动门禁引用、不进入镜像构建上下文；空库启动永远由权威 `PostgresSchema.Sql` 直建，删除 `migrations/` 后全新初始化行为完全不变。仍禁止在代码中实现 migration runner 或对迁移目录的运行时依赖。文件统一为 `NNNN_vX_to_vY.sql`：`platform.schema_version`（本地库内容版本，涵盖表结构与存档载荷标签）兼容性迁移，序号全局递增；存档 `SectionCodec.CurrentSchemaVersion` 每次升级时 `PostgresSchema.Version` 须同步 +1，并在对应版本迁移文件里内嵌 `UPDATE ... SET schema_version` 标签推进（前提：新字段可缺省解码，否则该版本走交互确认清零）。本地开发启动链（`reset-outdated-players.sh` 经 `dev.sh`）可自动执行本链，人工亦可显式调用 `migrate-local-db.sh`。
- 本地开发可以直接破坏代码和数据结构；只有实际操作远程/生产数据时才执行备份、审批和可恢复性检查，这属于数据安全要求，不得反向变成代码兼容层。
- 目标服务器按 `2c4g/40g` 预算设计：至少支持 `5000+` 注册账号存储、`100+` 持续网络收发、`500+` 高峰网络收发、`2000+` 偶尔上线/离线挂机玩家补算。
- 主存储固定为 PostgreSQL：账号、玩家快照、durable outbox、市场账本和市场流水均落库；应用容器不挂载数据卷。
- 市场账本使用 PostgreSQL Serializable 事务、行锁及带 fence 的分区租约；`Market:PartitionCount` / `Market:PartitionId` 可保留分布式分片边界，但同一分片只能一个写者持有。
- 远程个人设备只能作为高延迟冷挂机计算节点，经 compute-link WebSocket **反向拨入**主服 `/internal/compute-link`（节点多为无公网 NAT 设备，只拨出不监听；节点本地 HTTP 口仅作调试）；节点不得承载在线连接、市场写入、账号鉴权或数据库写权限。
- 正式前端、HTTP API 和 WebSocket 使用同一来源；生产代码不得硬编码开发端口。
- 当前服务端不存在任何 `/api/debug/*` 或 `/api/metrics` 端点；任何 debug/metrics 端点只允许在 `Development` 环境存在，生产 Caddy 必须显式拦截对应路径（拦截规则已配置）。
- 账号鉴权已落地：`/api/v1/auth/register|login|logout|session`，PBKDF2 密码哈希 + HttpOnly Cookie 会话 + 认证限流；客户端不再仅凭本地 UUID 标识玩家。该实现为内测口径——涉及充值、真实资产或开放注册前，必须先完成服务端身份认证、授权与审计的强化评审。
- 防多开子系统已私有模块化（TASK-20261024-04）：实现在兄弟私有仓库 `DaoHua-Private`（永不公开），主仓库只保留中性扩展点 `src/Server/Clean/Admission/`（`IHelloAdmissionHook` + 默认开放语义 + `IHostModuleStartup` 反射发现）与前端契约 stub `frontend/src/private/`。拼装遵循「存在即完整，缺失即开源」：`Server.Clean.csproj` 探测 `../DaoHua-Private` 注入 `server/**/*.cs`，前端经 `scripts/sync-private-modules.mjs` junction 挂载 `src/private-impl/` + vite alias 切换；完整版服务端无条件校验 Hello `ModuleDeclaration`（缺失即 4010 拒绝开源前端）。关闭码占用：4001-4004 鉴权、4005-4008 与 4010 反多开（私有）、4009 慢消费者。拼装真源见 `docs/backend/private-modules.md`，裁决语义见 `docs/backend/anti-multi-open.md`。

## 4. 修改前检查

每次修改前必须：

0. **文档先行**：`docs/design/总纲/` 为设计真源。若实现与设计文档存在偏差，必须先修订文档对齐设计意图，再修改实际逻辑；禁止让代码与文档长期分叉。新设计同样先落文档再落地实现。
1. 阅读相关代码、配置和调用点。
2. 执行 `git status --short`，区分用户改动和本次改动。
3. 执行 `git submodule status`，确认 YuoCore 没有缺失或漂移。
4. **复述需求并阐明理解**：向用户明确复述需求、说明预期与边界；评估任务规模并确立调度策略（小型单 Agent，大型若需多 Agent 须先行声明分工）。
5. 确认真实工具链：.NET 10、Node.js 22、npm 和 Docker Buildx。
6. 涉及生产时先只读核对 DNS、SSH、容器、持久卷、Caddy 和健康端点。
7. 涉及前端开发时，必须先查阅前端公共组件文档 `docs/frontend/common-components.md`，核对已有统一模板与复用组件。
8. 凡是用户发起的**非 Bug 修复**性质任务，必须先分析提示词和需求，登记写入 `docs/tasks.md` 任务面板并记录当前时间。

禁止覆盖、丢弃、暂存、格式化或提交用户已有的无关改动。

## 5. 本地开发命令

首次克隆：

```bash
git submodule update --init --recursive
npm --prefix frontend ci
```

启动服务端：

```bash
dotnet run --project src/Server/Server.csproj
```

启动正式前端开发服务器：

```bash
npm --prefix frontend run dev
```

Vite 将 `/api` 和 `/ws` 代理到 `127.0.0.1:5128`。只有连接外部后端时才设置 `VITE_API_URL` 或 `VITE_WS_URL`。

最小完整验证（交付/合并前必跑全量）：

```bash
scripts/verify.sh --full
```

无参数的 `scripts/verify.sh` 是日常快速口径（跳过 PostgresIntegration 与 Compose 校验），仅用于迭代自检。

本地容器编排验证（根 `compose.yaml`，app 以 `Development` 运行，仅用于本地容器化行为排查）：

```bash
docker compose -f compose.yaml up -d --build
node scripts/smoke-production.mjs http://127.0.0.1:5128
docker compose -f compose.yaml down
```

生产同构拓扑验证（真实生产编排为 `deploy/compose.prod.yaml`，要求预先构建好的不可变镜像；app 以 `Production` 运行，端口 `127.0.0.1:15128`）：

```bash
DB_PASSWORD='<本地验证口令>' APP_IMAGE='daohua:prod-verify' COMPUTE_IMAGE='daohua-compute:prod-verify' \
COMPUTE_TOKEN='<本地验证令牌>' docker compose -f deploy/compose.prod.yaml up -d
node scripts/smoke-production.mjs http://127.0.0.1:15128
docker compose -f deploy/compose.prod.yaml down
```

`docker compose down` 不得附带 `--volumes`，除非用户明确要求删除本地数据。

常用脚本清单（`scripts/`，全部以仓库根目录为工作目录执行）：

| 脚本 | 用途 |
| --- | --- |
| `verify.sh` | 合并必跑最小门禁：工具链、显式 restore + `--no-restore` Release 构建、Domain/Ecs/Server 三套测试（自动供给 `daohua_market_test` 集成测试库，并按 trx `executed` 实际数硬校验非空，防「零匹配静默全绿」）、DataTool 校验、前端 i18n/日志标记/`node --test` 单测/生产构建、Compose 配置；默认无参数即日常快速口径（跳过 Compose 与 PostgresIntegration）；`--full` 才是合并/交付全量，`--backend` / `--frontend` 可叠加 `--full`；容量门禁不在其中，见 `capacity-target.sh`；双口径：存在 `../DaoHua-Private` 时自动升级完整口径（追加私有测试套与私有前端类型检查），开源环境保持纯开源口径 |
| `dev.sh` | 一键启动本地前后端联调（数据库自检、端口检查、后端热重启、前端 Vite、Docker 化 Caddy 域名入口 `http://daohua.localhost`）；交互终端由 TUI 前台接管（启动流程在后台子 shell 完成），`q`/`Ctrl+C` 停止，`:` 进入指令模式（help 查看），非 TTY 使用普通日志流 |
| `dev-tui.mjs` | `dev.sh` 的无依赖 TUI 运维控制台（前台直读终端输入、事件驱动 + 行级 diff 渲染）：日志滚动回看/来源与级别过滤/长行折行，`:` 指令模式直连 `/ops/api` 查询与写操作 |
| `dev-hot.mjs` | C# 源码监听与后端平滑热重启（由 `dev.sh` 调用）：只监听 Server 真实编译闭包（`src/Domain`/`src/Ecs`/`src/Protocol`/`src/Server`/`src/Server.Clean` + `config/game-data` + `vendor/YuoCore` + 存在时的 `../DaoHua-Private/server`），默认静默 3s 防抖后重建重启，构建窗口内的变更会补跑 |
| `sync-private-modules.mjs` | 私有客户端模块挂载同步：把 `../DaoHua-Private/frontend/antiMulti` 以 junction/symlink 挂载到 `frontend/src/private-impl/`（缺失时清理残留），`dev.sh` 与 `verify.sh` 前端面自动调用；真源 `docs/backend/private-modules.md` |
| `dev-service.sh` | 本地容器化同构环境管理：`up`/`restart`/`down`/`status`/`logs`/`smoke` |
| `config-editor.sh` | 启动 `tools/config-editor` 可视化配表编辑器（127.0.0.1:5199） |
| `capacity-target.sh` | 真实 PostgreSQL/WAL/latest-only Worker 容量门禁（容量相关变更与发布前运行，不在 `verify.sh` 合并必跑内） |
| `smoke-production.mjs` | 生产/容器烟测：健康、就绪、同源静态入口与 WebSocket 首帧 |
| `check-frontend-contracts.mjs` | 前后端协议/契约一致性检查（被 `tests/Server.Tests` 调用） |
| `check-i18n-keys.mjs` | 前端 i18n 词条覆盖门禁：源码字面量键 + 配表驱动动态键族（物品/装备/行动/流派/目标/技艺/槽位/状态）全量比对中文词典，缺失即失败（`verify.sh` 必跑） |
| `check-log-markup.mjs` | 日志富文本标记转义门禁：扫描 C# 插值字符串里「少转义一层」的 `{{kind:id\|label}}`（会退化成单花括号导致前端原样显示），`verify.sh` 必跑 |
| `frontend/src/**/*.test.mjs` | 前端纯逻辑单测（`node:test`，`verify.sh` 必跑）：图鉴材料分类互斥完备、境界命名、建筑/图鉴/境界计算、战斗反馈、背包分组、聊天与日志未读、战斗模拟器确定性 |
| `export-i18n-csv.mjs` | 导出前端 i18n 词条模板 CSV |
| `optimize-svg.mjs` | SVG 美术资源优化 |
| `docker-build-prod.sh` | 生产镜像多阶段构建与 CCR 推送（`--push`/`--push-existing`） |
| `deploy-prod.sh` | 生产服务器受控更新（`--image <不可变镜像>`） |
| `release-prod.sh` | 正式服一键全自动打包与发布：前置门禁自检、三路并发（CCR 镜像构建推送 + 远端数据备份 + odin 掌机 arm64 交叉编译预传）、自动提取不可变 sha256 镜像并触发部署与双层烟测 |
| `install-ccr-auth.sh` | 服务器 CCR 登录态安装（`docker login --password-stdin`） |
| `init-local-db-roles.sh` | 本地 PostgreSQL 角色初始化（对齐 `deploy/postgres-local-roles.sql`） |
| `migrate-local-db.sh` | 本地库兼容性迁移（按 `platform.schema_version` 链式执行 `migrations/` 落后脚本；`--dry-run` 空跑）。由 `reset-outdated-players.sh`/`dev.sh` 启动链自动调用，也可人工执行；运行时与门禁均不引用 |
| `reset-outdated-players.sh` | 本地库版本漂移处置：存档行或 `platform.schema_version` 落后时优先执行 `migrations/` 迁移链（经 `migrate-local-db.sh`）；迁移后仍有不可迁移存档漂移时须控制台交互确认（输入 RESET）才重置玩家游戏域数据（保留账号/角色名），拒绝或无 TTY 则中止启动；`dev.sh` 启动时自动调用，仅作用于本地回环库，`--dry-run` 只检测 |
| `backup-pull-sync.sh` | 计算节点端定时拉取云端主服 PostgreSQL 备份、本地长期归档与 SHA-256 完整性对账脚本（位于 `deploy/`） |
| `install-backup-sync.sh` | 计算节点端一键安装并启用 systemd 备份拉取定时器脚本（位于 `deploy/`） |

## 6. 实现规则

- **需求规模与 Agent 协同分级机制（硬规则）**：
  - **小型需求严禁多 Agent 开展**：凡局部改动、单模块/单文件逻辑调整、小型功能、Bug 修复、样式或文案微调、简单配置变更等小型需求，**严禁启动多个 Agent**；必须由主 Agent 在单一上下文中独立、高效、低开销地直接闭环完成，杜绝上下文同步割裂、文件写冲突与碎片化派发。
  - **大型需求多 Agent 先行明示分工**：仅当需求具有跨前后端、跨独立领域与基础设施、或多份独立资产并发处理等明确正交分界的大型需求时，才可考虑使用多 Agent 并发开发。**但在启动多 Agent 开发之前，必须先在回复或计划中向用户明确表明每个 Agent 的具体负责情况**（包括：Agent 角色与标识、负责的子系统与文件边界、具体任务目标与交付物契约），待分工透明清晰后再行派发；各 Agent 完成后统一由主 Agent 负责集成并在主上下文执行全局门禁验证。
- 只修改当前任务直接相关文件，保持 diff 精简、集中、可审查。
- 优先复用现有类型、服务、组件、目录和协议，不复制同类实现。
- 修改函数签名、协议或数据结构时同步更新全部调用点和测试。
- 不用特殊 case 掩盖根因，不留下 debug 输出、临时代码、无说明 TODO 或无用文件。
- 错误处理必须明确；不得静默吞掉持久化、结算、鉴权和部署异常。
- 不随意修改公共 API、WebSocket 包结构、事件名、配置键和存档格式；但在当前本地开发阶段，如任务要求重构，可以一次性修改并同步全部调用点、文档和测试，禁止为了旧调用方保留兼容层。
- **禁止过度防御式编程**：只在外部输入、跨边界数据、持久化载荷和明确的领域不变量处做必要校验。类型系统、状态机、聚合单写者所有权已经保证的内部路径不得层层重复判空、重复校验、吞异常、静默回退、凭空填默认值或添加未经需求支持的兜底分支；内部不变量被破坏时应快速失败并保留清晰错误证据。
- 不为假设中的未来需求提前增加抽象、配置开关、兼容分支、重试、缓存或降级路径；每个保护逻辑必须能指出真实故障边界、恢复动作和对应验证。
- 内测阶段可破坏旧数据兼容；若变更数据格式或部署拓扑，必须同步更新 PostgreSQL schema、备份/恢复文档和容量门禁。
- 新增依赖必须有明确收益，使用现有 package manager，并同步锁文件。
- 注释只解释设计意图、业务约束、安全假设、边界或性能取舍，默认使用中文。

## 7. 服务端与领域规则

- 服务端是行动、战斗、市场和资产结算的唯一权威来源。
- 所有战斗、属性、收益率、计数与时长数值，计算和储存一律统一使用 `double` 类型（前端对应 `number` 即 double 精度），百分比类型内部也必须采用 `double` 点数。
- **服务端类型命名规约（现行聚合模型）**：
  - **繁多业务子类型采用帕斯卡前缀法（PascalCase Prefix）**：同族可枚举的业务子类型以业务名作为前缀聚合命名——玩家聚合分段族 `PlayerProgression` / `PlayerWorldState` / `PlayerCombatState` / `PlayerBuffState` / `PlayerLoadoutState` / `PlayerSettingsState` / `PlayerCombatSkillState` / `PlayerDuelState`，分段枚举 `PlayerSectionKind`（`Progression`/`World`/`Combat`/`Buffs`/`Loadout`/`Settings`/`ActionQueue`/`Duel`），载荷与存储记录 `PlayerStateHead` / `PlayerSectionSnapshot` / `StoredSection` / `StoredAction` / `SectionFlushItem` / `AutoUseSlotState` 等，便于 IDE 极速自动补全与文件树聚类。
  - **职责型核心类型保留自然后缀法（Natural Suffix）**：引擎与结算 `SimulationEngine` / `CombatSkillEngine`，选择与解算 `EncounterWaveSelector` / `PlayerAttributeResolver`，映射与编解码 `ProtocolSnapshotMapper` / `SectionCodec` / `MarketDtoMapper`，宿主单写者与运行时 `EcsPlayerRuntimeAdapter` / `MarketRuntime` / `WebSocketRuntime`，持久化适配 `Postgres*Repository`，后台任务 `*Worker` / `*HostedService`。
  - **已废弃命名不得复用**：`Task*` 任务数据类、`Executor*` 静态执行器属于历史约定，新增代码不得使用；`*Component`/`*System` 仅限定在 `src/Ecs`（游戏 ECS 层）与 YuoCore 内核中使用，禁止出现在 Domain/Server.Clean 等非 ECS 层。
- 确定性快进的在线、离线挂机和重连结果必须保持一致。
- **行动队列与无限循环不变量**：行动队列中 `RemainingRepeats == -1`（或负值代表无限重复）在任何 `SimulationEngine.AdvanceTo` 模拟推进与单轮结算完成后，**绝对不得自动出队**；只有正数次数递减至 0、玩家显式取消或资源前置条件断绝时方可移出队列。协议与前端选择器必须完整支持 `-1` 无限循环语义。
- **协议快照映射与配表透传**：`ProtocolSnapshotMapper` 转换快照与增量 DTO 时必须显式透传当前激活的 `GameDataCatalog`，以保证动作指纹与动态实体上下文完整一致，禁止脱离配表单独伪造协议载荷。
- 不得为性能优化改变战斗结果、掉落、市场撮合、队列完成边界或快照语义。
- 玩家并发所有权模型为「`PlayerAggregate` 聚合（命令内文档往返重建）+ YuoCore ECS（`EcsGameRuntime`/`EcsWorldShard`/`YuoFiber`）+ 玩家级 `SemaphoreSlim` 单写者」：生产宿主 `src/Server` 使用 `vendor/YuoCore`，同一玩家的命令与模拟推进在其哈希路由的 ECS 分片 Fiber 内串行执行，`EcsPlayerRuntimeAdapter` 的玩家级 gate 保证跨边界串行；跨边界访问不得直接持有聚合或 `YuoEntity` 引用。
- **ECS 业务开发唯一权威规约见 `docs/backend/ecs-development-guide.md`**：新增命令/行动/组件/系统/持久化字段必须先对照其中的通道与不变量清单（`MutatePlayer` 通道、脏标记差集、`CanBulkAdvance` 资格、`ShouldUpdate` 闸门、文档往返修订续写）。
- **特殊效果统一工作流**：新增、修改、修复或评审装备被动、技能附加效果、Buff/Debuff、怪物特性及其他玩法特殊效果时，必须使用仓库级 [special-effects Skill](.agents/skills/special-effects/SKILL.md)，对齐触发时序、轻量处理器接入、共享结算、状态生命周期与确定性验证；涉及效果数值时配合 `data-designer`。纯视觉特效不属于此 Skill。
- 玩家快照写回失败必须保留可恢复证据；不得为了降噪降低持久化错误等级。
- 健康端点不得泄露 token、连接串、玩家数据或内部异常。
- CORS 只允许配置的完整来源；禁止恢复任意来源加凭据的组合。
- **协议信封强对齐与往返门禁**：服务端在 `Protocol` 或 `Server` 增改 `WireMessageType`、信封结构或数据包协议时，必须在同一次提交中同步对齐前端 `frontend/src/` 中的枚举与解析逻辑，并补充端到端反序列化或信封编解码往返单测，严禁单边提交未对齐的信封类型引发客户端断连循环。

## 8. 前端规则

- 正式功能只修改 `frontend`。
- HTTP 和 WebSocket 默认使用 `window.location.origin` / 同源 `ws(s)`。
- 高频状态更新不得整体重建面板，必须保持输入焦点、选区和滚动位置。
- 不削弱 TypeScript 检查，不引入无必要的全局状态或网络重试层。
- API、WebSocket 和 UI 状态的错误必须可见且可恢复，不能无限静默重连。
- 桌面和移动端都必须避免文本溢出、控件重叠和动态内容引发布局跳动。
- **Store 行为落地红线**：禁止在状态管理器（Zustand、React Context 等）中声明仅有空函数体 `() => {}` 的虚空方法。任何对外暴露的操作方法必须具备实际的状态流转、数据变更或明确的报错抛出，防止伪实现静默吞没调用。
- **i18n 词条闭环**：UI 代码中每引入一个新的 `t('...')` 国际化键名，必须同步在 `frontend/public/locales/template.csv` 及主语言词典中定义；禁止残留未录入模板的裸露键名。
- **UI 文本简练红线**：界面文案只承载玩家可操作的信息（名称、状态、错误、必要后果与玩法指引），一律简短直白；禁止出现设计/实现层面的说明性文本——包括但不限于内部术语（预算、消耗点数、占位、待扩展、后续版本开放）、实现细节（服务端、缓存、哈希、持久化、CSV/文件路径、协议字段）、功能用途的赘述解释（如「退出登录用于切换账号」）与宣传式脚注。玩法机制、物品风味描述、数值公式 tooltip 属于游戏内容，不受此限，但同样应保持简练。
- **物品/装备/材料/消耗品/功法技能一律图标化展示**：凡展示物品、装备、材料、消耗品或功法技能条目，必须使用图标组件——整块瓦片用 `ItemTile`（含品质边框与悬浮详情），行内小图标用 `ItemIcon`，文本行/复合胶囊引用用 `ItemReference`；禁止仅用纯文本名称罗列。瓦片锚点类须按约定登记进 `frontend/src/styles/selection-grid.css` 的共享 `:is()` 组。
- **公共组件与统一模板强制收拢**：
  - 前端开发前，**必须先阅读前端公共组件文档 [`docs/frontend/common-components.md`](./docs/frontend/common-components.md)**，检索是否存在可复用的公共组件或统一模板（如 Hover 提示、复合 Label、境界徽章、模态弹窗、网格选择器等），禁止随意重新自制相似样式或重复实现。
  - 凡是将任何前端部分收拢走统一模板（如 hoverinfo、label、badge、picker 等），**必须同步登记写入 [`docs/frontend/common-components.md`](./docs/frontend/common-components.md)**。
  - 前端公共组件文档**不记录具体样式细节**，只作为**快速索引查找表**，仅记录适用场景（什么情况下使用）和指定源码/样式路径，具体用法与样式随用随查对应文件。

## 9. 测试与验证

- 领域或协议变更：运行对应 xUnit 测试，必要时补充确定性和边界测试。
- 服务端装配变更：至少运行 Release build、领域测试、`/healthz`、`/readyz` 和 WebSocket 烟测。
- 容量相关变更必须运行 `capacity-target` 门禁，覆盖 `5000` 账号存储估算、`500` 峰值命令包体和 `2000` 冷挂机一小时补算。
- 前端变更：至少运行 `npm --prefix frontend run build` 与 `node --test "frontend/src/**/*.test.mjs"`；交互变化还需浏览器验证。
- Docker 变更：必须实际构建镜像，并以只读根文件系统、持久卷和非 root 用户启动验证。
- 部署变更：必须分别验证 Git 状态、CCR 镜像摘要、远端运行镜像 ID、容器健康和公网入口。
- 优先运行最小必要验证，不默认执行长时间全量压测。
- **测试必须最小充分且非重复**：只为关键业务不变量、真实高风险边界、跨模块契约、幂等/并发/持久化失败和用户可见回归编写测试。不得为了覆盖率把每个 getter、转发方法、简单分支、私有实现细节或同一语义的排列组合都写成测试。
- 单元测试、集成测试和 smoke 必须各自覆盖不同故障面；禁止用多层重复断言验证同一个实现细节。新增测试前必须能说明“它防止哪一种独立回归”；无法说明时不新增。
- smoke 只验证真实跨边界入口、健康状态和关键用户流程，不把所有领域逻辑搬进 smoke；领域细节应由少量高价值确定性测试覆盖。
- 未实际运行的测试不得声称通过；无法运行时必须说明原因和建议命令。
- 禁止删除、跳过或放宽测试来掩盖问题。

## 10. Docker 与镜像规则

- 生产镜像必须使用多阶段构建，不携带源码、Node.js、SDK、测试产物或本地数据。
- 容器必须以固定非 root UID/GID 运行，根文件系统只读，仅 `/tmp` 可写。
- 镜像必须包含 OCI `created`、`version`、`revision` 标签，并通过 `/healthz` 暴露非敏感版本。
- 构建上下文不得包含 `.git`、secret、快照、outbox、`node_modules`、`bin` 或 `obj`。
- 镜像验收必须确认镜像内不包含运行数据，防止本地持久化内容进入镜像层。
- 正式镜像只构建 `linux/amd64`，除非目标服务器架构发生变化并已验证。
- 不使用 `latest` 作为本项目生产移动标签；约定使用 `prod`，同时保留 `sha-*` 或明确版本的不可变标签。
- 禁止把 token、CCR 密码、SSH key、证书或业务数据写入 Dockerfile、ARG、ENV、label 或镜像层。

## 11. CCR 发布规则

正式发布命令：

```bash
scripts/docker-build-prod.sh --push
```

- 默认仓库为 `ccr.ccs.tencentyun.com/yuohira/daohua`。
- 正式发布前工作区必须已提交且验证通过；脚本默认拒绝 dirty worktree。
- `--allow-dirty` 只允许首次引导和明确排障，不得作为日常发布路径。
- `--push` 会同时推送不可变标签和 `prod`；未传入时只能本地构建。
- 已完成容器运行验收时使用 `--push-existing`；脚本必须确认不可变标签与 `prod` 的本地镜像 ID 一致，避免重建出未经烟测的新产物。
- CCR 推送、服务器部署和 Caddy 修改是三个独立状态，汇报时必须分别给出证据。
- 不得在日志、文档或回复中输出 Docker auth 内容。
- 安装服务器 CCR 登录态必须再次获得用户明确许可；只允许使用 `scripts/install-ccr-auth.sh` 的 `docker login --password-stdin` 路径，不得复制或打印整份本机 Docker 配置。

## 12. 生产部署与更新规则

- 默认服务器 SSH 别名为 `yuohira`，域名为 `daohua.yuohira.com`。
- 生产目录固定为 `/opt/daohua`，Compose 项目名固定为 `daohua`。
- 应用端口只绑定 `127.0.0.1:15128`，禁止直接暴露到公网。
- 首次部署和受控更新使用 `scripts/deploy-prod.sh --image <不可变镜像>`。
- `daohua-ccr-auto-update.timer` 每分钟检查 `prod`；镜像 ID 未变化时不得重建容器。
- 新镜像未通过健康检查时必须自动回滚，并记录失败镜像 ID；同一失败版本不得循环重试。
- 更新不得删除、重建或改名 `daohua_daohua-postgres` 卷。
- 当前进程内在线玩家由 ECS 适配器按玩家串行处理，数据库使用市场分区租约及 fence；`EcsGameRuntime` 分片数默认 1（构造参数 shardCount）；跨进程玩家分片所有权和红绿无停机仍未完成，正常更新依靠 WebSocket 自动重连、PostgreSQL outbox 和单实例 app 优雅替换恢复。
- 回滚前先停用自动更新 timer，使用已验证的不可变镜像，验证完成后再决定是否恢复 timer。

## 13. Caddy 规则

- 站点配置独立放在 `/etc/caddy/sites/daohua.caddy`，主 Caddyfile 只增加一次 import。
- 修改前必须备份现有主配置和站点配置。
- 必须先执行 `caddy validate`，成功后使用 `systemctl reload caddy`，不得无验证重启。
- 校验或 reload 失败必须自动恢复原配置。
- Caddy 必须支持 WebSocket、HTTPS、压缩、安全响应头，并拦截 `/api/debug/*` 和 `/api/metrics`。
- Caddy 必须对隐藏文件、凭据文件、数据库归档和常见框架探测路径直接返回 404，不得交给 SPA fallback。
- 不得修改或中断 `daojie.yuohira.com`、`frp.yuohira.com`、`food.yuohira.com`、`game.yuohira.com` 等无关站点。

## 14. 数据、备份与安全

- 生产数据操作前先确认精确卷名、容器状态、PostgreSQL 健康和备份路径。
- 删除、恢复、覆盖生产数据必须获得用户单独明确许可；恢复前应先新建当前状态备份并验证目标归档。
- 自动备份使用远端 `/opt/daohua/backup.sh`：每小时生成 PostgreSQL custom dump 压缩归档与 SHA-256 校验文件；支持环境变量控制留存，主服配合本地计算节点异地备份拉取（`deploy/backup-pull-sync.sh`）收窄热备窗口（hourly 保留 6 份、daily 保留 3 份、base 保留 1 份），由本地计算节点承担全量长期灾备存储与 SHA-256 自动对账。
- 不读取或输出无关 secret；不得提交 `.env`、token、cookie、私钥、连接串或 Docker auth。
- 外部输入必须校验；错误响应不得包含堆栈、路径、凭据或内部存储内容。

## 15. Git 规则

- **自动提交范围与限制**：
  - **功能与代码修改自动提交**：每次完成完整代码功能修改/添加，或解决一组 Bug 并通过完整门禁验证后，**必须自动执行 Git 提交**，无需等待用户额外发出提交指令。
  - **纯文档修改严禁自动提交**：凡是纯文档类的修改（如 `docs/` 目录下的架构设计、开发指南、任务面板、注释或说明文档调整），**不得自动执行 Git 提交**，必须保持在工作区，等待用户明确指令或在后续完成对应功能代码实现后一并合并提交。
- `commit` 不等于 `push`；push、force push、reset、rebase、amend、tag、release 和 deploy 仍需用户明确许可。
- commit 前只暂存本次任务直接相关文件，先运行对应验证（如 `scripts/verify.sh` 或相应单测），核对 `git diff --cached` 确保无无关改动后提交。
- 提交说明必须使用规范的中文 Conventional Commits，例如：

```text
feat: 完善 CCR 生产部署流程
fix: 修复生产环境同源 WebSocket 地址
docs: 补充数据备份和回滚说明
```

- submodule 更新必须单独说明旧、新 commit；不得把 submodule 内 dirty 状态带入主仓库提交。

## 16. 文档同步

修改以下内容时必须同步更新 `README.md` 或 `docs/ops/deployment.md`：

- 开发命令、依赖版本、端口或目录。
- Docker 构建、镜像名、标签或平台。
- 环境变量、健康端点、Caddy、CCR、部署和回滚方式。
- 数据卷、备份、恢复、存档格式或运行拓扑。
- 对外 API、WebSocket 协议和生产安全边界。
- 前端公共组件、统一模板或基础交互规范变更（同步更新 `docs/frontend/common-components.md`）。
- 非 Bug 修复开发任务规划与进度（必须同步更新 `docs/tasks.md` 及 `docs/tasks/*.md` 独立详情文档）。

对应实现发生修改时，其代码注释必须同步更新，保持注释与真实行为一致，禁止注释与实现分叉。

## 17. 任务面板与需求追踪规则

- **非 Bug 修复任务强制登记与详情分档**：
  - 每次用户发起任何非 Bug 修复性质的任务（新功能、架构重构、数值设计、玩法拓展、规则制定、界面更新等），必须在启动开发前深入分析用户的提示词与核心需求，并在回复中首先复述需求并阐述理解。
  - **详情文件独立存放**：每个任务的实现方案、详细设计、子任务分解、时间节点与进度日志必须单独创建存放在 `docs/tasks/TASK-YYYYMMDD-XX-任务简称.md`（命名格式：「`TASK-` + 年月日 + `-` + 当日两位序号 + `-` + 任务简要中文名」，如 `TASK-20261011-07-任务面板归档与命名规约.md`；任务编号沿用 `TASK-YYYYMMDD-XX`，文件名简称为检索辅助），**严禁在主任务看板 `docs/tasks.md` 中堆砌冗长的实现细节或过程日志**，避免污染主文档。
  - **主看板轻量索引**：主文档 `docs/tasks.md` 仅作为任务总览看板、状态流转总表与路线图，仅保留任务简表与跳转至 `docs/tasks/TASK-*.md` 的链接。
- **开发中持续更新**：在开发执行过程中（每完成阶段性目标、遇到重大分支决策或完成功能验证），必须持续同步更新对应 `docs/tasks/TASK-*.md` 独立文档中的子任务状态、进度条与进度日志，并同步更新 `docs/tasks.md` 主看板的状态。
- **时刻记录时间**：任务提出时间、需求分析完成时间、各阶段开发进度节点时间以及最终完成时间均需精确记录（格式：`YYYY-MM-DD HH:mm:ss`）。
- **任务完成与归档**：任务在通过所有验证并自动提交 Git 后，必须在对应 `docs/tasks/TASK-*.md` 及主看板 `docs/tasks.md` 中将状态更新为 `已完成`，记录完成时间并移入已完成任务归档表格。

