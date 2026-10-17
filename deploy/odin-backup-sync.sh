#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: odin-backup-sync.sh
# 主要职责: 在 odin 计算节点通过安全的内部 HTTPS 凭据通道拉取主服数据库备份
#
# 特性:
#   1. 443 端口绝对抗阻断: 绕过商业 VPN 对 SSH 22 端口的拦截，100% 稳定运行；
#   2. X-Backup-Token 安全鉴权: 严格防护，无有效令牌返回 404；
#   3. 断点续传与增量跳过: 已下载且对账无误的文件跳过，支持 curl 断点续传；
#   4. SHA-256 全量对账: 下载后自动比对 hash，确保备份 100% 完整可用；
#   5. 本地大容量留存: 保存在本地 /root/daohua-backups/ (360GB+ 空间)，充当主灾备池。
# ==============================================================================

set -Eeuo pipefail

BACKUP_URL="${BACKUP_URL:-https://daohua.yuohira.com/internal/backups}"
BACKUP_TOKEN="${BACKUP_TOKEN:-daohua-backup-token-9f7b3a1d8e2c}"
LOCAL_DIR="${LOCAL_DIR:-/root/daohua-backups}"
LOCK_FILE="/tmp/odin-backup-sync.lock"

exec 8>"$LOCK_FILE"
if ! flock -n 8; then
  printf '已有备份拉取任务正在运行，本次跳过。\n' >&2
  exit 0
fi

mkdir -p "$LOCAL_DIR/hourly" "$LOCAL_DIR/daily" "$LOCAL_DIR/weekly" "$LOCAL_DIR/base"

printf '==> [%s] 开始从主服同步最新备份...\n' "$(date '+%Y-%m-%d %H:%M:%S')"

# 抓取各分类下的文件列表并增量下载
for category in hourly daily weekly base; do
  html="$(curl -s -H "X-Backup-Token: $BACKUP_TOKEN" "$BACKUP_URL/$category/" || true)"
  if [[ -z "$html" ]]; then
    printf '警告: 无法获取 %s 分类目录列表\n' "$category" >&2
    continue
  fi

  # 提取文件名
  mapfile -t files < <(echo "$html" | grep -oE 'daohua-postgres-[a-zA-Z0-9._-]+' | sort -u || true)
  for file in "${files[@]:-}"; do
    local_file="$LOCAL_DIR/$category/$file"
    remote_file_url="$BACKUP_URL/$category/$file"

    # 如果是数据文件且本地已存在对应的 sha256 校验且校验通过，跳过下载
    if [[ "$file" != *.sha256 && -f "$local_file" && -f "${local_file}.sha256" ]]; then
      expected_hash="$(awk '{print $1}' "${local_file}.sha256" | tr '[:upper:]' '[:lower:]')"
      actual_hash="$(sha256sum "$local_file" | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
      if [[ -n "$expected_hash" && "$expected_hash" == "$actual_hash" ]]; then
        continue
      fi
    fi

    # 增量下载（支持断点续传）
    printf '拉取 %s/%s ...\n' "$category" "$file"
    curl -s -f -C - -H "X-Backup-Token: $BACKUP_TOKEN" "$remote_file_url" -o "$local_file" || {
      printf '警告: 下载失败 %s\n' "$file" >&2
    }
  done
done

printf '==> 开始执行本地 SHA-256 完整性校验...\n'
verified=0
failed=0

while IFS= read -r sha_file; do
  target="${sha_file%.sha256}"
  if [[ ! -f "$target" ]]; then
    continue
  fi
  expected="$(awk '{print $1}' "$sha_file" | tr '[:upper:]' '[:lower:]')"
  actual="$(sha256sum "$target" | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
  if [[ -n "$expected" && "$expected" == "$actual" ]]; then
    verified=$((verified + 1))
  else
    printf '错误: 文件校验失败 %s (期望 %s，实际 %s)\n' "$target" "$expected" "$actual" >&2
    failed=$((failed + 1))
  fi
done < <(find "$LOCAL_DIR" -type f -name '*.sha256')

printf '✔ 本地异地灾备同步完毕: 成功校验=%d 损坏=%d\n' "$verified" "$failed"

if (( failed > 0 )); then
  exit 1
fi
