# 道化太初

基于 YuoCore 的 C# 服务端行动队列放置游戏原型。当前已经打通“采集草药 → 炼制引气丹 → 自动服丹引气入体”的首个完整循环，并提供 PostgreSQL 商业级落盘、可选远程冷挂机计算节点、真实 WebSocket 在线压测、离线挂机快进压测和 2c4g/40g 容量门禁。

## 目录

- `src/Daoxuan.Domain`：纯领域模型、行动目录、确定性快进算法、权威地图/寻路/边中位置、支持多敌人/首领/群体目标/功法/消耗品/持续伤害/控制与韧性修正的战斗事件模拟器、分片市场订单簿和可恢复账本模型。
- `src/Daoxuan.DataTool`：游戏数据配置导出和跨文件完整性校验工具。
- `src/Daoxuan.Protocol`：服务端、浏览器和压测共用的数据包契约。
- `src/Daoxuan.Server`：ASP.NET Core、YuoCore Fiber/ECS、WebSocket 和持久化。
- `src/Daoxuan.ComputeNode`：可通过 FRP 暴露给主服务调用的高延迟冷挂机补算节点；只处理离线快照，不持有权威状态。
- `src/Daoxuan.LoadTests`：在线、离线、完全离线和市场并发负载模拟器。
- `tests/Daoxuan.Domain.Tests`：循环、材料阻塞、快进一致性、战斗和市场测试。
- `frontend`：React 19、Zustand 和 Vite 前端；包含行动、背包、日志、战斗配置、灵市盘口、下单和挂单管理页面。
- `config/game-data`：地图、怪物、技能、物品、装备、遭遇和行动的版本化 JSON 配置。
- `docs/`：项目文档，导航索引见 `docs/README.md`。
  - `docs/design/`：游戏与数值设计——初步设计总纲、战斗数值规约、境界阶位与期望属性模型、战斗基准与产出模型、游戏数据架构、性能基线、Milky Way Idle 玩法映射参考。
  - `docs/frontend/`：前端规范——公共组件与统一模板速查手册、悬浮提示与浮层专项设计。
  - `docs/ops/`：部署运维——生产构建、CCR 发布、服务器更新与回滚。
  - `docs/world/`：世界观与剧情——设定总览、世界观、章节剧情与地图设计。

## 本地运行

要求：.NET SDK 10、Node.js 22 或以上。首次克隆先初始化固定版本的 YuoCore submodule：

```bash
git submodule update --init --recursive
```

前后端一起开发时推荐使用 `scripts/dev.sh`：服务端由 `scripts/dev-hot.mjs` 守护，源码变更进入
60 秒防抖静默期（期间有新变更则刷新等待），静默期满先 `dotnet build`——编译失败保持旧进程继续运行，
成功才优雅重启服务端；服务端进程异常退出时按退避自动拉起。防抖时长可用环境变量
`SERVER_DEV_RESTART_DEBOUNCE_MS` 调整（设为 0 表示立即重建）。前端仍为 Vite 热更新。

```bash
scripts/dev.sh
```

启动服务端：

```bash
dotnet run --project src/Daoxuan.Server/Daoxuan.Server.csproj
```

开发服务固定监听 `127.0.0.1:5128`，存档写入仓库根目录 `data`。需要使用与生产一致的只读根文件系统、非 root 用户和 durable outbox 时，使用固定入口：

```bash
scripts/dev-service.sh up
```

该入口使用显式命名卷 `daoxuan_daoxuan-data` 与 `daoxuan_daoxuan-postgres`，重新构建不会更换存档卷，也不会在端口占用时静默切换到其他端口。

服务端默认监听 `http://127.0.0.1:5128`，WebSocket 地址为 `ws://127.0.0.1:5128/ws`。

在线行动采用事件截止时间调度，不设置固定 Tick。服务端使用单调时间轴直接等待下一次攻击、施法、持续效果、搜索、复活或行动完成的精确毫秒边界；即使线程因操作系统调度稍晚唤醒，领域结算仍按原定边界顺序补算，不会把延迟累积到下一次行动。快照检查点、冷玩家回收和市场过期维护由独立后台调度器执行，不参与游戏时间推进。协议同时下发行动的绝对完成时刻，前端行动条按该时刻线性显示。

需要启用 YuoCore durable outbox 时：

```bash
Storage__DurableOutboxEnabled=true \
Storage__OutboxDirectory=data/outbox \
dotnet run --project src/Daoxuan.Server/Daoxuan.Server.csproj
```

生产同构 Compose 默认启用 PostgreSQL：账号、玩家快照、durable outbox、市场账本和市场流水均落库；本地直跑不配置数据库时仍可使用 `local-json` 便捷开发路径。

```bash
Storage__Provider=postgres \
Storage__ConnectionString='Host=127.0.0.1;Port=5432;Database=daoxuan;Username=daoxuan;Password=<密码>' \
Market__LedgerProvider=postgres \
dotnet run --project src/Daoxuan.Server/Daoxuan.Server.csproj
```

远程冷挂机计算节点可在个人设备上运行，并通过 FRP 暴露给主服务。节点只接受主服务提交的离线快照批处理，不连接生产数据库、不处理在线会话：

```bash
ComputeNode__Token=<共享令牌> \
ComputeNode__Urls=http://127.0.0.1:5129 \
dotnet run --project src/Daoxuan.ComputeNode/Daoxuan.ComputeNode.csproj
```

启动前端：

```bash
cd frontend
npm install
npm run dev
```

浏览器固定打开 `http://127.0.0.1:15172`。Vite 使用 `strictPort`，端口被占用时直接报错，不再静默换端口；同源 `/api` 和 `/ws` 代理到 `127.0.0.1:5128`。玩家 UUID 同时保存在同主机 Cookie 与当前 origin 的 `localStorage`，开发端口变化时仍优先复用同一个玩家身份；如需连接其他服务端，设置 `VITE_API_URL` 或 `VITE_WS_URL`。

## 构建与测试

```bash
dotnet build Daoxuan.slnx --disable-build-servers --nologo
dotnet test Daoxuan.slnx --no-build --disable-build-servers --nologo
npm --prefix frontend run build
```

也可以运行统一验证入口：

```bash
scripts/verify.sh
```

2c4g/40g 容量目标门禁：

```bash
dotnet run --project src/Daoxuan.LoadTests/Daoxuan.LoadTests.csproj -- \
  --scenario capacity-target
```

单独校验游戏数据：

```bash
dotnet run --project src/Daoxuan.DataTool/Daoxuan.DataTool.csproj -- \
  validate config/game-data
```

## Docker 与生产部署

本地运行生产同构镜像：

```bash
docker compose up -d --build
node scripts/smoke-production.mjs http://127.0.0.1:5128
```

项目提供 CCR 不可变版本和 `prod` 标签发布、PostgreSQL 主存储、整点自动备份轮转、服务器定时更新、失败回滚、持久卷及 `xx.yuohira.com` Caddy 配置。完整流程、运维命令和当前分布式边界见 `docs/ops/deployment.md`。

## 性能场景

在线场景会建立真实 `ClientWebSocket`，并用生产 `DaoxuanPacketCodec` 发送相同节奏的命令包：

```bash
dotnet run --project src/Daoxuan.LoadTests/Daoxuan.LoadTests.csproj -- \
  --scenario online --players 500 --duration-seconds 60 \
  --url ws://127.0.0.1:5128/ws --connect-concurrency 64
```

一万名离线挂机玩家补算一小时：

```bash
dotnet run --project src/Daoxuan.LoadTests/Daoxuan.LoadTests.csproj -- \
  --scenario idle-offline --players 10000 --duration-seconds 3600
```

一万名历练玩家补算一小时暮影谷多敌人战斗行动：

```bash
dotnet run --project src/Daoxuan.LoadTests/Daoxuan.LoadTests.csproj -- \
  --scenario combat-offline --players 10000 --duration-seconds 3600
```

十万名完全离线玩家进行账号规模与排行榜聚合基线测试：

```bash
dotnet run --project src/Daoxuan.LoadTests/Daoxuan.LoadTests.csproj -- \
  --scenario fully-offline --players 100000 --duration-seconds 3600
```

市场订单簿并发基线（`players` 为订单总数）：

```bash
dotnet run --project src/Daoxuan.LoadTests/Daoxuan.LoadTests.csproj -- \
  --scenario market --players 100000 --duration-seconds 1
```

500 人按正式灵市页面节奏读取盘口、个人挂单和市场指标：

```bash
dotnet run --project src/Daoxuan.LoadTests/Daoxuan.LoadTests.csproj -- \
  --scenario market-read --players 500 --duration-seconds 15 \
  --url ws://127.0.0.1:5128/ws --connect-concurrency 64
```

十万条过期挂牌的时间索引回收基线：

```bash
dotnet run --project src/Daoxuan.LoadTests/Daoxuan.LoadTests.csproj -- \
  --scenario market-expiry --players 100000 --duration-seconds 1
```

2c4g/40g 容量门禁（5000+ 注册账号、500 高峰命令包体、2000 冷挂机补算）：

```bash
dotnet run --project src/Daoxuan.LoadTests/Daoxuan.LoadTests.csproj -- \
  --scenario capacity-target
```

目录接口：`GET /api/actions`、`GET /api/items`、`GET /api/world/maps`、`GET /api/combat/abilities`、`GET /api/combat/equipment`、`GET /api/combat/encounters`。地图移动通过 WebSocket `travel_to` 发送目标节点；服务端规划完整路线并以一个队首行动推进，停止时保留边中位置。战斗配置通过 WebSocket `set_combat_loadout` 更新，支持 4 个功法槽、3 个通用消耗品槽、法器与 0-20 级强化、绝对值与百分比触发条件，以及按 `100 / (100 + tenacity)` 修正控制命中率的韧性字段。盘口接口：`GET /api/market/book?itemId=item.spirit-herb&enhancementLevel=0&depth=20`；市场写接口为 `POST /api/players/{playerId}/market/orders`、`DELETE /api/players/{playerId}/market/orders/{orderId}`、`POST /api/players/{playerId}/market/claim`，WebSocket 对应命令为 `market_place`、`market_cancel`、`market_claim`。市场账本在 PostgreSQL 中通过 revision CAS、advisory lock 和主分片拓扑持久化；玩家快照可与市场账本在同一事务内提交。

所有场景输出 JSON，包含耗时、CPU 时间、托管内存、工作集、收发字节、帧数、行动结算数、错误数和服务端队列指标。`combat-offline` 还统计战斗事件、完成遭遇、敌人击杀、胜负和离线待推送日志；`market-read` 直接请求正式 HTTP 端点，并分别统计完整响应、`304`、头部/正文大小和 P50/P95/P99。

地图、怪物、技能、物品、装备、遭遇和行动已经从业务代码迁移到 `config/game-data`。服务启动时会校验结构版本、重复 ID、跨文件引用和数值范围，并把内容版本与配置指纹暴露到健康端点。生命归零只会让该玩家进入 180 秒复活状态，不会结束当前波次、重置怪物或扣减战斗次数；死亡玩家不能行动或成为攻击目标，组队成员和召唤物可继续推进同一场战斗。复活时恢复满生命和满灵力，并重置技能与物品冷却；玩家主动重新开始历练也会回满资源并建立全新的战斗时间轴。战斗中的技能和物品冷却由服务端权威模拟投影到客户端槽位倒计时。

无会话玩家在 30 秒无访问后会转为序列化冷快照，重连时按同一离线快进入口恢复；这只释放 ECS 聚合，不改变账号快照或行动队列。生产同构模式下 durable outbox、玩家快照和市场账本落在 PostgreSQL；市场挂牌按官方公开规则保留 30 天，以订单簿时间索引分钟级回收并返还冻结资产。远程计算节点只批量补算落后的离线快照，成功后由主服务回写权威 PostgreSQL 快照。
