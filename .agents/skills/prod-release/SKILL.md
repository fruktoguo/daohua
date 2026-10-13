---
name: prod-release
description: >-
  正式服发布：一次发布同时更新 yuohira 主服务器（linux/amd64 不可变镜像）与 odin 计算节点（linux-arm64 自包含发布），覆盖发布前门禁与备份、镜像构建推送、主服受控更新、odin 节点重发布、双层烟测、存档版本漂移的手动修复与回滚路径。适用于「正式服发布」「发布到生产」「更新主服和 odin」「prod release」等请求。
---

# 正式服发布（yuohira 主服 + odin 计算节点）

一次发布必须**成对更新**两端：yuohira 主服（Docker 不可变镜像）与 odin 计算节点（本地交叉发布 linux-arm64）。两端代码必须来自**同一 Git commit**——配表内嵌于 Domain.dll，节点与主服配表指纹不一致时主服会以 `GAME_DATA_FINGERPRINT_MISMATCH` 拒绝拨入。

```text
前置门禁 → 构建推送镜像 → 发布前备份 → 主服更新 → odin 节点更新 → 双端验证 → （存档问题手动修复）→ 收尾登记
```

## 阶段 0：前置门禁（全部通过才可继续）

1. `git status --short` 必须干净——`docker-build-prod.sh` 拒绝 dirty worktree；用户有未提交改动时先停下询问，禁止擅自提交或丢弃。
2. `git submodule status` 确认 YuoCore 无缺失、无漂移（`+`/`-` 前缀都算漂移）。
3. `scripts/verify.sh` 全绿（Release 构建、Domain/Ecs/Server 三套测试、DataTool 校验、Compose 配置、前端构建）。
4. 容量相关变更（存储结构、快照、队列、市场）追加跑 `scripts/capacity-target.sh`。
5. 确认远端可达：`ssh -o BatchMode=yes yuohira true` 与 `ssh -o BatchMode=yes odin true`。
6. **odin 网络预检**：odin 经 Clash Meta TUN 上网，出站节点挂掉会导致节点拨不进主服（2026-10-13 事故）。发布前先验证：
   ```bash
   ssh odin 'curl -s -m 10 -o /dev/null -w "%{http_code}\n" https://daohua.yuohira.com/healthz'
   ```
   非 200 时先修 odin 网络（换 Clash 节点/加直连规则），否则发布后节点无法拨入。

## 阶段 1：构建与推送镜像

```bash
scripts/docker-build-prod.sh --push --release-tag sha-<短hash>
```

- 一次构建**成对**镜像并推 CCR：`daohua:<tag>`（Server）与 `daohua-compute:<tag>`（ComputeNode），同时打 `prod` 移动标签。
- 记录 push 输出里的 digest；随后用 `docker inspect --format '{{index .RepoDigests 0}}' ccr.ccs.tencentyun.com/yuohira/daohua:<tag>` 取 `@sha256:...` 完整引用（deploy-prod.sh 强制成对 digest，不接受浮动标签）。

## 阶段 2：发布前备份（生产数据红线）

发布前必须确认有一份**已验证**的近期备份；没有则立即手动触发：

```bash
ssh yuohira 'sudo /opt/daohua/backup.sh && sudo /opt/daohua/verify-backup.sh'
```

- 备份落在 `/opt/daohua/backups/`（小时/日/周分层轮转），`verify-backup.sh` 会做 `pg_restore --list` 完整性校验。
- 本次发布若涉及 `PostgresSchema.Version` 升级（存档格式/表结构变更），备份是唯一后悔药——**备份失败就中止发布**。

## 阶段 3：yuohira 主服更新

```bash
SMOKE_ACCOUNT='<烟测账号>' SMOKE_PASSWORD='<烟测口令>' \
scripts/deploy-prod.sh \
  --image ccr.ccs.tencentyun.com/yuohira/daohua@sha256:<digest> \
  --compute-image ccr.ccs.tencentyun.com/yuohira/daohua-compute@sha256:<digest>
```

- 脚本内置：远端环境校验 → CCR 拉取验证 → 资产安装 → 成对容器更新 → 回环 `/readyz` → 服务器回环全量烟测 → 公网 HTTPS/WebSocket 烟测（断言 `/readyz` 被 Caddy 拦截）。
- 没有烟测凭证时用 `--skip-smoke`，但**必须在阶段 5 手动补验**。
- 生产环境变量占位值校验失败（`daohua.env` 仍是 change-me 类）会拒绝部署——这是门禁不是故障。

## 阶段 4：odin 计算节点更新

odin 是 aarch64 掌机（Termux + PRoot Ubuntu），**不能复用 amd64 生产镜像**，必须本地交叉发布。前提：本地 HEAD 就是主服镜像的构建 commit（工作区干净时天然成立）。

```bash
# 1. 交叉发布（约 1-3 分钟）
dotnet publish src/ComputeNode -c Release -r linux-arm64 --self-contained -o /tmp/daohua-compute-odin --nologo

# 2. 上传到新目录（运行中二进制不可覆写，ETXTBSY）
ssh -o BatchMode=yes odin 'rm -rf /root/daohua-compute.new && mkdir -p /root/daohua-compute.new'
scp -q -r /tmp/daohua-compute-odin/* odin:/root/daohua-compute.new/

# 3. 原子换目录 + 杀旧进程（Termux 层守护 10s 内自动拉起）
ssh -o BatchMode=yes odin '
  set -Eeuo pipefail
  rm -rf /root/daohua-compute.old
  [[ -d /root/daohua-compute ]] && mv /root/daohua-compute /root/daohua-compute.old
  mv /root/daohua-compute.new /root/daohua-compute
  chmod +x /root/daohua-compute/DaoHua.ComputeNode
  pkill -f "DaoHua[.]ComputeNode" || true'
```

**Windows 发布坑（必踩清单）**：

- **scp 不保留执行权限位**：换目录后必须 `chmod +x /root/daohua-compute/DaoHua.ComputeNode`，否则守护拉起循环报 `Permission denied`（日志在 `~/daohua_compute.log`）。
- Git Bash 会把 `/sdcard/...` 类参数转成 Windows 路径：需要时用 `//sdcard/...` 双斜杠。
- Mac 环境可直接跑 `docs/ops/odin/redeploy-compute.sh`，Windows 按上述三步等价执行。

**odin 验证**：

```bash
ssh odin 'curl -s http://127.0.0.1:8090/healthz; echo; curl -s http://127.0.0.1:8090/readyz'
ssh odin 'grep -a "ComputeNode.Upstream" ~/daohua_compute.log | tail -5'
```

- `healthz` 的 `gameDataFingerprint` 必须与主服 `/readyz`/日志里的指纹一致。
- 日志必须出现「已接入主服 node=odin」；`readyz` 的 `completedJobs` 随补算任务增长即链路健康。
- 指纹不匹配 = 发布两端代码不同源：回到阶段 1 核对 commit，禁止用改配表的方式凑指纹。

## 阶段 5：发布后双端验证

1. 主服：`deploy-prod.sh` 烟测全绿；跳过时手动 `node scripts/smoke-production.mjs https://daohua.yuohira.com`。
2. 主服日志无 `ComputeLinkEndpoint` 拒绝记录（令牌/指纹/上限）：
   ```bash
   ssh yuohira 'sudo docker logs daohua-app --since 10m 2>&1 | grep -a ComputeLinkEndpoint | tail -10'
   ```
3. odin：`readyz` 正常且 `completedJobs` 开始增长（有 pending 补算任务时）。
4. 前端连生产站做一次真实登录冒烟（人工或烟测账号）。

## 阶段 6：存档问题手动修复（发布后）

生产库**没有自动迁移**：主服启动门禁 `PostgresSchema.VerifyAsync` 校验 `platform.schema_version` 最新版本 == 代码常量，不符即 fail-fast 拒绝启动；玩家存档行 `schema_version` 落后同理。修复一律手动，且**先备份、后动手，重大操作必须获得用户明确许可**。

### 症状 A：主服起不来，日志报 schema 版本不匹配

`migrations/` 迁移链手动按序补跑（文件名 `NNNN_vX_to_vY.sql`，按库当前版本向后链式执行，不得按文件序号猜测）：

```bash
# 先看库当前版本
ssh yuohira 'sudo docker exec daohua-db psql -U <app用户> -d daohua -t -c "SELECT max(version) FROM platform.schema_version"'

# 逐个执行落后脚本（脚本自带事务，末尾自写版本号）
ssh yuohira 'sudo docker exec -i daohua-db psql -U <app用户> -d daohua' < migrations/00NN_vX_to_vY.sql
```

### 症状 B：主服能起，个别玩家存档加载失败（schema_version 落后）

对应迁移文件内嵌了存档标签 `UPDATE ... SET schema_version`（`game.player_state_heads` / `player_state_sections` / `platform.section_flush_queue`），按症状 A 同样方式手动执行该 SQL；迁移成立前提见 `migrations/README.md`（新字段可缺省解码）。

### 症状 C：存档漂移不可迁移（语义变更/校验和不符）

1. 立即手动备份：`ssh yuohira 'sudo /opt/daohua/backup.sh'`。
2. **禁止无人值守自动删除**。向用户说明漂移范围（受影响玩家数、字段），获得明确确认后，参照 `scripts/reset-outdated-players.sh` 的口径手动清零受影响玩家的游戏域数据（保留账号/角色名）；无 TTY 的自动化环境一律先停下等用户裁定。
3. 修复后重跑阶段 5 验证。

### 回滚

- 镜像回滚：`deploy-prod.sh --image <上一版@sha256> --compute-image <上一版@sha256>`（旧 digest 从 CCR 或发布记录取）。
- 数据回滚：`/opt/daohua/pitr-restore.sh`（PITR）或从 `/opt/daohua/backups/` 恢复——**必须用户明确许可后执行**。
- odin 回滚：`ssh odin 'rm -rf /root/daohua-compute && mv /root/daohua-compute.old /root/daohua-compute && pkill -f "DaoHua[.]ComputeNode"'`。

## 收尾

1. 更新 `docs/tasks.md` 对应任务状态（含双端版本号/指纹、烟测结果、存档修复记录）。
2. Git 提交按用户指示执行（发布本身不自动提交）。
3. 提醒用户：主服镜像更新后，odin 节点必须同 commit 重发——这是本 skill 存在的原因。
