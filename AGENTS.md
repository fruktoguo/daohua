# AGENTS.md - 道化太初开发与发布规则

本文件适用于仓库根目录及全部子目录。用户当次明确要求优先于本文件；存在冲突时先说明冲突和风险，再等待用户裁定。

## 1. 语言与沟通

- 所有交流、计划、总结、注释、文档和 Git 提交说明使用中文。
- 文件名、命令、类型名、变量名、函数名、协议字段、框架和库名可保留英文。
- 不输出冗长思维链，只说明关键依据、结论和必要取舍。
- 生产问题先回答：是否仍在发生、是否有数据风险、现在是否需要处理。

## 2. 项目权威边界

- `src/Domain`：纯领域规则、确定性模拟、战斗、地图和市场模型；不得依赖 ASP.NET Core、Docker、数据库或浏览器实现。
- `src/Protocol`：服务端、浏览器和压测共享的纯数据协议；不得暴露领域聚合、运行时所有权对象或存储实现对象。
- `src/Server`：ASP.NET Core 宿主、WebSocket、运行时装配和持久化适配。
- `src/ComputeNode`：可选高延迟冷挂机计算节点；只执行确定性补算，不持有玩家权威状态，不直接访问生产数据库。
- `src/LoadTests`：负载场景，不是生产运行时依赖。
- `src/DataTool`：游戏数据 JSON 校验与指纹工具；独立命令行项目，无项目引用。
- `frontend`：唯一正式 React/Vite 前端。
- `src/Ecs`：游戏 ECS 层（`EcsGameRuntime`/`EcsWorldShard`/`EcsActionPipeline`、业务组件与行动/战斗系统），生产运行时的游戏状态与调度实现。
- `tests/Ecs.Tests`：ECS 运行时与适配器边界回归门禁（`EcsGameRuntime`/`EcsWorldShard`/`EcsPlayerRuntimeAdapter`）。
- `tests/Domain.Tests`：领域和协议回归门禁。
- `tests/Server.Tests`：服务端装配、持久化与跨边界协议回归门禁。
- `tools/`：本地开发辅助工具（`config-editor` 独立 Vite 应用、`combat-simulator.html`），不进入生产镜像。
- `art/`：美术与图像资源源文件及图标清单。
- `deploy/`：生产部署资产（`compose.prod.yaml`、systemd 单元、备份/归档/回滚脚本、Caddy 站点配置、PG 角色 SQL）。
- `vendor/YuoCore`：固定版本的 Git submodule（上游的 `ecs-optimized` 分支），生产运行时内核。除非任务明确涉及 YuoCore，不得在本仓库任务中修改 submodule 内容。**在 submodule 内工作时，必须遵循其仓库内 `AGENTS.md` → `docs/ai-development-guide.md` → `docs/yuocore-ecs-kernel-rules.md` 的规范链**（ECS 内核的稠密索引纪律、生命周期语义红线、观测零成本与消融归因门禁以该链为准）；submodule 内的提交在其自身分支完成且不推送，本仓库只更新 submodule 指针与 `scripts/bench-service.sh` 的 `CORE_SHA` 钉。
- `data`：运行数据，不进入镜像，不提交 Git，不随代码发布覆盖。

依赖方向必须保持（以各 `.csproj` 实际 `ProjectReference` 为准）：

```text
Server -> src/Ecs + Server.Clean + Domain + Protocol + YuoCore(Abstractions/Ecs/Runtime) + Npgsql
src/Ecs -> Domain + YuoCore(Abstractions/Ecs/Runtime)（游戏 ECS 组件/系统/分片/管线）
Domain -> YuoCore(Abstractions/Ecs)（纯领域原语引用，不持有宿主所有权对象）
Domain.Tests -> Domain + Protocol
Server.Tests -> Server + Domain + Protocol + ComputeNode
Ecs.Tests -> Server + src/Ecs + Server.Clean + Domain + Protocol
LoadTests -> Protocol + Npgsql
ComputeNode -> Domain（仅纯领域程序集：确定性补算与 SectionCodec 单源复用，禁止任何宿主/存储引用）
DataTool -> 无项目引用（独立可执行进程/工具）
```

单内核直引：全仓只存在 `vendor/YuoCore` 一个内核根，所有 `ProjectReference` 为普通引用，不得重新引入 `YuoCoreRoot`/`EcsBuildVariant`/`AdditionalProperties` 等变体属性。同名程序集双变体机制已随基线 submodule 退役。

Domain 不依赖 Protocol；禁止让 Domain 反向依赖 Server、前端或部署层。跨层需求优先放入现有适配边界，不新增循环依赖。

## 3. 当前生产架构与容量目标

- 项目内部代表名统一使用 `daohua` / `DaoHua` / `DAOHUA`；不得新增历史旧名的任何大小写形式作为命名、卷名、环境变量或服务名。
- 当前内测阶段允许直接破坏历史数据、存档格式和部署拓扑兼容性；为了达成容量与落盘目标，可以直接改表、改快照和清空内测数据，但生产/远端真实数据操作仍必须先备份并获得明确许可。
- **本地开发阶段兼容清零（硬规则）**：当前工作默认处于本地开发/内测阶段，不设计、不实现任何历史兼容路径。旧数据、旧快照、旧数据库 schema、旧 API/WebSocket 字段、旧协议、旧部署拓扑和旧组件模型均可直接废弃；禁止为了它们新增 migration runner、双读双写、兼容 DTO、legacy flag、fallback adapter、旧字段回填或过渡分支。允许一次性改表、改快照、删旧模型和清空本地数据。所谓“行为基线”只用于保护当前明确的业务结算语义，不等于保留历史格式或历史调用方式。
- **兼容性迁移目录（唯一例外）**：数据库兼容性迁移脚本允许存在，但必须统一收拢在仓库根目录 `migrations/` 独立文件夹中（纯 SQL/脚本文件，命名 `NNNN_vX_to_vY.sql`），仅供既有库就地升级时由人工或运维流程显式执行。该目录必须满足**可整体删除**约束：不参与任何项目编译、不被运行时装配或启动门禁引用、不进入镜像构建上下文；空库启动永远由权威 `PostgresSchema.Sql` 直建，删除 `migrations/` 后全新初始化行为完全不变。仍禁止在代码中实现 migration runner 或对迁移目录的运行时依赖。
- 本地开发可以直接破坏代码和数据结构；只有实际操作远程/生产数据时才执行备份、审批和可恢复性检查，这属于数据安全要求，不得反向变成代码兼容层。
- 目标服务器按 `2c4g/40g` 预算设计：至少支持 `5000+` 注册账号存储、`100+` 持续网络收发、`500+` 高峰网络收发、`2000+` 偶尔上线/离线挂机玩家补算。
- 主存储固定为 PostgreSQL：账号、玩家快照、durable outbox、市场账本和市场流水均落库；应用容器不挂载数据卷。
- 市场账本使用 PostgreSQL Serializable 事务、行锁及带 fence 的分区租约；`Market:PartitionCount` / `Market:PartitionId` 可保留分布式分片边界，但同一分片只能一个写者持有。
- 远程个人设备只能作为高延迟冷挂机计算节点，经 compute-link WebSocket **反向拨入**主服 `/internal/compute-link`（节点多为无公网 NAT 设备，只拨出不监听；节点本地 HTTP 口仅作调试）；节点不得承载在线连接、市场写入、账号鉴权或数据库写权限。
- 正式前端、HTTP API 和 WebSocket 使用同一来源；生产代码不得硬编码开发端口。
- 当前服务端不存在任何 `/api/debug/*` 或 `/api/metrics` 端点；任何 debug/metrics 端点只允许在 `Development` 环境存在，生产 Caddy 必须显式拦截对应路径（拦截规则已配置）。
- 账号鉴权已落地：`/api/v1/auth/register|login|logout|session`，PBKDF2 密码哈希 + HttpOnly Cookie 会话 + 认证限流；客户端不再仅凭本地 UUID 标识玩家。该实现为内测口径——涉及充值、真实资产或开放注册前，必须先完成服务端身份认证、授权与审计的强化评审。
- 防多开子系统独立收拢：服务端 `src/Server/Clean/AntiMultiOpen/`（标签互斥开关下发 / 单账号互踢 / 设备指纹配额，关闭码 4005-4008），前端 `frontend/src/antiMulti/`；设计真源见 `docs/backend/anti-multi-open.md`，准入裁决在 Hello 握手时统一执行。

## 4. 修改前检查

每次修改前必须：

0. **文档先行**：`docs/design/总纲/` 为设计真源。若实现与设计文档存在偏差，必须先修订文档对齐设计意图，再修改实际逻辑；禁止让代码与文档长期分叉。新设计同样先落文档再落地实现。
1. 阅读相关代码、配置和调用点。
2. 执行 `git status --short`，区分用户改动和本次改动。
3. 执行 `git submodule status`，确认 YuoCore 没有缺失或漂移。
4. 非简单任务先给出简短中文计划。
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

最小完整验证：

```bash
scripts/verify.sh
```

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
| `verify.sh` | 最小完整门禁：工具链、Release 构建、领域/服务端测试、DataTool 校验、Compose 配置、本地 PostgreSQL 容量验证 |
| `dev.sh` | 一键启动本地前后端联调（数据库自检、端口检查、后端热重启、前端 Vite） |
| `dev-hot.mjs` | C# 源码监听与后端平滑热重启（由 `dev.sh` 调用） |
| `dev-service.sh` | 本地容器化同构环境管理：`up`/`restart`/`down`/`status`/`logs`/`smoke` |
| `config-editor.sh` | 启动 `tools/config-editor` 可视化配表编辑器（127.0.0.1:5199） |
| `capacity-target.sh` | 真实 PostgreSQL/WAL/latest-only Worker 容量门禁 |
| `smoke-production.mjs` | 生产/容器烟测：健康、就绪、同源静态入口与 WebSocket 首帧 |
| `check-frontend-contracts.mjs` | 前后端协议/契约一致性检查（被 `tests/Server.Tests` 调用） |
| `export-i18n-csv.mjs` | 导出前端 i18n 词条模板 CSV |
| `optimize-svg.mjs` | SVG 美术资源优化 |
| `docker-build-prod.sh` | 生产镜像多阶段构建与 CCR 推送（`--push`/`--push-existing`） |
| `deploy-prod.sh` | 生产服务器受控更新（`--image <不可变镜像>`） |
| `install-ccr-auth.sh` | 服务器 CCR 登录态安装（`docker login --password-stdin`） |
| `init-local-db-roles.sh` | 本地 PostgreSQL 角色初始化（对齐 `deploy/postgres-local-roles.sql`） |
| `migrate-local-db.sh` | 本地库显式兼容性迁移（按 `platform.schema_version` 链式执行 `migrations/` 落后脚本；`--dry-run` 空跑）。仅供人工调用，运行时与门禁均不引用 |

## 6. 实现规则

- **多 Agent 并行开发优先**：开发阶段凡遇到可拆解、模块相对独立、并发无写冲突的任务（如前后端分离、领域/宿主/测试分工、多配置/多组件并行编写等），如果可以，**优先使用多 Agent（Subagents）并行并发开发**，最大化交付吞吐并隔离上下文；各 Agent 完成后统一进行全局门禁验证。
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
- **物品/装备/材料/消耗品/功法技能一律图标化展示**：凡展示物品、装备、材料、消耗品或功法技能条目，必须使用图标组件——整块瓦片用 `ItemTile`/`ItemTileReference`（含品质边框与悬浮详情），行内小图标用 `ItemIcon`，文本行引用用 `ItemReference`；禁止仅用纯文本名称罗列。瓦片锚点类须按约定登记进 `frontend/src/styles/selection-grid.css` 的共享 `:is()` 组。
- **公共组件与统一模板强制收拢**：
  - 前端开发前，**必须先阅读前端公共组件文档 [`docs/frontend/common-components.md`](./docs/frontend/common-components.md)**，检索是否存在可复用的公共组件或统一模板（如 Hover 提示、复合 Label、境界徽章、模态弹窗、网格选择器等），禁止随意重新自制相似样式或重复实现。
  - 凡是将任何前端部分收拢走统一模板（如 hoverinfo、label、badge、picker 等），**必须同步登记写入 [`docs/frontend/common-components.md`](./docs/frontend/common-components.md)**。
  - 前端公共组件文档**不记录具体样式细节**，只作为**快速索引查找表**，仅记录适用场景（什么情况下使用）和指定源码/样式路径，具体用法与样式随用随查对应文件。

## 9. 测试与验证

- 领域或协议变更：运行对应 xUnit 测试，必要时补充确定性和边界测试。
- 服务端装配变更：至少运行 Release build、领域测试、`/healthz`、`/readyz` 和 WebSocket 烟测。
- 容量相关变更必须运行 `capacity-target` 门禁，覆盖 `5000` 账号存储估算、`500` 峰值命令包体和 `2000` 冷挂机一小时补算。
- 前端变更：至少运行 `npm --prefix frontend run build`；交互变化还需浏览器验证。
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
- 自动备份使用远端 `/opt/daohua/backup.sh`：每小时生成 PostgreSQL custom dump 压缩归档，小时保留 24 份、每日 00:00 保留 7 份、每周一 00:00 保留 4 份，并生成 SHA-256 校验文件。
- 删除、恢复、覆盖生产数据必须获得用户单独明确许可；恢复前应先新建当前状态备份并验证目标归档。
- 只有实际操作远程/生产数据时，数据库 migration、存档变更、密钥轮换和批量玩家数据修复才必须先提供备份、可恢复性和回滚方案；这是运维安全边界，不要求本地代码实现历史兼容。当前本地开发可以直接替换 schema、快照和存档格式。
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

## 17. 任务面板与需求追踪规则

- **非 Bug 修复任务强制登记与详情分档**：
  - 每次用户发起任何非 Bug 修复性质的任务（新功能、架构重构、数值设计、玩法拓展、规则制定、界面更新等），必须在启动开发前深入分析用户的提示词与核心需求。
  - **详情文件独立存放**：每个任务的实现方案、详细设计、子任务分解、时间节点与进度日志必须单独创建存放在 `docs/tasks/TASK-YYYYMMDD-XX.md`，**严禁在主任务看板 `docs/tasks.md` 中堆砌冗长的实现细节或过程日志**，避免污染主文档。
  - **主看板轻量索引**：主文档 `docs/tasks.md` 仅作为任务总览看板、状态流转总表与路线图，仅保留任务简表与跳转至 `docs/tasks/TASK-*.md` 的链接。
- **开发中持续更新**：在开发执行过程中（每完成阶段性目标、遇到重大分支决策或完成功能验证），必须持续同步更新对应 `docs/tasks/TASK-*.md` 独立文档中的子任务状态、进度条与进度日志，并同步更新 `docs/tasks.md` 主看板的状态。
- **时刻记录时间**：任务提出时间、需求分析完成时间、各阶段开发进度节点时间以及最终完成时间均需精确记录（格式：`YYYY-MM-DD HH:mm:ss`）。
- **任务完成与归档**：任务在通过所有验证并自动提交 Git 后，必须在对应 `docs/tasks/TASK-*.md` 及主看板 `docs/tasks.md` 中将状态更新为 `已完成`，记录完成时间并移入已完成任务归档表格。

## 18. 完成标准

任务只有同时满足以下条件才能报告完成：

1. 实现与现有架构一致，无临时绕过。
2. 相关构建和测试实际通过。
3. 镜像可构建、以非 root 启动且健康。
4. 如已发布，CCR 不可变标签和 `prod` 指向同一内容摘要。
5. 如已部署，远端镜像 ID、健康端点、HTTPS 和 WebSocket 都已验证。
6. 持久卷存在且未被替换，自动更新 timer 状态正常。
7. 文档已同步，`docs/tasks.md` 任务状态与进度已更新并闭环，Git 状态准确，无 secret 或无关文件。

## 19. 最终回复格式

任务完成后用中文简洁回复：

```text
修改摘要：
- ...

修改文件：
- ...

验证结果：
- ...

Git 状态：
- ...

风险 / 后续：
- ...
```

没有风险或后续事项时可以省略最后一项。
