# 道化太初生产构建与部署

> **文档定位**：生产构建、CCR 镜像发布、服务器部署更新、持久卷与备份回滚的完整运维流程；本地开发启动见根目录 `README.md`。

## 目标架构

```text
本地或 CI
  -> Release build + tests + capacity-target
  -> linux/amd64 多阶段镜像（app + compute node 产物）
  -> CCR 不可变标签 + prod 标签
  -> yuohira 服务器自动更新器
  -> PostgreSQL 主存储（账号 / 玩家快照 / outbox / 市场账本 / 流水）
  -> 127.0.0.1:15128
  -> Caddy / HTTPS / WebSocket
  -> https://xx.yuohira.com
```

生产容量目标固定按 `2c4g/40g` 预算设计：`5000+` 注册账号、`100+` 持续网络收发、`500+` 高峰网络收发、`2000+` 偶尔上线/离线挂机补算。`scripts/verify.sh` 会运行 `capacity-target` 门禁，作为每次改动的最低回归检查。

镜像同时包含 Vite 静态产物、ASP.NET Core 主服务和 `Daoxuan.ComputeNode` 计算节点产物。浏览器 API 与 WebSocket 走同源；生产权威数据不在镜像内，PostgreSQL 数据保存在 Docker 卷 `daoxuan_daoxuan-postgres`。

当前线上仍是单 `app` 实例承载在线连接；PostgreSQL 已提供事务快照、outbox、市场 revision CAS 和 advisory lock，为后续多主分片保留边界。远程个人设备只能运行高延迟冷挂机计算节点，通过 FRP 暴露内部 HTTP 批处理接口；它不连接生产数据库，不处理在线连接、市场写入或账号鉴权。

## 环境要求

- .NET SDK 10，仓库 `global.json` 允许向同一大版本的最新 feature band 滚动。
- Node.js 22、npm。
- Docker Engine、Docker Compose 和 Docker Buildx。
- 可访问 `ccr.ccs.tencentyun.com`。
- 生产发布者已执行 `docker login ccr.ccs.tencentyun.com`。
- 远端 root Docker 也必须登录私有 CCR；凭据只能通过 `docker login --password-stdin` 安装，不得写入仓库或部署脚本。
- SSH 别名 `yuohira` 可登录且具备免交互 `sudo`。
- 生产 `/opt/daoxuan/daoxuan.env` 必须设置 `DAOXUAN_POSTGRES_PASSWORD`；可选设置 `DAOXUAN_ACCESS_TOKEN`、`DAOXUAN_DISTRIBUTED_COMPUTE_ENABLED`、`DAOXUAN_COMPUTE_NODE_ENDPOINTS`、`DAOXUAN_COMPUTE_TOKEN`。

首次克隆必须初始化依赖：

```bash
git clone --recurse-submodules <仓库地址>
cd daoxuan
git submodule update --init --recursive
```

YuoCore 固定在 `vendor/YuoCore` 的 submodule commit，镜像构建不再依赖仓库外目录。

私有 CCR 首次部署前，经明确授权后安装服务器登录态：

```bash
scripts/install-ccr-auth.sh --confirm
```

脚本只读取本机 CCR 的单个 auth 条目，通过 SSH stdin 调用远端 `docker login --password-stdin`；不会打印凭据，也不会复制其他 registry 配置。它会先停用 timer，真实拉取和容器健康验证成功后才重新启用自动更新。

## 开发与验证

后端：

```bash
dotnet run --project src/Daoxuan.Server/Daoxuan.Server.csproj
```

直接运行后端时固定监听 `127.0.0.1:5128`，开发存档统一写入仓库根目录的 `data`，不会再因启动目录变化落到不同的 `src/*/data`。该目录被 Git 和镜像构建上下文排除。

前端：

```bash
npm --prefix frontend ci
npm --prefix frontend run dev
```

Vite 固定监听 `127.0.0.1:15172`，并把 `/api` 和 `/ws` 代理到 `127.0.0.1:5128`；`strictPort` 会在端口占用时直接失败，禁止静默漂移到其他端口。完整门禁：

```bash
scripts/verify.sh
```

门禁同时校验 `config/game-data` 的结构版本、重复 ID、跨文件引用、内容指纹和 `2c4g/40g` 容量目标。

本地生产同构验证：

```bash
scripts/dev-service.sh up
scripts/dev-service.sh status
scripts/dev-service.sh down
```

`scripts/dev-service.sh` 固定使用 `127.0.0.1:5128`，端口被其他进程占用时会直接失败，不会自动换端口。Compose 始终使用显式命名卷 `daoxuan_daoxuan-postgres`；普通 `down`、重新构建和项目名变化都不会更换数据库卷。不要执行 `docker compose down --volumes` 或手工删除该卷，除非明确要删除本地持久数据。

## CCR 镜像发布

正常发布要求工作区已提交且验证通过：

```bash
scripts/verify.sh
scripts/docker-build-prod.sh --push
```

默认会推送：

```text
ccr.ccs.tencentyun.com/yuohira/daoxuan:sha-<commit前12位>
ccr.ccs.tencentyun.com/yuohira/daoxuan:prod
```

不可变标签用于审计和回滚，`prod` 用于自动更新。仅构建、不推送：

```bash
scripts/docker-build-prod.sh
```

对本地镜像完成容器烟测后，推送同一个已验收产物：

```bash
scripts/docker-build-prod.sh --push-existing
```

脚本会先确认不可变标签和 `prod` 在本机指向同一镜像 ID，不会在推送前重新构建。

首次引导仓库尚无 commit 时，必须显式标识版本和 dirty 构建：

```bash
scripts/docker-build-prod.sh \
  --allow-dirty \
  --release-tag bootstrap-YYYYMMDDTHHMMSSZ
node scripts/smoke-production.mjs http://127.0.0.1:15128
scripts/docker-build-prod.sh \
  --allow-dirty \
  --release-tag bootstrap-YYYYMMDDTHHMMSSZ \
  --push-existing
```

该方式只用于首次部署，后续发布不得长期使用 `--allow-dirty`。

## 首次部署

优先使用刚推送的不可变镜像：

```bash
scripts/deploy-prod.sh \
  --image ccr.ccs.tencentyun.com/yuohira/daoxuan:sha-<commit前12位>
```

脚本会执行以下操作：

1. 只读检查远端 `sudo`、Docker 和 Compose。
2. 校验 `/opt/daoxuan/daoxuan.env` 已设置 `DAOXUAN_POSTGRES_PASSWORD`。
3. 安装 `/opt/daoxuan/compose.prod.yaml`、更新器、数据库备份脚本和 systemd timer。
4. 拉取指定镜像并等待 PostgreSQL 与应用容器健康。
5. 启用 `daoxuan-ccr-auto-update.timer` 和 `daoxuan-db-backup.timer`。
6. 备份、校验并 reload 现有 Caddy，将站点接入 `127.0.0.1:15128`。
7. 验证回环健康端点、HTTPS 页面、API 和 WebSocket 首包。

应用容器只绑定服务器回环地址，不直接开放业务端口。Caddy 配置位于：

```text
/etc/caddy/sites/daoxuan.caddy
```

主 Caddyfile 只增加：

```caddyfile
import /etc/caddy/sites/*.caddy
```

每次安装都会保留带 UTC 时间戳的 Caddy 配置备份。

## 自动更新

`daoxuan-ccr-auto-update.timer` 每分钟拉取 `prod`，比较候选镜像 ID 和当前容器镜像 ID：

- ID 相同：不重建容器。
- ID 变化：先用隔离 tmpfs 启动候选镜像，验证健康端点、游戏数据和行动目录，再优雅替换单写正式实例。
- 候选预热失败：当前正式容器保持不变，并记录失败镜像 ID。
- 新镜像失败：回滚上一镜像并记录失败 ID。
- CCR 出现不同镜像前：不重复尝试同一失败版本。

查看状态：

```bash
ssh yuohira 'sudo systemctl status daoxuan-ccr-auto-update.timer --no-pager'
ssh yuohira 'sudo journalctl -u daoxuan-ccr-auto-update.service -n 100 --no-pager'
ssh yuohira 'sudo docker inspect --format "{{.Config.Image}} {{.Image}} {{.State.Health.Status}}" daoxuan-app'
```

手工触发检查：

```bash
ssh yuohira 'sudo systemctl start daoxuan-ccr-auto-update.service'
```

## 健康与日志

回环检查：

```bash
ssh yuohira 'curl -fsS http://127.0.0.1:15128/healthz'
ssh yuohira 'curl -fsS http://127.0.0.1:15128/readyz'
```

公网闭环：

```bash
node scripts/smoke-production.mjs https://xx.yuohira.com
```

容器日志：

```bash
ssh yuohira 'sudo docker logs --tail 200 daoxuan-app'
```

日志使用 Docker `json-file`，单文件 20 MiB，最多保留 5 个文件。

## 数据备份

自动备份由 `daoxuan-db-backup.timer` 在每个整点触发：

```bash
ssh yuohira 'sudo systemctl status daoxuan-db-backup.timer --no-pager'
ssh yuohira 'sudo /opt/daoxuan/backup.sh'
```

`/opt/daoxuan/backup.sh` 使用 `pg_dump --format=custom --compress=gzip:9` 生成 PostgreSQL custom dump，并用 `pg_restore --list` 校验归档。保留策略固定为：小时备份 24 份、每日 00:00 备份 7 份、每周一 00:00 备份 4 份；每份归档旁生成 `.sha256` 文件。

备份保存在 `/opt/daoxuan/backups/{hourly,daily,weekly}`。恢复会覆盖生产数据库，必须单独获得明确许可，并在维护窗口内执行。恢复前应再次备份当前数据库、停用自动更新 timer、停止应用、校验目标归档，再执行恢复和烟测。

## 回滚

先停止自动更新，防止 `prod` 立即覆盖回滚版本：

```bash
ssh yuohira 'sudo systemctl stop daoxuan-ccr-auto-update.timer'
```

使用已验证的不可变标签重新部署，常规路径是从本仓库运行：

```bash
scripts/deploy-prod.sh \
  --skip-caddy \
  --image ccr.ccs.tencentyun.com/yuohira/daoxuan:sha-<目标commit>
```

验证完成后再决定是否恢复 timer：

```bash
ssh yuohira 'sudo systemctl enable --now daoxuan-ccr-auto-update.timer'
```

## 访问令牌（可选）

当前客户端只用本地 UUID 标识玩家，`playerId` 即权限：知道 ID 就能读取该玩家快照，并以下单、撤单、领取市场资产。服务端提供可选共享访问令牌，为公网部署增加最低门槛：

- 配置 `Server:AccessToken`（环境变量 `Server__AccessToken`）后，`/ws`、`GET /api/players/{id}` 与三个市场写接口都要求携带令牌：HTTP 使用 `X-Access-Token` 请求头，WebSocket 使用 query 参数 `token`；缺失或不匹配返回 401。
- 未配置时不启用鉴权，行为与历史版本一致；启动日志会输出 Warning：`未配置 Server:AccessToken，玩家级接口无鉴权，仅建议本地开发使用`。

生成强随机令牌：

```bash
openssl rand -base64 32
```

本地开发：

```bash
Server__AccessToken=<令牌> dotnet run --project src/Daoxuan.Server/Daoxuan.Server.csproj
```

前端在「设置 → 访问令牌」保存后写入 localStorage（键 `daoxuan_access_token`），之后每个 API 请求和 WebSocket 连接自动携带。

生产在 `/opt/daoxuan/daoxuan.env` 中设置：

```text
DAOXUAN_ACCESS_TOKEN=<令牌>
```

Compose 将其插值为容器环境变量 `Server__AccessToken`；自动更新 timer 通过 unit 内 `EnvironmentFile=-/opt/daoxuan/daoxuan.env` 读取同一文件，保证自动重建容器时插值不丢失。修改后需重建容器才生效（等待一次自动更新，或手工执行 `docker compose up -d --force-recreate app`）。

该令牌是部署者持有的共享口令，不是账号体系：轮换令牌即可让全部旧客户端失效，但在接入真正的身份认证之前，仍不得用于充值或真实资产归属。

## 安全与商业化边界

当前流程提供可复现构建、镜像审计、CCR 发布、自动更新、健康回滚、HTTPS、安全响应头、PostgreSQL 主存储、分层自动备份和可选远程冷挂机计算节点。

当前所谓热更新是“候选镜像预热 + 单写实例优雅替换 + WebSocket 自动重连”，可以显著缩短不可用窗口，但不是双实例无停机。`/readyz` 会明确返回 `zeroDowntimeReady=false`；启用 PostgreSQL 后 `scaleOutReady=true` 只表示存储和市场分片边界已经具备分布式前提，不代表在线玩家已多实例路由。

应用业务仍有两项明确边界：

- 客户端本地 UUID 不是商业账号认证，不能用于充值或真实资产归属。
- 远程计算节点只适合高延迟冷挂机补算，不可承载在线战斗、市场写路径或数据库写权限。

达到真正红绿无停机前必须完成玩家分片所有权、幂等命令、Caddy 红绿健康路由和旧实例连接排空。不同实例还必须以配置指纹和协议版本阻止不兼容版本同时承载同一玩家。

正式开放付费前，应先接入服务端认证授权、审计日志、异地备份和恢复演练；这些属于业务与数据架构升级，不应伪装成部署脚本已经解决。
