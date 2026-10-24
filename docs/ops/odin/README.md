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
              export DAOHUA_COMPUTE_UPSTREAM_URLS=wss://daohua.yuohira.com/internal/compute-link
              export DAOHUA_COMPUTE_NODE_NAME=odin
              export DAOHUA_COMPUTE_URLS=http://0.0.0.0:8090   # 显式对外，供局域网健康探测
              export DOTNET_gcServer=0 DOTNET_GCConserveMemory=5 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
              exec ./DaoHua.ComputeNode'          # 崩溃/退出后 10s 内自动拉起
```

- 二进制：`/root/daohua-compute/`（Ubuntu rootfs 内，glibc 自包含构建）
- 链路：主动拨出 `wss://daohua.yuohira.com/internal/compute-link`（compute-link.v1.3）注册进云端动态节点组；NAT 后免隧道，断线自动指数退避重连。`DAOHUA_COMPUTE_UPSTREAM_URLS` 支持分号分隔的多入口列表（多主服多入口时按列表序故障转移，全部失败绕回头部继续退避），旧单值键 `DAOHUA_COMPUTE_UPSTREAM_URL`/`ComputeNode:UpstreamUrl` 在列表未配置时仍可读
- 本地监听：`0.0.0.0:8090`（经 `DAOHUA_COMPUTE_URLS` 显式配置；未显式配置时 ComputeNode 默认只绑 `127.0.0.1:8090`，反向拨入模式无需对外暴露调试口）；健康检查 `GET /healthz`、`/readyz`（无需令牌），`/internal/v1/compute/advance-batch` 仅作调试口
- 日志：`~/daohua_compute.log`（节点 stdout，含 `ComputeNode.Upstream` 链路日志）、`~/daohua_compute_watchdog.log`（守护拉起记录）
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

## 接入主服务

- 接入方式（compute-link.v1.3）：odin 主动拨出 `wss://daohua.yuohira.com/internal/compute-link`，`X-Compute-Token` 鉴权 + hello 握手（nodeName=odin、并发容量、配表指纹）后进入动态节点组；连接即注册、断开即热拔出，在飞批次由主服按租约退回重试。
- 优雅排空（v1.3）：节点停机/重部署先上报 `draining` 帧，主服立即停派新批次、在飞结果照收；主服停机/滚动升级向所有节点下发 `drain` 帧，节点跑完在飞后主动断链并立即重连。两帧均为提示语义，丢帧退化为原断线-租约语义。
- 令牌：设备 `~/daohua_compute_watchdog.sh` 内的 `DAOHUA_COMPUTE_TOKEN` 必须与云端一致。主服支持每节点独立凭证 `ComputeNode:Tokens`（节点名→令牌映射，环境变量形如 `ComputeNode__Tokens__odin`）：odin 的 hello 携带 `nodeName=odin`，命中映射时只认 odin 专属令牌（共享 `COMPUTE_TOKEN` 对其失效），单节点泄露只需轮换该节点映射；未配置映射的节点仍回退共享 `COMPUTE_TOKEN`。仓库内脚本只保留占位值，正式接入时在设备上同步真实令牌（勿入仓库）。
- 设备息屏/断网即离线；节点设计为可水平替换，离线只会导致其批次退回 pending 由其他节点/稍后重试消化，不影响主服正确性。
