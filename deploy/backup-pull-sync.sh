#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: backup-pull-sync.sh
# 主要职责: 本地计算节点定时从主服拉取 (Pull) 数据库备份并做 SHA-256 完整性对账
#
# 架构与安全特性:
#   1. 出站主动拉取: 本地节点无需公网 IP 与端口穿透，主动出站经 SSH 连接云端主服；
#   2. 增量断点续传: 基于 rsync 增量同步 dump 与物理归档，不携带 --delete（本地作为长期全量归档池）；
#   3. 双层完整性校验: 同步后自动对新拉取的文件执行 sha256sum 校验；
#   4. 并发排他锁: 文件锁避免重复执行；
#   5. 可配置本地保留: 默认宽松留存，保护本地大容量存储不被无限写穿。
# ==============================================================================

set -Eeuo pipefail

ENV_FILE="${ENV_FILE:-/etc/daohua-backup-sync.env}"
if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a; source "$ENV_FILE"; set +a
fi

REMOTE_HOST="${REMOTE_HOST:-yuohira}"
REMOTE_DIR="${REMOTE_DIR:-/opt/daohua/backups/}"
LOCAL_DIR="${LOCAL_DIR:-/data/daohua-backups}"
LOCK_FILE="${LOCK_FILE:-/tmp/daohua-backup-pull.lock}"
LOCAL_HOURLY_KEEP_DAYS="${LOCAL_HOURLY_KEEP_DAYS:-90}"
SSH_OPTS="${SSH_OPTS:--o BatchMode=yes -o ConnectTimeout=15}"

while (($# > 0)); do
  case "$1" in
    --local-dir|-l) LOCAL_DIR="${2:?缺少本地目录}"; shift 2 ;;
    --remote-host|-r) REMOTE_HOST="${2:?缺少远程主机}"; shift 2 ;;
    --remote-dir) REMOTE_DIR="${2:?缺少远程目录}"; shift 2 ;;
    *) shift ;;
  esac
done

exec 8>"$LOCK_FILE"
if ! flock -n 8; then
  printf '已有备份拉取任务正在运行，本次跳过。\n' >&2
  exit 0
fi

mkdir -p "$LOCAL_DIR"

printf '==> 开始从主服 %s:%s 增量拉取备份至本地 %s\n' "$REMOTE_HOST" "$REMOTE_DIR" "$LOCAL_DIR"

# 1. 增量拉取（不删本地历史，断点续传）
rsync -avz --partial --inplace \
  -e "ssh $SSH_OPTS" \
  "${REMOTE_HOST}:${REMOTE_DIR}" \
  "${LOCAL_DIR}/"

printf '==> 备份同步完成，开始执行 SHA-256 完整性对账...\n'

# 2. 遍历校验所有 .sha256 文件
verified_count=0
failed_count=0

while IFS= read -r sha_file; do
  target_file="${sha_file%.sha256}"
  if [[ ! -f "$target_file" ]]; then
    continue
  fi
  expected_hash="$(awk '{print $1}' "$sha_file" | tr '[:upper:]' '[:lower:]')"
  actual_hash="$(sha256sum "$target_file" | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
  if [[ -n "$expected_hash" && "$expected_hash" == "$actual_hash" ]]; then
    verified_count=$((verified_count + 1))
  else
    printf '警告: 校验失败 %s (期望 %s，实际 %s)\n' "$target_file" "$expected_hash" "$actual_hash" >&2
    failed_count=$((failed_count + 1))
  fi
done < <(find "$LOCAL_DIR" -type f -name '*.sha256')

printf '==> 对账校验完毕: 成功=%d 失败=%d\n' "$verified_count" "$failed_count"

if (( failed_count > 0 )); then
  printf '错误: 存在损坏的备份文件，请检查网络或主服源文件。\n' >&2
  exit 1
fi

# 3. 本地高频 hourly 备份老化（可选，默认 90 天；0 表示永久留存）
if [[ "$LOCAL_HOURLY_KEEP_DAYS" =~ ^[1-9][0-9]*$ && -d "$LOCAL_DIR/hourly" ]]; then
  deleted_hourly=$(find "$LOCAL_DIR/hourly" -type f -mtime +"$LOCAL_HOURLY_KEEP_DAYS" -name '*.dump*' -delete -print | wc -l)
  if (( deleted_hourly > 0 )); then
    printf '==> 已老化清理超过 %d 天的本地旧小时备份共 %d 个文件。\n' "$LOCAL_HOURLY_KEEP_DAYS" "$deleted_hourly"
  fi
fi

printf '✔ 本地异地灾备同步全部完成。\n'
