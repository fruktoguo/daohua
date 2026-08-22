# AGENTS.md - 道化太初开发与发布规则

本文件适用于仓库根目录及全部子目录。用户当次明确要求优先于本文件；存在冲突时先说明冲突和风险，再等待用户裁定。

## 1. 语言与沟通

- 所有交流、计划、总结、注释、文档和 Git 提交说明使用中文。
- 文件名、命令、类型名、变量名、函数名、协议字段、框架和库名可保留英文。
- 不输出冗长思维链，只说明关键依据、结论和必要取舍。
- 生产问题先回答：是否仍在发生、是否有数据风险、现在是否需要处理。

## 2. 项目权威边界

- `src/Daoxuan.Domain`：纯领域规则、确定性模拟、战斗、地图和市场模型；不得依赖 ASP.NET Core、Docker、数据库或浏览器实现。
- `src/Daoxuan.Protocol`：服务端、浏览器和压测共享的纯数据协议；不得暴露 ECS、Fiber、Actor 或存储实现对象。
- `src/Daoxuan.Server`：ASP.NET Core 宿主、WebSocket、运行时装配和持久化适配。
- `src/Daoxuan.ComputeNode`：可选高延迟冷挂机计算节点；只执行确定性补算，不持有玩家权威状态，不直接访问生产数据库。
- `src/Daoxuan.LoadTests`：负载场景，不是生产运行时依赖。
- `frontend`：唯一正式 React/Vite 前端。
- `frontend-sim`：历史模拟与参考实现，不得作为正式入口或生产镜像来源。
- `tests/Daoxuan.Domain.Tests`：领域和协议回归门禁。
- `vendor/YuoCore`：固定版本的 Git submodule。除非任务明确涉及 YuoCore，不得在本仓库任务中修改 submodule 内容。
- `data`：运行数据，不进入镜像，不提交 Git，不随代码发布覆盖。

依赖方向必须保持：

```text
Daoxuan.Protocol <- Daoxuan.Domain <- Daoxuan.Server
                                 <- Daoxuan.LoadTests
                                 <- Daoxuan.ComputeNode
Daoxuan.Domain -> YuoCore.Abstractions + YuoCore.Ecs
Daoxuan.Server -> YuoCore.Persistence + YuoCore.Runtime + PostgreSQL/Npgsql
```

禁止让 Domain 反向依赖 Server、前端或部署层。跨层需求优先放入现有适配边界，不新增循环依赖。

## 3. 当前生产架构与容量目标

- 项目内部代表名统一使用 `daoxuan` / `Daoxuan` / `DAOXUAN`；不得新增历史旧名的任何大小写形式作为命名、卷名、环境变量或服务名。
- 当前内测阶段允许破坏历史数据、存档格式和部署拓扑兼容；为了达成容量与落盘目标，可以直接改表、改快照和清空内测数据，但生产/远端真实数据操作仍必须先备份并获得明确许可。
- 目标服务器按 `2c4g/40g` 预算设计：至少支持 `5000+` 注册账号存储、`100+` 持续网络收发、`500+` 高峰网络收发、`2000+` 偶尔上线/离线挂机玩家补算。
- 生产主存储为 PostgreSQL：账号、玩家快照、durable outbox、市场账本和市场流水均落库；`/app/data` 仅保留容器本地 outbox 兼容目录和临时运行数据，不再作为玩家/市场权威存储。
- 市场账本使用 PostgreSQL revision CAS 与 advisory lock；`Market:PartitionCount` / `Market:PartitionId` 可保留分布式分片边界，但同一分片只能一个写者持有。
- 远程个人设备只能作为高延迟冷挂机计算节点，通过 `Daoxuan.ComputeNode` 的内部 HTTP 批处理接口被主服务调用；节点不得承载在线连接、市场写入、账号鉴权或数据库写权限。
- 正式前端、HTTP API 和 WebSocket 使用同一来源；生产代码不得硬编码开发端口。
- `/api/debug/*` 只能在 `Development` 环境存在；生产 Caddy 还必须显式拦截 debug 和 metrics 路径。
- 当前客户端使用本地 UUID 标识玩家，尚未形成商业账号鉴权。涉及充值、真实资产或开放注册前，必须先完成服务端身份认证、授权和审计。

## 4. 修改前检查

每次修改前必须：

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
dotnet run --project src/Daoxuan.Server/Daoxuan.Server.csproj
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

生产同构容器验证：

```bash
docker compose up -d --build
node scripts/smoke-production.mjs http://127.0.0.1:5128
docker compose down
```

`docker compose down` 不得附带 `--volumes`，除非用户明确要求删除本地数据。

## 6. 实现规则

- 只修改当前任务直接相关文件，保持 diff 精简、集中、可审查。
- 优先复用现有类型、服务、组件、目录和协议，不复制同类实现。
- 修改函数签名、协议或数据结构时同步更新全部调用点和测试。
- 不用特殊 case 掩盖根因，不留下 debug 输出、临时代码、无说明 TODO 或无用文件。
- 错误处理必须明确；不得静默吞掉持久化、结算、鉴权和部署异常。
- 不随意修改公共 API、WebSocket 包结构、事件名、配置键和存档格式。
- 内测阶段可破坏旧数据兼容；若变更数据格式或部署拓扑，必须同步更新 PostgreSQL schema、备份/恢复文档和容量门禁。
- 新增依赖必须有明确收益，使用现有 package manager，并同步锁文件。
- 注释只解释设计意图、业务约束、安全假设、边界或性能取舍，默认使用中文。

## 7. 服务端与领域规则

- 服务端是行动、战斗、市场和资产结算的唯一权威来源。
- 所有战斗、属性、收益率、计数与时长数值，计算和储存一律统一使用 `double` 类型（前端对应 `number` 即 double 精度），百分比类型内部也必须采用 `double` 点数。
- 确定性快进的在线、离线挂机和重连结果必须保持一致。
- 不得为性能优化改变战斗结果、掉落、市场撮合、队列完成边界或快照语义。
- ECS/Fiber 所有权必须遵守 YuoCore 约束；跨 Fiber/进程访问不得直接持有 Entity。
- 玩家快照写回失败必须保留可恢复证据；不得为了降噪降低持久化错误等级。
- 健康端点不得泄露 token、连接串、玩家数据或内部异常。
- CORS 只允许配置的完整来源；禁止恢复任意来源加凭据的组合。

## 8. 前端规则

- 正式功能只修改 `frontend`，除非任务明确要求维护 `frontend-sim`。
- HTTP 和 WebSocket 默认使用 `window.location.origin` / 同源 `ws(s)`。
- 高频状态更新不得整体重建面板，必须保持输入焦点、选区和滚动位置。
- 不削弱 TypeScript 检查，不引入无必要的全局状态或网络重试层。
- API、WebSocket 和 UI 状态的错误必须可见且可恢复，不能无限静默重连。
- 桌面和移动端都必须避免文本溢出、控件重叠和动态内容引发布局跳动。
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
- 未实际运行的测试不得声称通过；无法运行时必须说明原因和建议命令。
- 禁止删除、跳过或放宽测试来掩盖问题。

## 10. Docker 与镜像规则

- 生产镜像必须使用多阶段构建，不携带源码、Node.js、SDK、测试产物或本地数据。
- 容器必须以固定非 root UID/GID 运行，根文件系统只读，仅 `/app/data` 和 `/tmp` 可写。
- 镜像必须包含 OCI `created`、`version`、`revision` 标签，并通过 `/healthz` 暴露非敏感版本。
- 构建上下文不得包含 `.git`、secret、快照、outbox、`node_modules`、`bin` 或 `obj`。
- 镜像验收必须确认镜像内 `/app/data` 初始文件数为 0，防止被 Git 忽略的本地快照进入镜像层。
- 正式镜像只构建 `linux/amd64`，除非目标服务器架构发生变化并已验证。
- 不使用 `latest` 作为本项目生产移动标签；约定使用 `prod`，同时保留 `sha-*` 或明确版本的不可变标签。
- 禁止把 token、CCR 密码、SSH key、证书或业务数据写入 Dockerfile、ARG、ENV、label 或镜像层。

## 11. CCR 发布规则

正式发布命令：

```bash
scripts/docker-build-prod.sh --push
```

- 默认仓库为 `ccr.ccs.tencentyun.com/yuohira/daoxuan`。
- 正式发布前工作区必须已提交且验证通过；脚本默认拒绝 dirty worktree。
- `--allow-dirty` 只允许首次引导和明确排障，不得作为日常发布路径。
- `--push` 会同时推送不可变标签和 `prod`；未传入时只能本地构建。
- 已完成容器运行验收时使用 `--push-existing`；脚本必须确认不可变标签与 `prod` 的本地镜像 ID 一致，避免重建出未经烟测的新产物。
- CCR 推送、服务器部署和 Caddy 修改是三个独立状态，汇报时必须分别给出证据。
- 不得在日志、文档或回复中输出 Docker auth 内容。
- 安装服务器 CCR 登录态必须再次获得用户明确许可；只允许使用 `scripts/install-ccr-auth.sh` 的 `docker login --password-stdin` 路径，不得复制或打印整份本机 Docker 配置。

## 12. 生产部署与更新规则

- 默认服务器 SSH 别名为 `yuohira`，域名为 `xx.yuohira.com`。
- 生产目录固定为 `/opt/daoxuan`，Compose 项目名固定为 `daoxuan`。
- 应用端口只绑定 `127.0.0.1:15128`，禁止直接暴露到公网。
- 首次部署和受控更新使用 `scripts/deploy-prod.sh --image <不可变镜像>`。
- `daoxuan-ccr-auto-update.timer` 每分钟检查 `prod`；镜像 ID 未变化时不得重建容器。
- 新镜像未通过健康检查时必须自动回滚，并记录失败镜像 ID；同一失败版本不得循环重试。
- 更新不得删除、重建或改名 `daoxuan_daoxuan-data` 与 `daoxuan_daoxuan-postgres` 卷。
- 当前数据库已支持市场主分片分布式租约，但在线玩家分片和红绿无停机仍未完成；正常更新依靠 WebSocket 自动重连、PostgreSQL outbox 和单实例 app 优雅替换恢复。
- 回滚前先停用自动更新 timer，使用已验证的不可变镜像，验证完成后再决定是否恢复 timer。

## 13. Caddy 规则

- 站点配置独立放在 `/etc/caddy/sites/daoxuan.caddy`，主 Caddyfile 只增加一次 import。
- 修改前必须备份现有主配置和站点配置。
- 必须先执行 `caddy validate`，成功后使用 `systemctl reload caddy`，不得无验证重启。
- 校验或 reload 失败必须自动恢复原配置。
- Caddy 必须支持 WebSocket、HTTPS、压缩、安全响应头，并拦截 `/api/debug/*` 和 `/api/metrics`。
- Caddy 必须对隐藏文件、凭据文件、数据库归档和常见框架探测路径直接返回 404，不得交给 SPA fallback。
- 不得修改或中断 `daojie.yuohira.com`、`frp.yuohira.com`、`food.yuohira.com`、`game.yuohira.com` 等无关站点。

## 14. 数据、备份与安全

- 生产数据操作前先确认精确卷名、容器状态、PostgreSQL 健康和备份路径。
- 自动备份使用远端 `/opt/daoxuan/backup.sh`：每小时生成 PostgreSQL custom dump 压缩归档，小时保留 24 份、每日 00:00 保留 7 份、每周一 00:00 保留 4 份，并生成 SHA-256 校验文件。
- 删除、恢复、覆盖生产数据必须获得用户单独明确许可；恢复前应先新建当前状态备份并验证目标归档。
- 数据库 migration、存档迁移、密钥轮换和批量玩家数据修复必须先提供备份、兼容和回滚方案。
- 不读取或输出无关 secret；不得提交 `.env`、token、cookie、私钥、连接串或 Docker auth。
- 外部输入必须校验；错误响应不得包含堆栈、路径、凭据或内部存储内容。

## 15. Git 规则

- **自动提交要求**：每次完成完整功能修改或添加，或解决一组 Bug 并通过完整验证后，**必须自动执行 Git 提交**，无需等待用户额外发出提交指令。
- `commit` 不等于 `push`；push、force push、reset、rebase、amend、tag、release 和 deploy 仍需用户明确许可。
- commit 前只暂存本次任务直接相关文件，先运行对应验证（如 `scripts/verify.sh` 或相应单测），核对 `git diff --cached` 确保无无关改动后自动 commit。
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
- 非 Bug 修复开发任务规划与进度（必须同步更新 `docs/tasks.md`）。

## 17. 任务面板与需求追踪规则

- **非 Bug 修复任务强制登记**：每次用户发起任何非 Bug 修复性质的任务（新功能、架构重构、数值设计、玩法拓展、规则制定、界面更新等），必须在启动开发前先深入分析用户的提示词与核心需求，登记写入 `docs/tasks.md`。
- **开发中持续更新**：在开发执行过程中（每完成阶段性目标、遇到重大分支决策或完成功能验证），必须持续同步更新 `docs/tasks.md` 中的子任务状态、进度条与进度日志。
- **时刻记录时间**：任务提出时间、需求分析完成时间、各阶段开发进度节点时间以及最终完成时间均需精确记录（格式：`YYYY-MM-DD HH:mm:ss`）。
- **任务完成与归档**：任务在通过所有验证并自动提交 Git 后，必须在 `docs/tasks.md` 中将状态更新为 `已完成`，记录完成时间并移入已完成任务归档表格。

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

