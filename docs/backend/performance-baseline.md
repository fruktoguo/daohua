# 首轮性能基线

测试日期：2026-08-06（市场账本/协议/战斗事件复测）  
构建：`.NET 10.0.204`；离线/市场附有 Release 复测，在线沿用当前服务端 Debug 实例  
环境：WSL2，AMD Ryzen 7 5800X（8 核 16 线程），约 47 GiB 可见内存  
说明：服务端与负载发生器运行在同一台机器。本结果用于原型内的相对回归，不代表 Release、容器或生产数据库环境的容量承诺；表中在线/离线数字默认使用 `memory-queue`，durable outbox 的 WAL I/O 需要单独采样。

服务端已将 ASP.NET Core 的逐请求结果日志降到 `Warning`，压测保留应用异常与市场账本日志，避免框架日志 I/O 污染收发和撮合指标。

## 当前重测入口（2026-09-14）

本文以下数字和原始命令均为 2026-08-06 历史证据，不代表当前实现已通过相同负载。旧 `online`、`idle-offline` 等场景及历史 debug 端点已删除；保留原始命令用于追溯，不应直接执行或为重跑恢复旧接口。

当前支持 `capacity-target`、`websocket`、`http-read`、`compute`、`postgres`，参数以 `src/LoadTests/LoadTestOptions.cs` 为准。参数语义：`websocket` 的 `--players` 为并发连接数、`--concurrency` 为同时握手上限；`http-read` 的实际并发由 `--concurrency` 决定（`--players` 仅入报告）；`compute` 的 `--players` 为补算玩家数；`postgres` 的探测规模固定为容量目标账号数。实际 PostgreSQL 容量门禁使用 `scripts/capacity-target.sh`；跨网络采样先启动本地服务，再执行：

```bash
dotnet run --project src/LoadTests/LoadTests.csproj -c Release -- --scenario websocket --players 500 --duration-seconds 30 --require-server --url ws://127.0.0.1:5128/ws
dotnet run --project src/LoadTests/LoadTests.csproj -c Release -- --scenario http-read --players 500 --duration-seconds 30 --require-server --url http://127.0.0.1:5128
```

`websocket --require-server` 会为每个连接注册 `loadtest-ws-*` 账号换取会话 Cookie；注册接口限流 12 次/分钟/源 IP，大规模连接数会触发 429 并把其余连接降级为 `authMode=anonymous` 的 Upgrade 可达性探测。`postgres` 场景数据库不可达即输出 Failure 报告。

新结果应独立记录日期、提交、环境和原始输出，不覆盖下方历史数据。当前指标接口为需认证的 `/api/v1/metrics` 与 `/api/v1/market/metrics`，生产 Caddy 拦截公网访问；旧 `/api/metrics` 仅为历史记录。

## 0. 2c4g/40g 容量目标门禁（历史样本）

命令：

```bash
dotnet run --project src/LoadTests/LoadTests.csproj --configuration Release --no-build -- \
  --scenario capacity-target
```

当前 Release 样本用于锁定用户要求的最低目标：`5000+` 注册账号存储、`100+` 持续网络收发、`500+` 高峰网络收发、`2000+` 偶尔上线/离线挂机玩家补算。该场景不改战斗和玩法逻辑，只使用正式 `Simulation` 与 `PacketCodec` 估算账号快照、命令包体和冷挂机补算。

| 指标 | 结果 | 预算 |
| --- | ---: | ---: |
| 错误 | 0 | 0 |
| 5000 账号估算存储 | 18,596,673 B | 268,435,456 B |
| 500 高峰命令包体 / 秒 | 64,680 B/s | 524,288 B/s |
| 2000 冷挂机补算一小时墙钟 | 1,947.9 ms | 30,000 ms |
| 进程工作集 | 126,214,144 B | 805,306,368 B |
| 完成行动 / 边界转换 | 720,000 / 720,000 | — |

本样本运行于 WSL2 5800X，强于目标 VPS；因此它是回归门禁，不是最终生产 P95/P99 承诺。正式容量验收仍需在 2 核 CPU 配额、4 GiB 内存和 PostgreSQL 容器同时运行时复测在线 500 人和 HTTP/WS P95/P99。


> 历史样本（已删除场景与端点的 2026-08-06 原始数据）已归档至 [`docs/archive/perf-baseline-20260806.md`](../archive/perf-baseline-20260806.md)。
