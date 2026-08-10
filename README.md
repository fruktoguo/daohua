# 太虚仙途（暂定名）

基于 YuoCore 的 C# 服务端行动队列放置游戏原型。当前已经打通“采集草药 → 炼制引气丹 → 自动服丹引气入体”的首个完整循环，并提供真实 WebSocket 在线压测、离线挂机快进压测和完全离线账号压测。

## 目录

- `src/Xiuxian.Domain`：纯领域模型、行动目录、确定性快进算法、权威地图/寻路/边中位置、支持多敌人/首领/群体目标/功法/消耗品/持续伤害/控制与韧性修正的战斗事件模拟器、分片市场订单簿和可恢复账本模型。
- `src/Xiuxian.DataTool`：游戏数据配置导出和跨文件完整性校验工具。
- `src/Xiuxian.Protocol`：服务端、浏览器和压测共用的数据包契约。
- `src/Xiuxian.Server`：ASP.NET Core、YuoCore Fiber/ECS、WebSocket 和持久化。
- `src/Xiuxian.LoadTests`：在线、离线、完全离线和市场并发负载模拟器。
- `tests/Xiuxian.Domain.Tests`：循环、材料阻塞、快进一致性、战斗和市场测试。
- `frontend`：React 19、Zustand 和 Vite 前端；包含行动、背包、日志、战斗配置、灵市盘口、下单和挂单管理页面。
- `config/game-data`：地图、怪物、技能、物品、装备、遭遇和行动的版本化 JSON 配置。
- `docs/initial-design.md`：初步游戏与技术设计。
- `docs/performance-baseline.md`：三种在线状态的首轮实测基线。
- `docs/milky-way-idle-mapping.md`：公开玩法/数值到修仙系统的映射与排除边界。

## 本地运行

要求：.NET SDK 10、Node.js 22 或以上。首次克隆先初始化固定版本的 YuoCore submodule：

```bash
git submodule update --init --recursive
```

启动服务端：

```bash
dotnet run --project src/Xiuxian.Server/Xiuxian.Server.csproj
```

开发服务固定监听 `127.0.0.1:5128`，存档写入仓库根目录 `data`。需要使用与生产一致的只读根文件系统、非 root 用户和 durable outbox 时，使用固定入口：

```bash
scripts/dev-service.sh up
```

该入口使用显式命名卷 `xiuxian_xiuxian-data`，重新构建不会更换存档卷，也不会在端口占用时静默切换到其他端口。

服务端默认监听 `http://127.0.0.1:5128`，WebSocket 地址为 `ws://127.0.0.1:5128/ws`。

在线行动采用事件截止时间调度，不设置固定 Tick。服务端使用单调时间轴直接等待下一次攻击、施法、持续效果、搜索、复活或行动完成的精确毫秒边界；即使线程因操作系统调度稍晚唤醒，领域结算仍按原定边界顺序补算，不会把延迟累积到下一次行动。快照检查点、冷玩家回收和市场过期维护由独立后台调度器执行，不参与游戏时间推进。协议同时下发行动的绝对完成时刻，前端行动条按该时刻线性显示。

需要启用 YuoCore durable outbox 时：

```bash
Storage__DurableOutboxEnabled=true \
Storage__OutboxDirectory=data/outbox \
dotnet run --project src/Xiuxian.Server/Xiuxian.Server.csproj
```

启用后玩家快照先写入本地 WAL，再由后台原子写回并按序 ack；启动时会 replay 未确认命令，停服时 compact 已确认帧。默认关闭该模式以保持性能基线的内存队列路径。

市场当前默认使用单机 `local-json` 账本：

```bash
Market__LedgerProvider=local-json \
Market__PartitionCount=1 \
Market__PartitionId=0 \
dotnet run --project src/Xiuxian.Server/Xiuxian.Server.csproj
```

`GET /api/market/topology` 会返回实例负责的主分片和稳定哈希算法。`local-json` 只允许 `0/1` 单主分片，并继续用 `data/snapshots/market-owner.lock` 防止双实例分裂；配置多个主分片会在启动时拒绝。盘口、市场指标和玩家挂单读取支持 `ETag` 条件响应，未变化时返回 `304`，避免重复生成和传输 JSON。PostgreSQL 事务账本需要单独启用数据库适配器，未接入前不会把配置伪装成可用。

启动前端：

```bash
cd frontend
npm install
npm run dev
```

浏览器固定打开 `http://127.0.0.1:15172`。Vite 使用 `strictPort`，端口被占用时直接报错，不再静默换端口；同源 `/api` 和 `/ws` 代理到 `127.0.0.1:5128`。玩家 UUID 同时保存在同主机 Cookie 与当前 origin 的 `localStorage`，开发端口变化时仍优先复用同一个玩家身份；如需连接其他服务端，设置 `VITE_API_URL` 或 `VITE_WS_URL`。

## 构建与测试

```bash
dotnet build Xiuxian.slnx --disable-build-servers --nologo
dotnet test Xiuxian.slnx --no-build --disable-build-servers --nologo
npm --prefix frontend run build
```

也可以运行统一验证入口：

```bash
scripts/verify.sh
```

单独校验游戏数据：

```bash
dotnet run --project src/Xiuxian.DataTool/Xiuxian.DataTool.csproj -- \
  validate config/game-data
```

## Docker 与生产部署

本地运行生产同构镜像：

```bash
docker compose up -d --build
node scripts/smoke-production.mjs http://127.0.0.1:5128
```

项目提供 CCR 不可变版本和 `prod` 标签发布、服务器定时更新、失败回滚、持久卷、一致性备份及 `xx.yuohira.com` Caddy 配置。完整流程、运维命令和当前单实例/鉴权边界见 `docs/deployment.md`。

## 性能场景

在线场景会建立真实 `ClientWebSocket`，并用生产 `XiuxianPacketCodec` 发送相同节奏的命令包：

```bash
dotnet run --project src/Xiuxian.LoadTests/Xiuxian.LoadTests.csproj -- \
  --scenario online --players 500 --duration-seconds 60 \
  --url ws://127.0.0.1:5128/ws --connect-concurrency 64
```

一万名离线挂机玩家补算一小时：

```bash
dotnet run --project src/Xiuxian.LoadTests/Xiuxian.LoadTests.csproj -- \
  --scenario idle-offline --players 10000 --duration-seconds 3600
```

一万名历练玩家补算一小时暮影谷多敌人战斗行动：

```bash
dotnet run --project src/Xiuxian.LoadTests/Xiuxian.LoadTests.csproj -- \
  --scenario combat-offline --players 10000 --duration-seconds 3600
```

十万名完全离线玩家进行账号规模与排行榜聚合基线测试：

```bash
dotnet run --project src/Xiuxian.LoadTests/Xiuxian.LoadTests.csproj -- \
  --scenario fully-offline --players 100000 --duration-seconds 3600
```

市场订单簿并发基线（`players` 为订单总数）：

```bash
dotnet run --project src/Xiuxian.LoadTests/Xiuxian.LoadTests.csproj -- \
  --scenario market --players 100000 --duration-seconds 1
```

500 人按正式灵市页面节奏读取盘口、个人挂单和市场指标：

```bash
dotnet run --project src/Xiuxian.LoadTests/Xiuxian.LoadTests.csproj -- \
  --scenario market-read --players 500 --duration-seconds 15 \
  --url ws://127.0.0.1:5128/ws --connect-concurrency 64
```

十万条过期挂牌的时间索引回收基线：

```bash
dotnet run --project src/Xiuxian.LoadTests/Xiuxian.LoadTests.csproj -- \
  --scenario market-expiry --players 100000 --duration-seconds 1
```

目录接口：`GET /api/actions`、`GET /api/items`、`GET /api/world/maps`、`GET /api/combat/abilities`、`GET /api/combat/equipment`、`GET /api/combat/encounters`。地图移动通过 WebSocket `travel_to` 发送目标节点；服务端规划完整路线并以一个队首行动推进，停止时保留边中位置。战斗配置通过 WebSocket `set_combat_loadout` 更新，支持 4 个功法槽、3 个通用消耗品槽、法器与 0-20 级强化、绝对值与百分比触发条件，以及按 `100 / (100 + tenacity)` 修正控制命中率的韧性字段。历练首次搜索固定 20 秒，后续重新搜索以遭遇的 5 秒基线按寻怪效率衰减；每一波遭遇由当前地图节点的 `encounterIds` 决定，行动配置只保留旧数据兼容回退。战斗本身不限时，生命与灵力跨遭遇保留，并通过 `combatResources` 与 `queueProgress.expedition` 向客户端同步。遭遇目录包含加权敌群、强度上限、首领轮次和群体功法目标类型。盘口接口：`GET /api/market/book?itemId=item.spirit-herb&enhancementLevel=0&depth=20`；市场写接口为 `POST /api/players/{playerId}/market/orders`、`DELETE /api/players/{playerId}/market/orders/{orderId}`、`POST /api/players/{playerId}/market/claim`，WebSocket 对应命令为 `market_place`、`market_cancel`、`market_claim`。活动挂单可通过 `GET /api/players/{playerId}/market/orders` 查询；跨实例路由信息通过 `GET /api/market/topology` 查询。市场账本当前使用本地原子 JSON 快照，并由存储边界统一执行 revision CAS 和单写者租约；正式 PostgreSQL 适配器需要在同一事务中提交账本、玩家快照和 Outbox。

所有场景输出 JSON，包含耗时、CPU 时间、托管内存、工作集、收发字节、帧数、行动结算数、错误数和服务端队列指标。`combat-offline` 还统计战斗事件、完成遭遇、敌人击杀、胜负和离线待推送日志；`market-read` 直接请求正式 HTTP 端点，并分别统计完整响应、`304`、头部/正文大小和 P50/P95/P99。

地图、怪物、技能、物品、装备、遭遇和行动已经从业务代码迁移到 `config/game-data`。服务启动时会校验结构版本、重复 ID、跨文件引用和数值范围，并把内容版本与配置指纹暴露到健康端点。生命归零只会让该玩家进入 180 秒复活状态，不会结束当前波次、重置怪物或扣减战斗次数；死亡玩家不能行动或成为攻击目标，组队成员和召唤物可继续推进同一场战斗。复活时恢复满生命和满灵力，并重置技能与物品冷却；玩家主动重新开始历练也会回满资源并建立全新的战斗时间轴。战斗中的技能和物品冷却由服务端权威模拟投影到客户端槽位倒计时。

无会话玩家在 30 秒无访问后会转为序列化冷快照，重连时按同一离线快进入口恢复；这只释放 ECS 聚合，不改变账号快照或行动队列。durable outbox 是本地持久化可靠性层；市场挂牌按官方公开规则保留 30 天，以订单簿时间索引分钟级回收并返还冻结资产。市场存储契约已预留数据库事务、Outbox 和主分片 CAS 边界，但 PostgreSQL 驱动尚未接入。
