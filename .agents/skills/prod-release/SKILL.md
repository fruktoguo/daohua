---
name: prod-release
description: >-
  正式服快速发布：一次发布同时更新 yuohira 主服务器（linux/amd64 不可变镜像）与 odin 计算节点（linux-arm64 自包含发布），采用三路并发流水线（镜像构建推送 ∥ odin 交叉编译预传 ∥ 远端备份与预检），覆盖门禁、库版本与配表删项前置对账、双层烟测、存档无损修复与回滚。适用于「正式服发布」「发布到生产」「更新主服和 odin」「prod release」等请求。
---

# 正式服快速发布（yuohira 主服 + odin 计算节点）

一次发布必须**成对更新**两端：yuohira 主服（Docker 不可变镜像）与 odin 计算节点（本地交叉发布 `linux-arm64`）。两端代码必须来自**同一 Git commit**——配表内嵌于 `Domain.dll`，指纹不一致时主服以 `GAME_DATA_FINGERPRINT_MISMATCH` 拒绝节点拨入。

## 极速流水线总览（最大化并行）

`.dockerignore` 已排除 `**/bin`、`**/obj`、`**/node_modules`，因此门禁通过后**阶段 1（镜像构建推送）、阶段 2（远端备份）、阶段 4 前半（odin 交叉编译与预上传）必须三路并发启动**，将总耗时压缩一半以上：

```text
阶段 0：本机与远端单回合并发预检 → verify.sh --full 全量门禁
  ↓（门禁全绿后三路并发）
  ├─ 流 A（阶段 1）：docker-build-prod.sh --push 构建并推送成对镜像
  ├─ 流 B（阶段 2）：yuohira 远端 backup.sh + verify-backup.sh（含迁移脚本预置）
  └─ 流 C（阶段 4a）：本地 dotnet publish linux-arm64 → scp 预传至 odin:/root/daohua-compute.new/
  ↓（三路汇合）
阶段 2.5：（仅 schema 升级/配表删项时）停服 → 迁移链 / 存档替换
  ↓（双端并发切换）
  ├─ 阶段 3：deploy-prod.sh 成对 @sha256 更新主服 + 回环/公网双层烟测
  └─ 阶段 4b：odin 原子换目录 + 重启进程
  ↓
阶段 5：双端指纹/接入验证 + 提取 Ops 后台路径 → 收尾登记
```

> **官方一键总控入口**：仓库根目录下的 `scripts/release-prod.sh` 已将上述流水线（三路并发打包推送/备份/交叉编译 → 自动解析 sha256 digest → 自动主服部署与双层烟测 → odin 热重启 → 结果验证）全自动化闭环，日常发布可直接执行 `bash scripts/release-prod.sh`（支持 `--skip-odin`、`--skip-backup`、`--skip-smoke` 等选项）。

---

## 阶段 0：单回合并发预检与最小门禁

**第一回合一次性并发执行以下只读探测**（不要串行摸索）：

```bash
# 1. 本机 Git、Submodule、Docker 与 CCR 凭据检查
git status --short && git submodule status && git rev-parse --short HEAD
docker info >/dev/null && jq -er '.auths["ccr.ccs.tencentyun.com"].auth | length' ~/.docker/config.json

# 2. yuohira 远端状态：当前运行镜像 commit、数据库当前 schema 版本、代码目标 schema 版本
ssh -o BatchMode=yes yuohira '
  sudo docker image inspect "$(sudo docker inspect --format "{{.Image}}" daohua-app)" --format "old_rev={{index .Config.Labels \"org.opencontainers.image.revision\"}}"
  sudo docker exec daohua-db psql -U daohua -d daohua -tAc "SELECT max(version) FROM platform.schema_version"
'
grep -oE 'Version = [0-9]+' src/Server/Clean/PostgresSchema.cs

# 3. odin 远端可达与 Clash 出站网络预检（必须返回 200）
ssh -o BatchMode=yes odin 'curl -s -m 10 -o /dev/null -w "%{http_code}\n" https://daohua.yuohira.com/healthz'
```

### 前置门禁硬规则与 Windows 必踩坑

1. **干净工作区**：`git status --short` 必须为空（`docker-build-prod.sh` 拒绝 dirty worktree）。有未提交改动时先询问用户是否提交，禁止擅自丢弃。
2. **Submodule 无漂移**：`git submodule status` 无 `+`/`-` 前缀。
3. **配表删项前置扫描**（拿到远端 `old_rev` 后立即本地对比）：
   ```bash
   git diff <old_rev>..HEAD -- config/game-data/ | grep -E '^[+-]\s*"id":'
   ```
   若存在被删除的 `equipment.*` / `item.*` / `forge.*` / `building.*` ID，立即只读查询生产库 `economy.player_asset_balances` 与 `game.player_state_sections` 是否有存量玩家持有，**在发布前向用户确认替换映射**（见阶段 6），避免新服启动后玩家加载抛 `DomainException`。
4. **Windows 本地环境三件套**（跑 `verify.sh` 前处理）：
   - **Vite 锁 `esbuild.exe`**：若本地开着 `npm --prefix frontend run dev`，`npm ci` 会报 `EPERM unlink esbuild.exe`；先 `taskkill //F //IM esbuild.exe` 停掉本地前端开发服。
   - **Docker Desktop 与测试库供给**：Docker Desktop 启动后 `daohua-local-db` 容器自动绑定 `127.0.0.1:5432`；Windows 下 `docker ps` 带 `\r\n` 会让 `verify.sh` 内置 `grep -qx` 误判。直接幂等建库并显式传入连接串运行 `verify.sh`：
     ```bash
     docker exec daohua-local-db psql -U daohua -d postgres -c "CREATE DATABASE daohua_market_test;" 2>/dev/null || true
     export DAOHUA_MARKET_TEST_CONNECTION="Host=127.0.0.1;Port=5432;Database=daohua_market_test;Username=daohua;Password=daohua-local-dev;Maximum Pool Size=16;Timeout=5;Command Timeout=30"
     scripts/verify.sh --full
     ```
   - **Windows CCR 登录凭据**：Windows 上 `docker login` 会因 `credsStore: desktop` 或默认回退 `wincred`（`CredWriteW` 报 `Not enough memory resources`）失败。`~/.docker/config.json` 必须保持无 `credsStore` 字段、直接由 `.auths["ccr.ccs.tencentyun.com"].auth` 保存 Base64 凭据。若凭据缺失，让用户在 PowerShell 执行：
     ```powershell
     $p = Read-Host "CCR Password"
     $f = "$HOME\.docker\config.json"; $c = Get-Content $f -Raw | ConvertFrom-Json; $c.auths.'ccr.ccs.tencentyun.com'.auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("100031264062:$p")); [IO.File]::WriteAllText($f, ($c | ConvertTo-Json -Depth 5))
     ```
5. **容量门禁按需，不默认跑**：仅当变更触及存储结构/队列/市场结构（建表改表、分区、索引策略、队列模型）才追加 `scripts/capacity-target.sh`；纯存档标签推进、可缺省字段、配表/功能更新一律只跑 `verify.sh --full` 门禁。

---

## 阶段 1 ∥ 阶段 2 ∥ 阶段 4a：三路并发构建、备份与预传

`verify.sh --full` 全绿后，**在同一回合并发启动以下三个后台任务**：

### 流 A（阶段 1）：构建并推送成对不可变镜像

```bash
scripts/docker-build-prod.sh --push --release-tag sha-<短hash>
```

- 一次构建并推送 `daohua:<tag>`（Server）与 `daohua-compute:<tag>`（ComputeNode）及 `prod` 标签。
- 直接从 `docker push` 输出末尾读取两个 `sha-<短hash>: digest: sha256:<64位hex>`，拼成完整 `@sha256:` 引用供 `deploy-prod.sh` 使用。
- 若本机已构建好镜像仅需补推，用 `--push-existing --release-tag sha-<短hash>` 跳过重编。

### 流 B（阶段 2）：远端发布前备份与校验（生产数据红线）

```bash
ssh -o BatchMode=yes yuohira 'sudo /opt/daohua/backup.sh && sudo /opt/daohua/verify-backup.sh'
```

- 校验逻辑 dump（`pg_restore --list` + SHA256）、物理 base backup 与 WAL 归档完整性。**备份失败立即中止发布**。

### 流 C（阶段 4a）：odin 计算节点交叉编译与预上传

odin 是 aarch64 掌机（Termux + PRoot Ubuntu），必须在本地交叉编译 `linux-arm64` 并预先上传到 `.new` 临时目录（不触碰运行中的 `/root/daohua-compute`）：

```bash
rm -rf /tmp/daohua-compute-odin \
  && dotnet publish src/ComputeNode -c Release -r linux-arm64 --self-contained -o /tmp/daohua-compute-odin --nologo \
  && ssh -o BatchMode=yes odin 'rm -rf /root/daohua-compute.new && mkdir -p /root/daohua-compute.new' \
  && scp -q -r /tmp/daohua-compute-odin/* odin:/root/daohua-compute.new/
```

---

## 阶段 2.5：生产库版本迁移（仅当库版本 < `PostgresSchema.Version` 时执行）

- 若阶段 0 查出生产库 `max(version) == PostgresSchema.Version`：直接跳过本阶段进入阶段 3。
- 若生产库版本落后：**严禁先部署新镜像**（新镜像启动门禁会 fail-fast 并被 `ccr-auto-update.sh` 自动回滚拉黑）。必须先向用户说明迁移范围并获准后，按以下顺序执行：
  1. **预拉取新镜像**（缩短停机窗口）：`ssh yuohira "sudo docker pull <app@sha256> && sudo docker pull <compute@sha256>"`
  2. **停自动更新 timer 与全部写者容器**：
     ```bash
     ssh -o BatchMode=yes yuohira '
       sudo systemctl stop daohua-ccr-auto-update.timer
       sudo docker stop daohua-app
       sudo docker rm -f daohua-persistence-worker 2>/dev/null || true
     '
     ```
     等待 `SELECT count(*) FROM game.player_runtime_leases WHERE lease_until > clock_timestamp()` 归零（优雅停机通常立即释放，最长 45s），再跑一次 `sudo /opt/daohua/backup.sh` 留存静默态快照。
  3. **按 `vX_to_vY` 链式执行落后脚本**：
     - 普通脚本：`sudo docker exec -i daohua-db psql -X -v ON_ERROR_STOP=1 -U daohua -d daohua < migrations/NNNN_vX_to_vY.sql`
     - 若含 `CREATE INDEX CONCURRENTLY`（如 `0026`）：不能在事务块内执行，且若脚本末尾未写 `schema_version` 需补 `INSERT INTO platform.schema_version(version, applied_at) VALUES (Y, now()) ON CONFLICT DO NOTHING;`
     - 若迁移新建了表或 `IDENTITY` 序列：必须执行 `GRANT SELECT, INSERT, UPDATE, DELETE ON <新表> TO daohua_api;`、`GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA ... TO daohua_api, daohua_worker;` 与 `ALTER TABLE <新表> OWNER TO daohua_bootstrap;`（对齐 `deploy/postgres-local-roles.sql`）。
  4. **验证版本对账**：确认 `platform.schema_version`、`game.player_state_heads`、`game.player_state_sections`、`platform.section_flush_queue` 全部到达目标版本。

---

## 阶段 3 ∥ 阶段 4b：主服与 odin 双端并发切换

流 A/B/C（及阶段 2.5）完成后，**并发执行主服部署与 odin 原子换目录**：

### 阶段 3：yuohira 主服受控更新与双层烟测

```bash
# 自动生成合规随机烟测账号（仅允许 [a-zA-Z0-9_]{3,32}，口令 6-24 位，禁止连字符 -）：
SMOKE_ACCOUNT="smoke_$(date +%m%d)_$(openssl rand -hex 3)" SMOKE_PASSWORD="$(openssl rand -hex 8)" SMOKE_REGISTER=1 \
scripts/deploy-prod.sh \
  --image ccr.ccs.tencentyun.com/yuohira/daohua@sha256:<app_digest> \
  --compute-image ccr.ccs.tencentyun.com/yuohira/daohua-compute@sha256:<compute_digest>
```

- 内置完成：远端环境校验 → 资产与 systemd timer 安装 → `ccr-auto-update.sh` 成对更新 → Caddy 校验重载 → 回环 `/readyz` → 回环全量烟测 → 公网 HTTPS/WebSocket 烟测（断言 `/readyz` 返回 `blocked`）。

### 阶段 4b：odin 原子换目录与重启

```bash
ssh -o BatchMode=yes odin '
  set -Eeuo pipefail
  rm -rf /root/daohua-compute.old
  [[ -d /root/daohua-compute ]] && mv /root/daohua-compute /root/daohua-compute.old
  mv /root/daohua-compute.new /root/daohua-compute
  chmod +x /root/daohua-compute/DaoHua.ComputeNode
  pkill -x DaoHua.ComputeNode || true
'
```

**Windows / PRoot 必防坑**：
- `scp` 不保留执行权限位，必须 `chmod +x /root/daohua-compute/DaoHua.ComputeNode`。
- **禁止用 `pkill -f "DaoHua[.]ComputeNode"`**：`ssh odin '...'` 经 PRoot 启动时，PRoot 进程自身的 cmdline 含有整段脚本字符串，`pkill -f` 会把当前 SSH 会话自身杀掉（`proot info: vpid 1: terminated with signal 15`）；必须用精确进程名 `pkill -x DaoHua.ComputeNode`。
- Termux 外层守护 `daohua_compute_watchdog.sh` 会在 10 秒内自动拉起新二进制；守护日志文件在 Termux 主目录 `/data/data/com.termux/files/home/daohua_compute.log`（PRoot 内 `~` 是 `/root`，查日志须写完整路径）。

---

## 阶段 5：发布后双端验证

并发执行以下核对：

```bash
# 1. odin 健康、就绪、指纹与上游接入日志
ssh -o BatchMode=yes odin '
  curl -s http://127.0.0.1:8090/healthz; echo
  curl -s http://127.0.0.1:8090/readyz; echo
  grep -a -A1 "ComputeNode.Upstream" /data/data/com.termux/files/home/daohua_compute.log | tail -6
'

# 2. 主服节点接入日志、异常扫描与动态 Ops 后台路径提取
ssh -o BatchMode=yes yuohira '
  sudo docker logs daohua-app --since 5m 2>&1 | grep -a -A1 -E "ComputeLinkEndpoint|OpsPanel|ops-|fail:" | tail -30
'
```

验收标准：
1. `odin` 的 `gameDataFingerprint` 与主服烟测输出完全一致，且 `ComputeLinkEndpoint` 日志出现 `计算节点接入 ... name=odin`（注意：`deploy-prod.sh` 重载 Caddy 时会使 `odin` 短暂断线一次，30s 内会自动重连拨入）。
2. 主服日志无 `fail:` / `DomainException`。
3. 若主服启用了随机后台路径（`/ops-<16hex>`），从启动日志提取并在总结中告知用户。

---

## 阶段 6：存档配表漂移无损修复与回滚预案

### 存档内含已移除配表 ID（如旧装备/旧配方改名）

当配表删除或重命名了某装备/物品 ID（如 `equipment.xingshan-boots` 改为 `equipment.tiepi-guard`），存量玩家加载会抛 `DomainException`（`LoadOwnedAsync` 异常块会自动回滚内存态并归还租约，不留脏缓存）。获用户确认替换关系后，可在远端通过 `node` + `psql` 单事务在线无损替换：

1. `LOCK TABLE game.player_runtime_leases, game.player_state_heads, game.player_state_sections, platform.section_flush_queue, economy.player_asset_balances, economy.transaction_lines IN SHARE ROW EXCLUSIVE MODE;`
2. 校验受影响玩家无活跃租约、无待刷盘队列行；
3. 更新 `economy.player_asset_balances` 与 `economy.transaction_lines` 的 `asset_id`；
4. 解码受影响玩家的 `game.player_state_sections`（`codec` 为 `json` 或 `br-json` Brotli），替换 JSON 文本中的旧 ID，重算分段 `raw_bytes`、`stored_bytes`、`checksum = sha256(raw)`，再按 `kind:version:checksum` 字典序 `|` 拼接重算 `game.player_state_heads.checksum` 并 `COMMIT`。

### 回滚路径

- **镜像回滚**（仅限未执行跨版本数据库迁移时）：`scripts/deploy-prod.sh --image <旧app@sha256> --compute-image <旧compute@sha256>`。
- **odin 回滚**：`ssh odin 'rm -rf /root/daohua-compute && mv /root/daohua-compute.old /root/daohua-compute && pkill -x DaoHua.ComputeNode || true'`。
- **数据回滚**（已执行不可逆 schema 迁移时）：从阶段 2 生成的 `/opt/daohua/backups/hourly/*.dump` 或 `/opt/daohua/pitr-restore.sh` 恢复——**必须获得用户明确许可后执行**。

## 收尾

1. 更新 `docs/tasks.md` 对应任务状态（双端版本 tag、digest、配表指纹、烟测与迁移记录）。
2. Git 提交按用户指示执行（发布本身不自动提交）。
