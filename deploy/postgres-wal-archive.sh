#!/bin/sh
# PostgreSQL archive_command：把 WAL 段和历史文件压缩后原子写入挂载的归档目录。
# 参数由 PostgreSQL 传入：%p（数据目录内的相对路径）和 %f（WAL 文件名）。

set -eu

source_path=${1:?缺少 WAL 源文件路径}
wal_name=${2:?缺少 WAL 文件名}
archive_dir=${WAL_ARCHIVE_DIR:-/var/lib/postgresql/wal-archive}

# 合法归档对象：WAL 段（24 位十六进制）、备份历史文件（24.8.backup）、时间线历史文件（8.history）。
if ! printf '%s\n' "$wal_name" | grep -Eq '^([A-Fa-f0-9]{24}(\.[A-Fa-f0-9]{8}\.backup)?|[A-Fa-f0-9]{8}\.history)$'; then
  echo "非法 WAL 文件名: $wal_name" >&2
  exit 2
fi

if [ ! -f "$source_path" ]; then
  echo "WAL 源文件不存在: $source_path" >&2
  exit 1
fi

mkdir -p "$archive_dir"
destination="$archive_dir/$wal_name.gz"
temporary="$archive_dir/.${wal_name}.tmp.$$"
restored="$archive_dir/.${wal_name}.verify.$$"
trap 'rm -f "$temporary" "$restored"' EXIT HUP INT TERM

verify_existing() {
  # 独立解压保证 gzip 错误不会被 POSIX 管道末端退出码掩盖。
  gzip -dc "$destination" >"$restored"
  cmp -s "$source_path" "$restored" || {
    echo "WAL 归档冲突：目标文件内容不同: $destination" >&2
    exit 1
  }
}
if [ -f "$destination" ]; then
  verify_existing
  exit 0
fi

gzip -c "$source_path" >"$temporary"
chmod 0600 "$temporary"
if command -v sync >/dev/null 2>&1; then
  sync -f "$temporary" 2>/dev/null || sync
fi
if [ -e "$destination" ]; then
  verify_existing
else
  # 硬链接的创建不覆盖既有目标，封住检查后并发归档覆盖的窗口。
  if ! ln "$temporary" "$destination"; then
    verify_existing
  fi
fi
