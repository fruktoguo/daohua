#!/data/data/com.termux/files/usr/bin/bash
# daohua ComputeNode 守护（Termux 层）
# 由 ssh_ubuntu_wrapper.sh 在每次 SSH 连接时确保拉起；
# 节点跑在独立 proot 实例中，不随 SSH 会话 proot 的 --kill-on-exit 被杀。
export PATH=/data/data/com.termux/files/usr/bin:$PATH
export PROOT_NO_SECCOMP=0
LOG=/data/data/com.termux/files/home/daohua_compute_watchdog.log

exec 9>/data/data/com.termux/files/home/.daohua_compute_watchdog.lock
flock -n 9 || exit 0

while true; do
    if ! pgrep -f "DaoHua[.]ComputeNode" >/dev/null 2>&1; then
        echo "[$(date "+%F %T")] compute node not running, starting" >> "$LOG"
        nohup proot-distro login ubuntu -- /bin/bash -c "
            cd /root/daohua-compute
            # 备份拉取令牌与计算节点专属令牌从设备受控 0600 文件注入，不入仓库、不裸露在命令行
            [ -f /etc/daohua-backup-sync.env ] && . /etc/daohua-backup-sync.env && export DAOHUA_BACKUP_TOKEN
            export DAOHUA_COMPUTE_TOKEN=odin-local-test-token-0123456789abcdef
            export DAOHUA_COMPUTE_UPSTREAM_URLS=wss://daohua.yuohira.com/internal/compute-link
            export DAOHUA_COMPUTE_NODE_NAME=odin
            export DAOHUA_COMPUTE_URLS=http://0.0.0.0:8090
            export DAOHUA_BACKUP_STORAGE_DIR=/root/daohua-backups
            export DOTNET_gcServer=0 DOTNET_GCConserveMemory=5 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
            exec /root/daohua-compute/DaoHua.ComputeNode
        " </dev/null >>/data/data/com.termux/files/home/daohua_compute.log 2>&1 &
    fi
    sleep 10
done
