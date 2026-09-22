# odin 安卓辅助计算节点运维手册

odin 是一台 AYN Odin 2 安卓掌机（aarch64、8 核、约 14GB 内存），局域网地址 `192.168.1.144`，通过 Termux + PRoot Ubuntu 提供 Linux 环境，定位为《道化太初》的**高延迟冷挂机辅助计算节点**（ComputeNode）。

本目录：

- `README.md`：设备档案、环境特性、运维操作与故障排查（本文件）
- `daohua_compute_watchdog.sh`：odin 上实际运行的 Termux 层守护脚本（与设备内 `~/daohua_compute_watchdog.sh` 同步）
- `redeploy-compute.sh`：本机一键重新发布 ComputeNode 到 odin

## 连接与拓扑

```bash
ssh odin    # ~/.ssh/config: HostName 192.168.1.144, Port 8022, User u0_a118
```

SSHD 由 Termux 层（bionic）提供，`ForceCommand = ~/ssh_ubuntu_wrapper.sh`：**每条 SSH 连接都会新建一个 `proot --kill-on-exit` 实例**进入 Ubuntu rootfs。

```text
Mac --ssh--> Termux sshd(8022) --ForceCommand--> 会话级 proot(--kill-on-exit) --> bash(本机操作面)
Termux 层常驻: sshd / postgres:15432 / ddns-go:9876 / frpc / cloudflared / 各 watchdog bash
独立 proot 实例: caddy(80/443) / daojie 主服(13001) / daohua-compute(8090)
```

## 关键环境约束（踩坑记录）

1. **会话 proot 退出即杀全部子进程**：`--kill-on-exit` 使 `nohup`、`setsid`、tmux 都无法让进程跨会话存活。常驻服务必须由 **Termux 层进程**拉起独立 proot 实例（参照 `frpc_watchdog.sh` / `start_all_daojie.sh` 模式）。
2. **.NET Server GC 无法启动**：服务器 GC 默认预留 256GiB 虚拟地址空间，PRoot/Android 内核拒绝 → 必须 `DOTNET_gcServer=0`（工作站 GC）；配 `DOTNET_GCConserveMemory=5` 进一步压低内存。
3. **`proot-distro login` 不透传宿主环境变量**：`DAOHUA_COMPUTE_TOKEN` 等必须在**内层** `bash -c` 里 export。
4. **dockerd 已损坏**（2026-08-20 起 proot 断言崩溃，容器列表为空，docker run 挂死）；containerd 仍在但不可用。不要在 odin 上走 Docker 方案。
5. **alpine/musl 与 glibc**：Ubuntu proot 是 glibc，本地 `dotnet publish -r linux-arm64 --self-contained` 产物可直接运行（免装运行时）。
6. `pgrep -f` 会匹配到**执行命令本身所在的会话 proot 命令行**，探测串要写成 `DaoHua[.]ComputeNode` 之类防自匹配形式。

## 当前部署形态

```text
~/ssh_ubuntu_wrapper.sh（每条 SSH 连接触发）
  └─ setsid ~/daohua_compute_watchdog.sh        # Termux 层守护，flock 单例，PPID=1 常驻
        └─ nohup proot-distro login ubuntu -- bash -c '
              cd /root/daohua-compute
              export DAOHUA_COMPUTE_TOKEN=<见设备文件，勿入仓库>
              export DOTNET_gcServer=0 DOTNET_GCConserveMemory=5 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
              exec ./DaoHua.ComputeNode'          # 崩溃/退出后 10s 内自动拉起
```

- 二进制：`/root/daohua-compute/`（Ubuntu rootfs 内，glibc 自包含构建）
- 监听：`0.0.0.0:8090`；健康检查 `GET /healthz`、`/readyz`（无需令牌）
- 日志：`~/daohua_compute.log`（节点 stdout）、`~/daohua_compute_watchdog.log`（守护拉起记录）
- wrapper 备份：`~/ssh_ubuntu_wrapper.sh.bak-daohua`

## 常用运维操作

```bash
# 状态：进程 + 端口 + 健康（Mac 侧直接可用）
ssh odin 'pgrep -fa "daohua_compute_watchdog"; ss -tln | grep 8090'
curl -s http://192.168.1.144:8090/healthz
curl -s http://192.168.1.144:8090/readyz

# 看日志
ssh odin 'tail -50 /data/data/com.termux/files/home/daohua_compute.log'

# 手动重启（杀掉后守护 10s 内自动拉起；匹配串含 . 防自匹配）
ssh odin 'pkill -f "DaoHua[.]ComputeNode"'

# 完全停用：先杀守护，再杀节点
ssh odin 'pkill -f daohua_compute_watchdog; pkill -f "DaoHua[.]ComputeNode"'

# 恢复：任意一次 ssh 连接即触发 wrapper 钩子重新拉起守护与节点
ssh odin true
```

## 重新发布新版本

```bash
docs/ops/odin/redeploy-compute.sh
```

脚本在 Mac 上完成 `linux-arm64` 自包含发布、scp 到 `/root/daohua-compute.new`、原子换目录并触发守护重启。运行中的二进制不可覆写（ETXTBSY），故必须走「新目录 → mv → 杀进程 → 守护拉起」路径。

## 接入主服务的缺口

- 当前令牌为测试值 `odin-local-test-token-0123456789abcdef`（在设备 `~/daohua_compute_watchdog.sh` 内）；正式接入前生成真实 `COMPUTE_TOKEN` 并同步两侧。
- odin 在内网 NAT 后，云端主服务无法直连 `192.168.1.144:8090`；要让云端把 `compute_jobs` 派发到 odin，需借助设备上已有的 frpc/cloudflared 通道把 8090 以受控方式暴露（`X-Compute-Token` 仍是唯一鉴权边界），或等待双向隧道方案落地。
- 设备息屏/断网即离线；节点设计为可水平替换，离线只会导致派发失败重试，不影响主服正确性。
